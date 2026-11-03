// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Statistics\ModelTableStatisticsSession.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅查询固定统计行集和显式所选表计数的有界ADOMD会话, 不提供任意查询或业务行值访问.

using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Application.Connections;
using System.Data;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Models.Statistics;

/// <summary>绑定可信目标的短生命周期统计会话, 仅支持固定元数据与显式所选表计数.</summary>
public interface IPowerBiTableStatisticsSession : IDisposable
{
    /// <summary>非Import或无法确定驻留完整范围的表, 不把局部驻留统计当作整表总量.</summary>
    IReadOnlyList<string> UnavailableTables => [];
    /// <summary>打开当前后端已绑定目标, 认证失败须抛出而非返回空统计.</summary>
    void Open();
    /// <summary>读取固定行集并在内部释放游标; 元数据未提供返回 null, 取消或连接错误须抛出.</summary>
    /// <param name="schema">仅允许四个编译时固定行集名称, 不接受 DAX 或地址.</param>
    /// <param name="token">同步边界和逐行读取的取消令牌.</param>
    /// <returns>调用方负责释放的有界元数据表, 或服务未提供该行集时的 null.</returns>
    DataTable? Read(string schema, CancellationToken token);
    /// <summary>执行最多50张已核验表的固定COUNTROWS, 权限、认证或协议失败必须抛出, 不返回未知或0.</summary>
    /// <param name="tables">本批实际表名, 已由权威快照验证; 不接受查询正文.</param>
    /// <param name="token">同步边界和逐行读取取消令牌.</param>
    /// <returns>调用方负责释放的完整计数行集, 包含固定别名与精确标量.</returns>
    DataTable ReadRowCounts(IReadOnlyList<string> tables, CancellationToken token);
}

/// <summary>仅供已经核验的当前模型使用, 密钥连接串与访问令牌不回传或记录.</summary>
internal sealed class ModelTableStatisticsSession(string connectionString, PowerBiServiceTarget? service, IReadOnlyList<string>? unavailableTables = null, ILogger? logger = null) : IPowerBiTableStatisticsSession
{
    private const int MaximumRows = 250000;
    private AdomdConnection? connection;
    private string? pendingConnectionString = connectionString;
    private PowerBiServiceTarget? pendingService = service;
    private readonly ILogger log = logger ?? NullLogger.Instance;
    public IReadOnlyList<string> UnavailableTables => unavailableTables ?? [];

    /// <summary>pbiazure 已在构造前初始化 ClaimsToken, 其他服务连接仅使用 SDK AccessToken.</summary>
    public void Open()
    {
        try
        {
            connection = new(pendingConnectionString);
            if (pendingService is { } target) connection.AccessToken = new(target.AccessToken, target.ExpiresOn, null);
            connection.Open();
        }
        finally { pendingConnectionString = null; pendingService = null; }
    }

    /// <summary>只接受固定列投影, 数据量超过边界明确失败, 不发布部分行集组成的尺寸.</summary>
    public DataTable? Read(string schema, CancellationToken token)
    {
        var query = schema switch
        {
            "MDSCHEMA_DIMENSIONS" => "SELECT DIMENSION_UNIQUE_NAME,DIMENSION_TYPE,DIMENSION_CARDINALITY FROM $SYSTEM.MDSCHEMA_DIMENSIONS",
            "DISCOVER_STORAGE_TABLES" => "SELECT DIMENSION_NAME,TABLE_ID,ROWS_COUNT FROM $SYSTEM.DISCOVER_STORAGE_TABLES",
            "DISCOVER_STORAGE_TABLE_COLUMNS" => "SELECT DIMENSION_NAME,TABLE_ID,COLUMN_ID,COLUMN_TYPE,DICTIONARY_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS",
            "DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS" => "SELECT DIMENSION_NAME,TABLE_ID,COLUMN_ID,SEGMENT_NUMBER,TABLE_PARTITION_NUMBER,USED_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS",
            _ => throw new AppException("INVALID_SCHEMA", "未授权的架构行集")
        };
        return ReadQuery(query, optional: true, MaximumRows, 16, token);
    }

