// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\ConnectedModelTableStatistics.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 对宿主已绑定目标读取独立统计, 前后核验身份和定义基线, 不把动态统计写入模型指纹.

using PbiUtils.Windows.Models.Statistics;
using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Windows.Models.Connected;

public sealed partial class ConnectedModelReader
{
    /// <summary>读取固定存储统计, 并为 Power BI Service 自动查询当前账号可见的实际行数.</summary>
    /// <param name="snapshot">共享操作门闩内取得的当前权威快照.</param>
    /// <param name="token">每个同步 SDK 边界及遍历时检查, 不承诺立即中断 SDK 内部调用.</param>
    /// <returns>绑定原身份和基线的独立统计; 离线工程不执行文件或网络读取.</returns>
    /// <exception cref="AppException">连接已失效、身份或基线变化、认证失败、数据越界或统计执行失败.</exception>
    /// <exception cref="OperationCanceledException">取消后拒绝发布统计并释放当前会话.</exception>
    public Task<ModelTableStatisticsResult> ReadStatisticsAsync(ProjectSnapshot snapshot, CancellationToken token) =>
        BusinessLog.RunAsync<ModelTableStatisticsResult>(log, "ConnectedModelReader.TableStatistics", async () =>
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal))
                return new(snapshot.ProjectId, snapshot.BaselineId, snapshot.Model.Tables.Select(table => new ModelTableStatistics(table.Name, null, null)).ToArray());
            return await ReadBoundStatisticsAsync(snapshot, token);
        });

    /// <summary>只读取固定存储元数据, 非 Import 驻留部分不冒充整表统计.</summary>
    /// <param name="names">已核验的当前模型表名.</param>
    /// <param name="session">当前已打开的统计会话.</param>
    /// <param name="limited">是否需要通过固定架构行集核验分区模式.</param>
    /// <param name="token">同步 SDK 边界与解析过程的取消令牌.</param>
    /// <returns>已确认为 Import 的表统计, 或元数据缺失时的未知值.</returns>
    private static IReadOnlyList<ModelTableStatistics> ReadStorageStatistics(string[] names, IPowerBiTableStatisticsSession session, bool limited, CancellationToken token)
    {
        using var storage = session.Read("DISCOVER_STORAGE_TABLES", token);
        token.ThrowIfCancellationRequested();
        if (limited)
        {
            if (storage is not { Rows.Count: > 0 }) return [];
            using var tables = session.Read("TMSCHEMA_TABLES", token);
            if (tables is not { Rows.Count: > 0 }) return [];
            using var partitions = session.Read("TMSCHEMA_PARTITIONS", token);
            using var model = partitions?.Rows.Count > 0 ? session.Read("TMSCHEMA_MODEL", token) : null;
            var imported = ModelTableStatisticsParser.ImportTables(tables, partitions, model, token);
            var importedNames = names.Where(imported.Contains).ToArray();
            return importedNames.Length == 0 ? [] : ReadStorage(importedNames);
        }
        var unavailable = session.UnavailableTables.ToHashSet(StringComparer.Ordinal);
        return [.. ReadStorage(names)
            .Select(table => unavailable.Contains(table.Table) ? table with { RowCount = null, SizeBytes = null } : table)];

        // 只聚合固定存储组成; 统计不改变当前模型的定义或权限.
        IReadOnlyList<ModelTableStatistics> ReadStorage(IReadOnlyList<string> tables)
        {
            using var columns = session.Read("DISCOVER_STORAGE_TABLE_COLUMNS", token);
            using var segments = session.Read("DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS", token);
            return ModelTableStatisticsParser.FromStorage(tables, storage, columns, segments, token);
        }
    }

    /// <summary>在同一会话读取存储元数据及 Service 行数, 并在统计前后核验目标与基线.</summary>
    /// <param name="snapshot">同一宿主操作门闩内的权威快照.</param>
    /// <param name="token">原请求总预算和取消, 不按批次重置.</param>
    /// <returns>前后核验一致且全部处理完成的结果; 任何失败不发布部分结果.</returns>
    private async Task<ModelTableStatisticsResult> ReadBoundStatisticsAsync(ProjectSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!targets.TryGetValue(snapshot.ProjectPath, out var target)) throw new AppException("MODEL_DISCONNECTED", "模型连接已断开", 3);
        AssertStatisticsSnapshot(snapshot, await ReadAsync(snapshot.ProjectPath, token));
        if (target.Endpoint is not null) await VerifyLocalAsync(snapshot.ProjectPath, token);
        var service = target.ServiceModelId is not null ? await cloud!.ResolveAsync(target.ServiceModelId, token) : null;
        var limited = service?.Server.StartsWith("pbiazure://", StringComparison.Ordinal) == true;
        if (limited)
        {
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(service!.Server + "|" + service.DatabaseId + "|" + service.AccountId)));
            if (identity != snapshot.ProjectId) throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重新读取统计", 3);
        }
        var ssas = service is null && target.Endpoint is null && !target.Server.Contains("://", StringComparison.Ordinal);
        if (ssas) await SsasConnection.ProbeAsync(target.Server, log, token);
        var tables = await Task.Run(() => WindowsNetworkIdentity.Run(ssas ? target.Credential : null, log, () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var session = tableStatisticsSession?.Invoke() ?? CreateStatisticsSession(snapshot, target, service, ssas, token);
                Exception? failure = null;
                try
                {
                    session.Open(); token.ThrowIfCancellationRequested();
                    return ReadTableStatistics(snapshot, session, limited, service is not null, token);
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    try { session.Dispose(); }
                    catch (Exception cleanup) when (failure is not null) { SsasConnection.LogFailure(log, "TableStatisticsCleanup", cleanup); }
                }
            }
            catch (AppException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                token.ThrowIfCancellationRequested();
                SsasConnection.LogFailure(log, "TableStatistics", error);
                throw new AppException("MODEL_STATISTICS_FAILED", "无法读取表统计, 请检查连接、权限及模型处理状态", 4);
            }
        }), token);
        token.ThrowIfCancellationRequested();
        AssertStatisticsSnapshot(snapshot, await ReadAsync(snapshot.ProjectPath, token));
        if (target.Endpoint is not null) await VerifyLocalAsync(snapshot.ProjectPath, token);
        token.ThrowIfCancellationRequested();
        return new(snapshot.ProjectId, snapshot.BaselineId, tables);
    }

    /// <summary>从同一会话读取当前模型表统计, Service 存储明细超限时仍查询实际行数.</summary>
    /// <param name="snapshot">已核验的当前模型快照.</param>
    /// <param name="session">绑定当前账号的统计会话.</param>
    /// <param name="limited">是否需要通过架构行集核验 Import 模式.</param>
    /// <param name="service">是否连接 Power BI Service.</param>
    /// <param name="token">统计与计数共用的取消令牌.</param>
    /// <returns>当前模型的完整统计结果, 无法读取存储大小时保留未知.</returns>
    /// <exception cref="AppException">会话、统计或实际行数查询失败.</exception>
    private static IReadOnlyList<ModelTableStatistics> ReadTableStatistics(ProjectSnapshot snapshot,
        IPowerBiTableStatisticsSession session, bool limited, bool service, CancellationToken token)
    {
        var names = snapshot.Model.Tables.Select(table => table.Name).ToArray();
        IReadOnlyList<ModelTableStatistics> storage;
        try { storage = ReadStorageStatistics(names, session, limited, token); }
        catch (AppException error) when (service && error.Code == "MODEL_STATISTICS_LIMIT") { storage = []; }
        return service ? ReadServiceStatistics(snapshot, storage, session, token) : storage;
    }

    /// <summary>分批实际统计 Service 中所有可查询表, 保留已核实的存储大小, 任一批失败不交付部分计数.</summary>
    /// <param name="snapshot">已核验的完整模型或受限架构快照.</param>
    /// <param name="storage">当前会话读取到的存储统计.</param>
    /// <param name="session">绑定当前账号的统计会话.</param>
    /// <param name="token">同一请求的取消令牌.</param>
    /// <returns>按模型顺序返回实际计数或已确认的不适用原因.</returns>
    /// <exception cref="AppException">精确计数结果缺失、歧义或无效.</exception>
    internal static IReadOnlyList<ModelTableStatistics> ReadServiceStatistics(ProjectSnapshot snapshot,
        IReadOnlyList<ModelTableStatistics> storage, IPowerBiTableStatisticsSession session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sizes = storage.ToDictionary(table => table.Table, table => table.SizeBytes, StringComparer.Ordinal);
        var counts = new Dictionary<string, ModelTableStatistics>(StringComparer.Ordinal);
        // 受限架构可能省略真实列; 只有完整定义才能确认表没有可查询的数据列.
        var queryable = snapshot.Model.Tables.Where(table => snapshot.Connection?.IsLimited == true
            || table.Columns.Any(column => column.Kind != "RowNumber")).Select(table => table.Name);
        foreach (var batch in queryable.Chunk(ModelTableRowCountQuery.MaximumTables))
        {
            token.ThrowIfCancellationRequested();
            using var rows = session.ReadRowCounts(batch, token);
            foreach (var count in ModelTableStatisticsParser.FromRowCounts(batch, rows, token)) counts.Add(count.Table, count);
        }
        token.ThrowIfCancellationRequested();
        return snapshot.Model.Tables.Select(table =>
        {
            var size = sizes.GetValueOrDefault(table.Name);
            return counts.TryGetValue(table.Name, out var count)
                ? count with { SizeBytes = size }
                : new ModelTableStatistics(table.Name, null, size, RowCountUnavailableReason: "no_queryable_columns");
        }).ToArray();
    }

    /// <summary>只比较模型身份和定义或可见架构基线, 不把本次统计结果加入比较或写入授权.</summary>
    private static void AssertStatisticsSnapshot(ProjectSnapshot expected, ProjectSnapshot actual)
    {
        if (expected.ProjectPath != actual.ProjectPath || expected.ProjectId != actual.ProjectId || expected.BaselineId != actual.BaselineId || !ModelConnectionInfo.SameTarget(expected.Connection, actual.Connection))
            throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重新读取统计", 3);
    }

    /// <summary>在同一平台身份范围内解析实际数据库名称并建立只读会话, TOM 和凭据不离开后台范围.</summary>
    private ModelTableStatisticsSession CreateStatisticsSession(ProjectSnapshot snapshot, Target target, PowerBiServiceTarget? service, bool ssas, CancellationToken token)
    {
        if (service?.Server.StartsWith("pbiazure://", StringComparison.Ordinal) == true)
            return new ModelTableStatisticsSession(PowerBiSchemaReader.ConnectionString(service, includeCredential: true), null, logger: log);
        using var server = new TOM.Server();
        var settings = ExportConnection(target, service, ssas);
        if (service is not null) server.AccessToken = new(service.AccessToken, service.ExpiresOn, null);
        server.Connect(settings.ConnectionString); token.ThrowIfCancellationRequested();
        var catalog = service?.Database ?? target.Database;
        var databases = server.Databases.Cast<TOM.Database>().Where(database => service?.DatabaseId is { Length: > 0 } databaseId
            ? database.ID == databaseId : catalog.Length == 0 || database.ID == catalog || database.Name == catalog).ToArray();
        if (databases.Length != 1) throw new AppException("MODEL_NOT_UNIQUE", "未找到唯一可读模型, 请重新连接", 3);
        AssertExportBaseline(snapshot, target, service, databases[0]);
        settings["Initial Catalog"] = databases[0].Name;
        var model = databases[0].Model;
        // DirectQuery、Dual、DirectLake 等可能只返回驻留部分, 不将其作为整表存储统计.
        var unavailable = model.Tables.Where(table => table.Partitions.Count == 0 || table.Partitions.Any(partition =>
            (partition.Mode == TOM.ModeType.Default ? model.DefaultMode : partition.Mode) != TOM.ModeType.Import)).Select(table => table.Name).ToArray();
        return new ModelTableStatisticsSession(settings.ConnectionString, service, unavailable, log);
    }
}
