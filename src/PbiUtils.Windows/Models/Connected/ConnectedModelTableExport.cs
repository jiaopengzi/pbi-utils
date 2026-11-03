// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\ConnectedModelTableExport.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复用宿主已注册目标和认证进行固定单表流式查询, 不接受任意 DAX 或连接地址.

using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Infrastructure.Models;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AnalysisServices.AdomdClient;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using TOM = Microsoft.AnalysisServices.Tabular;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PbiUtils.Windows.Models.Connected;

public sealed partial class ConnectedModelReader
{
    /// <summary>后台打开原注册模型的只读数据会话并完整消费, SDK、凭据及游标不离开该范围.</summary>
    /// <param name="snapshot">共享服务刚核验的在线快照; Service 可见架构限制保持, 不冒充完整 TOM 定义.</param>
    /// <param name="table">快照中唯一实际表名, 查询由后端固定生成.</param>
    /// <param name="rowLimit">正整数模型侧抽样上限, null为全部行; 不指定排序.</param>
    /// <param name="consume">同步流式写入器, 不得保存游标或发起任意查询.</param>
    /// <param name="token">查询生成/排版、每次同步 SDK 前后及逐行消费时检查, 不承诺立即中断内部 SDK.</param>
    /// <returns>写入器实际消费的数据行数; 任一失败均释放 SDK 资源.</returns>
    /// <exception cref="AppException">注册、实例、基线、认证、结构或数据读取失败.</exception>
    public async Task<long> ReadAsync(ProjectSnapshot snapshot, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
    {
        if (!targets.TryGetValue(snapshot.ProjectPath, out var target)) throw new AppException("MODEL_DISCONNECTED", "模型连接已断开", 3);
        ModelTableExportService.ValidateSnapshot(snapshot, new(snapshot.ProjectId, snapshot.BaselineId, [table], "csv", RowLimit: rowLimit));
        if (target.Endpoint is not null) await VerifyLocalAsync(snapshot.ProjectPath, token);
        var service = target.ServiceModelId is not null ? await cloud!.ResolveAsync(target.ServiceModelId, token) : null;
        if (service?.Server.StartsWith("pbiazure://", StringComparison.Ordinal) == true)
            return await Task.Run(() => new PowerBiServiceTableDataReader(schemaReader ?? new(), serviceTableData, log)
                .Read(snapshot, service, table, target.ServiceModelId, rowLimit, consume, token), token);
        var ssas = service is null && target.Endpoint is null && !target.Server.Contains("://", StringComparison.Ordinal);
        if (ssas) await SsasConnection.ProbeAsync(target.Server, log, token);
        var count = await Task.Run(() => WindowsNetworkIdentity.Run(ssas ? target.Credential : null, log, () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using var server = new TOM.Server();
                var settings = ExportConnection(target, service, ssas);
                if (service is not null) server.AccessToken = new(service.AccessToken, service.ExpiresOn, null);
                server.Connect(settings.ConnectionString);
                token.ThrowIfCancellationRequested();
                var catalog = service?.Database ?? target.Database;
                var databases = server.Databases.Cast<TOM.Database>().Where(d => service?.DatabaseId is { Length: > 0 } databaseId ? d.ID == databaseId : catalog.Length == 0 || d.ID == catalog || d.Name == catalog).ToArray();
                if (databases.Length != 1) throw new AppException("MODEL_NOT_UNIQUE", "未找到唯一可读模型, 请重新连接", 3);
                var database = databases[0];
                AssertExportBaseline(snapshot, target, service, database);
                var selectedTable = snapshot.Model.Tables.Single(t => t.Name == table);
                var columns = selectedTable.Columns.Where(c => c.Kind != "RowNumber").Select(c => c.Name).ToArray();
                // SSAS 初次 TOM 连接按 ID/名称定位; 数据会话使用实际数据库名称, 不将不受 SDK 支持的 ID 作为 catalog.
                settings["Initial Catalog"] = database.Name;
                settings["Timeout"] = 120;
                using var connection = new AdomdConnection(settings.ConnectionString);
                if (service is not null) connection.AccessToken = new(service.AccessToken, service.ExpiresOn, null);
                token.ThrowIfCancellationRequested(); connection.Open(); token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = ModelTableExportQuery.Create(selectedTable, rowLimit, token);
                command.CommandTimeout = 120;
                token.ThrowIfCancellationRequested();
                using var rows = command.ExecuteReader();
                token.ThrowIfCancellationRequested();
                if (rows.FieldCount != columns.Length) throw new AppException("EXPORT_SCHEMA_CHANGED", "导出字段结构已变化, 请刷新后重试", 3);
                var result = consume(new ExportRows(rows, columns, token), token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (AppException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                token.ThrowIfCancellationRequested();
                SsasConnection.LogFailure(log, "TableExport", error);
                throw new AppException("EXPORT_QUERY_FAILED", "无法读取表数据, 请检查连接、权限及模型处理状态", 4);
            }
        }), token);
        if (target.Endpoint is not null) await VerifyLocalAsync(snapshot.ProjectPath, token);
        return count;
    }

    /// <summary>构造仅供当前短生命周期 SDK 使用的连接参数, 凭据不进入连接串或日志.</summary>
    private static DbConnectionStringBuilder ExportConnection(Target target, PowerBiServiceTarget? service, bool ssas)
    {
        if (ssas) return new() { ConnectionString = SsasConnection.ConnectionString(new(target.Server, target.Database)) };
        var settings = new DbConnectionStringBuilder { ["Data Source"] = service?.Server ?? target.Server, ["Connect Timeout"] = 5, ["Timeout"] = 10, ["Application Name"] = "pbi-utils", ["Persist Security Info"] = false };
        if (service is not null) { settings["Integrated Security"] = "ClaimsToken"; settings["Identity Provider"] = service.IdentityProvider; }
        var catalog = service?.Database ?? target.Database;
        if (catalog.Length > 0) settings["Initial Catalog"] = catalog;
        return settings;
    }

    /// <summary>按与正常读取相同的完整身份和定义指纹复核新 SDK 会话, 不按表列形状猜测目标.</summary>
    private static void AssertExportBaseline(ProjectSnapshot snapshot, Target target, PowerBiServiceTarget? service, TOM.Database database)
    {
        var account = service?.AccountId ?? target.Credential?.Use((name, _) => name.ToUpperInvariant());
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((service?.Server ?? target.Server) + "|" + database.ID + "|" + target.Endpoint?.EngineStarted + "|" + account)));
        var model = ModelSnapshotMapper.Map(database, target.Endpoint is { } local ? DesktopModelName(local.Title) : service?.ModelName ?? database.Name);
        var baseline = target.Endpoint is not null ? DesktopDefinitionFingerprint(database) : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(model)));
        if (identity != snapshot.ProjectId || baseline != snapshot.BaselineId) throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重新导出", 3);
    }

    /// <summary>借用游标不拥有其生命周期; 每次同步读取前后检查取消, 不在令牌回调中跨线程销毁 SDK.</summary>
    private sealed class ExportRows(AdomdDataReader rows, IReadOnlyList<string> columns, CancellationToken token) : IModelTableRows
    {
        public IReadOnlyList<string> Columns => columns;
        public bool Read() { token.ThrowIfCancellationRequested(); var available = rows.Read(); token.ThrowIfCancellationRequested(); return available; }
        public object? GetValue(int ordinal) => rows.IsDBNull(ordinal) ? null : rows.GetValue(ordinal);
    }
}