    /// <summary>显式计数固定模板在生成/排版期间检查取消, 同一会话复用身份和30秒命令预算; 同步 SDK 只在前后检查.</summary>
    public DataTable ReadRowCounts(IReadOnlyList<string> tables, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ReadQuery(ModelTableRowCountQuery.Create(tables, token), optional: false, tables.Count, 2, token)
            ?? throw new AppException("MODEL_ROW_COUNT_INVALID", "精确行数结果不完整或格式无效", 3);
    }

    /// <summary>共用有界SDK游标生命周期; 只有可选元数据允许服务未提供, 显式计数的任何失败均传播.</summary>
    /// <param name="query">当前类固定行集或可信表模板, 不对外暴露任意查询.</param>
    /// <param name="optional">是否为允许未提供的固定元数据.</param>
    /// <param name="maximumRows">完整行集的有界最大行数, 超限不能截断返回.</param>
    /// <param name="maximumColumns">固定投影可接受的最大字段数.</param>
    /// <param name="token">同步SDK边界和逐行读取的取消.</param>
    /// <returns>完整内存行集, 或可选元数据未提供时的null.</returns>
    private DataTable? ReadQuery(string query, bool optional, int maximumRows, int maximumColumns, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AdomdCommand? command = null;
        AdomdDataReader? rows = null;
        DataTable? result = null;
        Exception? failure = null;
        try
        {
            command = connection!.CreateCommand();
            command.CommandText = query; command.CommandTimeout = 30;
            rows = command.ExecuteReader(); token.ThrowIfCancellationRequested();
            if (rows.FieldCount > maximumColumns) throw Limit(optional);
            result = new DataTable();
            for (var i = 0; i < rows.FieldCount; i++) result.Columns.Add(rows.GetName(i), rows.GetFieldType(i));
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                if (result.Rows.Count >= maximumRows) throw Limit(optional);
                var values = new object[rows.FieldCount];
                rows.GetValues(values); result.Rows.Add(values);
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (AdomdErrorResponseException error) when (optional && !token.IsCancellationRequested && !AuthenticationFailure(error))
        {
            token.ThrowIfCancellationRequested();
            result?.Dispose();
            // 该元数据行集可能被服务或权限限制; 后续权威回读仍须成功, 不把认证/连接失效伪装为未知统计.
            SsasConnection.LogFailure(log, "TableStatisticsMetadata", error);
            return null;
        }
        catch (Exception error) { failure = error; result?.Dispose(); token.ThrowIfCancellationRequested(); throw; }
        finally
        {
            // 游标和命令依次释放; 后续释放错误不能覆盖取消或首个读取失败, 单独清理失败也不能成功交付.
            Exception? cleanup = null;
            try { rows?.Dispose(); } catch (Exception error) { cleanup = error; }
            try { command?.Dispose(); }
            catch (Exception error) { if (cleanup is null) cleanup = error; else SsasConnection.LogFailure(log, "TableStatisticsCommandCleanup", error); }
            if (cleanup is not null)
            {
                if (failure is not null) SsasConnection.LogFailure(log, "TableStatisticsCursorCleanup", cleanup);
                else { result?.Dispose(); ExceptionDispatchInfo.Capture(cleanup).Throw(); }
            }
        }
    }

    /// <summary>认证链中的显式失效不能按可选统计未提供处理, 后端还会在全部读取后再次核验当前会话.</summary>
    private static bool AuthenticationFailure(Exception error)
    {
        var depth = 0;
        for (Exception? current = error; current is not null && depth++ < 6; current = current.InnerException)
        {
            if (current is AuthenticationException or UnauthorizedAccessException
                || current is AdomdConnectionException { ExceptionCause: Microsoft.AnalysisServices.ConnectionExceptionCause.AuthenticationFailed }
                || current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }) return true;
        }
        return false;
    }

    /// <summary>元数据超限与精确结果超出固定形状都明确失败, 不截断或返回部分结果.</summary>
    private static AppException Limit(bool optional) => optional
        ? new("MODEL_STATISTICS_LIMIT", "表统计超过读取上限, 无法提供完整统计", 3)
        : new("MODEL_ROW_COUNT_INVALID", "精确行数结果不完整或格式无效", 3);

    /// <summary>释放当前唯一连接及可能尚未消费的敏感构造参数.</summary>
    public void Dispose() { pendingConnectionString = null; pendingService = null; connection?.Dispose(); }
}
