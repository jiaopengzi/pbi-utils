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
    /// <summary>只读取固定存储或架构行集; 未提供字段保持未知, 数据量变化不会改变模型定义指纹.</summary>
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
            return await ReadBoundStatisticsAsync(snapshot, (session, limited, cancellation) => ReadMetadataStatistics(snapshot, session, limited, cancellation), false, token);
        });

    /// <summary>只读取固定元数据; 非Import驻留部分不冒充整表统计, 明确计数另由用户所选表入口触发.</summary>
    private static IReadOnlyList<ModelTableStatistics> ReadMetadataStatistics(ProjectSnapshot snapshot, IPowerBiTableStatisticsSession session, bool limited, CancellationToken token)
    {
        var names = snapshot.Model.Tables.Select(table => table.Name).ToArray();
        if (limited)
        {
            using var dimensions = session.Read("MDSCHEMA_DIMENSIONS", token);
            return ModelTableStatisticsParser.FromDimensions(names, dimensions, token);
        }
        using var storage = session.Read("DISCOVER_STORAGE_TABLES", token);
        token.ThrowIfCancellationRequested();
        using var columns = session.Read("DISCOVER_STORAGE_TABLE_COLUMNS", token);
        token.ThrowIfCancellationRequested();
        using var segments = session.Read("DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS", token);
        token.ThrowIfCancellationRequested();
        var unavailable = session.UnavailableTables.ToHashSet(StringComparer.Ordinal);
        return ModelTableStatisticsParser.FromStorage(names, storage, columns, segments, token)
            .Select(table => unavailable.Contains(table.Table) ? table with { RowCount = null, SizeBytes = null } : table).ToArray();
    }

    /// <summary>元数据与显式计数共用唯一目标、前后基线、凭据范围和SDK生命周期, 不建立第二套身份验证通道.</summary>
    /// <param name="snapshot">同一宿主操作门闩内的权威快照.</param>
    /// <param name="consume">仅消费已绑定短会话, 不保存SDK资源或指定连接.</param>
    /// <param name="queryCount">是否为用户明确发起的精确计数, 仅控制安全错误分类.</param>
    /// <param name="token">原请求总预算和取消, 不按批次重置.</param>
    /// <returns>前后核验一致且全部处理完成的结果; 任何失败不发布部分结果.</returns>
    private async Task<ModelTableStatisticsResult> ReadBoundStatisticsAsync(ProjectSnapshot snapshot,
        Func<IPowerBiTableStatisticsSession, bool, CancellationToken, IReadOnlyList<ModelTableStatistics>> consume, bool queryCount, CancellationToken token)
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
                    return consume(session, limited, token);
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
                SsasConnection.LogFailure(log, queryCount ? "TableRowCounts" : "TableStatistics", error);
                throw queryCount
                    ? new AppException("MODEL_ROW_COUNT_FAILED", "无法读取精确行数, 请检查连接、权限及模型处理状态", 4)
                    : new AppException("MODEL_STATISTICS_FAILED", "无法读取表统计, 请检查连接和权限", 4);
            }
        }), token);
        token.ThrowIfCancellationRequested();
        AssertStatisticsSnapshot(snapshot, await ReadAsync(snapshot.ProjectPath, token));
        if (target.Endpoint is not null) await VerifyLocalAsync(snapshot.ProjectPath, token);
        token.ThrowIfCancellationRequested();
        return new(snapshot.ProjectId, snapshot.BaselineId, tables);
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
        // DirectQuery、Dual、DirectLake等可能只返回驻留部分; 不为显示整表统计执行COUNTROWS或强制装载.
        var unavailable = model.Tables.Where(table => table.Partitions.Count == 0 || table.Partitions.Any(partition =>
            (partition.Mode == TOM.ModeType.Default ? model.DefaultMode : partition.Mode) != TOM.ModeType.Import)).Select(table => table.Name).ToArray();
        return new ModelTableStatisticsSession(settings.ConnectionString, service, unavailable, log);
    }
}
