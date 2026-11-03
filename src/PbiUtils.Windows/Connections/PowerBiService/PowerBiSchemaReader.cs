// FilePath    : pbi-utils\src\PbiUtils.Windows\Connections\PowerBiService\PowerBiSchemaReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 使用固定 ADOMD 架构行集读取共享或个人工作区, 不查询业务数据或写入模型.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AnalysisServices.AdomdClient;
using PbiUtils.Application;
using PbiUtils.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PbiUtils.Windows.Connections.PowerBiService;

/// <summary>用于真实 SDK 与确定性测试的架构会话, 只支持固定元数据行集.</summary>
public interface IPowerBiSchemaSession : IDisposable
{
    string ServerVersion { get; }
    /// <summary>建立一个短生命周期的后端认证连接.</summary>
    void Open(PowerBiServiceTarget target);
    /// <summary>读取一个固定元数据行集, 不执行用户 DAX 或查询业务值.</summary>
    DataTable Read(string schema);
}
/// <summary>共享容量支持范围与 Bravo 的非 XMLA 分支一致, 返回有明确限制的结构快照.</summary>
public sealed class PowerBiSchemaReader(Func<IPowerBiSchemaSession>? createSession = null, ILogger<PowerBiSchemaReader>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<PowerBiSchemaReader>.Instance;
    private static readonly string[] MeasureNameColumns = ["MEASURE_NAME", "MEASURE_CAPTION", "MEASURE_UNIQUE_NAME"];

    /// <summary>在线程池边界调用; 请求取消在每个同步 SDK 调用前后复核, using 总会释放连接.</summary>
    /// <param name="projectPath">已注册的后端模型句柄.</param>
    /// <param name="target">当前账号解析后的服务模型目标.</param>
    /// <param name="token">在同步 SDK 调用边界检查的取消令牌.</param>
    /// <param name="modelId">已绑定 Service 目录条目的后端句柄, 只供展示身份匹配.</param>
    /// <returns>实际返回的只读架构快照; 未提供能力保持明确限制.</returns>
    public ProjectSnapshot Read(string projectPath, PowerBiServiceTarget target, CancellationToken token, string? modelId = null)
    {
        using var operation = BusinessLog.Begin(log, "PowerBi.SchemaRead");
        for (var attempt = 0; ; attempt++)
        {
            var stage = "架构连接";
            try
            {
                token.ThrowIfCancellationRequested();
                var snapshot = ReadAttempt(projectPath, target, modelId, ref stage, token);
                operation.Complete();
                return snapshot;
            }
            catch (Exception error) when (attempt == 0 && !token.IsCancellationRequested && TransportReset(error) is not null)
            {
                // 只重建被明确中断的只读架构会话; 部分架构已经释放, 原身份、目标和总取消预算不变.
                SsasConnection.LogFailure(log, stage, error);
                BusinessLog.ModelReadRetry(log, stage, 2, TransportReset(error)!.Value);
            }
            catch (AppException error) { operation.Fail(error); throw; }
            catch (OperationCanceledException error) { operation.Fail(error); throw; }
            catch (Exception error)
            {
                SsasConnection.LogFailure(log, stage, error);
                operation.Fail(error); token.ThrowIfCancellationRequested(); throw Failure(stage, error, target);
            }
        }
    }
    /// <summary>读取一次完整的固定架构, 任一失败均释放当前会话和部分结果, 不发布跨会话拼接的模型.</summary>
    /// <param name="projectPath">原宿主模型注册.</param>
    /// <param name="target">本次连接前已经解析的账号、云及模型身份.</param>
    /// <param name="modelId">原目录后端句柄.</param>
    /// <param name="stage">返回实际失败阶段, 不含地址或模型内容.</param>
    /// <param name="token">原调用方令牌, 不创建或重置时限.</param>
    /// <returns>同一次架构会话内完成的快照.</returns>
    private ProjectSnapshot ReadAttempt(string projectPath, PowerBiServiceTarget target, string? modelId, ref string stage, CancellationToken token)
    {
        using var session = createSession?.Invoke() ?? new SchemaSession();
        token.ThrowIfCancellationRequested();
        BusinessLog.ModelStage(log, stage);
        session.Open(target);
        token.ThrowIfCancellationRequested();
        stage = "表架构";
        BusinessLog.ModelStage(log, stage);
        using var dimensions = session.Read("MDSCHEMA_DIMENSIONS");
        token.ThrowIfCancellationRequested();
        List<string> warnings = ["当前工作区使用架构只读连接. 分区、兼容级别、关系、角色和 UDF 未提供; 度量值定义以服务实际返回为准", "在线模型不含报表布局, 未查询业务数据或提交模型"];
        stage = "字段架构";
        BusinessLog.ModelStage(log, stage);
        using var hierarchies = Optional("MDSCHEMA_HIERARCHIES");
        stage = "度量值架构";
        BusinessLog.ModelStage(log, stage);
        using var measures = session.Read("MDSCHEMA_MEASURES");
        var model = Map(target.ModelName ?? target.Database, dimensions, hierarchies, measures);
        if (model.Measures.Count == 0) warnings.Add("服务架构未返回度量值; 请核对当前模型和架构读取权限, 不代表已确认模型没有度量值");
        if (model.Measures.Any(m => string.IsNullOrWhiteSpace(m.Expression))) warnings.Add("部分度量值 DAX 定义未提供, 只能查看实际返回的名称和属性");
        token.ThrowIfCancellationRequested();
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target.Server + "|" + target.DatabaseId + "|" + target.AccountId)));
        var baseline = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(model)));
        return new(projectPath, identity, baseline, "Power BI Service / 架构", "在线模型不含报表布局", session.ServerVersion, null,
            model, [], [], warnings, new("powerbi-service", "Power BI Service", true, ModelId: modelId));

        // 架构行集可能被单独限制; 已成功的表信息仍可显示, 缺失范围必须明确提示.
        DataTable Optional(string schema)
        {
            token.ThrowIfCancellationRequested();
            try { var table = session.Read(schema); token.ThrowIfCancellationRequested(); return table; }
            catch (AdomdErrorResponseException e)
            {
                token.ThrowIfCancellationRequested();
                warnings.Add($"{schema} 未提供 ({e.GetType().Name}, 0x{e.HResult:X8}); 此部分未读取, 不代表对象数量为零");
                return new DataTable();
            }
        }
    }
    /// <summary>只识别真实 ADOMD 的 HTTP 传输链中10053或10054断链, 认证、权限、取消和超时均不重试.</summary>
    private static int? TransportReset(Exception error)
    {
        if (error is not AdomdConnectionException connection || connection.ExceptionCause is Microsoft.AnalysisServices.ConnectionExceptionCause.AuthenticationFailed
            or Microsoft.AnalysisServices.ConnectionExceptionCause.WorkspaceNotFound or Microsoft.AnalysisServices.ConnectionExceptionCause.DatabaseNotFound
            or Microsoft.AnalysisServices.ConnectionExceptionCause.XmlaEndpointDisabled) return null;
        var http = false; int? reset = null;
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        for (Exception? current = error; current is not null && visited.Count < 6 && visited.Add(current); current = current.InnerException)
        {
            if (current is OperationCanceledException or TimeoutException or UnauthorizedAccessException or AuthenticationException) return null;
            if (current is HttpRequestException request)
            {
                if (request.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
                http = true;
            }
            if (current is SocketException { NativeErrorCode: 10053 or 10054 } socket) reset = socket.NativeErrorCode;
        }
        return http ? reset : null;
    }
    /// <summary>默认返回无凭据参数; pbiazure SDK 初始化显式使用内存 ClaimsToken 凭据, 禁止记录该返回值.</summary>
    /// <param name="target">已经绑定账号与云的只读目标.</param>
    /// <param name="includeCredential">仅后端 SDK 初始化时包含内存凭据, 不持久化.</param>
    /// <returns>云集群发现及连接使用30秒预算, 架构命令使用10秒预算的连接串; 调用方总取消预算不重置.</returns>
    /// <exception cref="AppException">要求初始化凭据但目标不是 pbiazure 协议.</exception>
    public static string ConnectionString(PowerBiServiceTarget target, bool includeCredential = false)
    {
        var builder = new DbConnectionStringBuilder
        {
            ["Data Source"] = target.Server,
            ["Initial Catalog"] = target.Database,
            // ADOMD 在构造器内以此预算解析云集群; 5秒会在发现稍慢时抛出 ArgumentException.
            // 此值不是整个请求时限, 同步 SDK 仍只在调用前后响应调用方取消.
            ["Connect Timeout"] = 30,
            ["Timeout"] = 10,
            ["Application Name"] = "pbi-utils",
            ["Integrated Security"] = "ClaimsToken",
            ["Identity Provider"] = target.IdentityProvider
        };
        // SDK 解析 pbiazure 参数时以 owner=null 建立认证句柄, 构造后设置 AccessToken 来不及.
        if (includeCredential)
        {
            if (!target.Server.StartsWith("pbiazure://", StringComparison.Ordinal)) throw new AppException("INVALID_SERVICE_TARGET", "ClaimsToken 初始化仅用于服务架构连接");
            builder["Password"] = target.AccessToken;
            builder["Persist Security Info"] = false;
        }
        try { return builder.ConnectionString; }
        finally { builder.Clear(); }
    }
    /// <summary>展示阶段与脱敏异常链, 不返回 SDK 原始异常或堆栈.</summary>
    public static AppException Failure(string stage, Exception error, PowerBiServiceTarget? target = null) => TransportReset(error) is not null
        ? new("SERVICE_CONNECTION_INTERRUPTED", "服务模型连接被中断, 请重新连接; 当前模型保持不变", 3)
        : new("MODEL_CONNECTION_FAILED", $"服务模型读取失败: {stage}. {ModelConnectionDiagnostics.Describe(error, target)}", 3);
    /// <summary>映射固定架构字段, 不把未提供的表达式或模型能力伪装为已读取.</summary>
    public static ModelInfo Map(string name, DataTable dimensions, DataTable hierarchies, DataTable measures)
    {
        if (!dimensions.Columns.Contains("DIMENSION_UNIQUE_NAME") || !dimensions.Columns.Contains("DIMENSION_TYPE"))
            throw new AppException("MODEL_SCHEMA_INVALID", "服务未返回可识别的表架构", 3);
        if (!MeasureNameColumns.Any(measures.Columns.Contains))
            throw new AppException("MODEL_SCHEMA_INVALID", "服务未返回可识别的度量值架构", 3);
        if (new[] { dimensions, hierarchies, measures }.Any(t => t.Rows.Count > 50000)) throw new AppException("MODEL_METADATA_LIMIT", "模型架构超过读取上限", 3);
        var tables = dimensions.Rows.Cast<DataRow>().Where(r => Value(r, "DIMENSION_TYPE") != "2").Select(r =>
        {
            var unique = Value(r, "DIMENSION_UNIQUE_NAME");
            var tableName = DaxName(unique);
            var columns = hierarchies.Rows.Cast<DataRow>().Where(h => Value(h, "DIMENSION_UNIQUE_NAME") == unique)
                .Select(h => new ColumnInfo(Value(h, "HIERARCHY_CAPTION") is { Length: > 0 } caption ? caption : DaxName(Value(h, "HIERARCHY_UNIQUE_NAME")),
                    "架构字段", "未提供", Value(h, "HIERARCHY_IS_VISIBLE") == "False", Value(h, "DESCRIPTION")))
                .Where(c => c.Name.Length > 0).DistinctBy(c => c.Name).ToArray();
            return new TableInfo(tableName, Value(r, "DESCRIPTION"), false, columns, []);
        }).Where(t => t.Name.Length > 0).DistinctBy(t => t.Name).ToArray();
        var values = measures.Rows.Cast<DataRow>().Select(r => new MeasureInfo(
            DaxName(Value(r, "MEASUREGROUP_NAME")), MeasureName(r), Value(r, "EXPRESSION"),
            Value(r, "DESCRIPTION"), Value(r, "DEFAULT_FORMAT_STRING"), Value(r, "MEASURE_DISPLAY_FOLDER"), Value(r, "MEASURE_IS_VISIBLE").Equals("False", StringComparison.OrdinalIgnoreCase)))
            .Where(m => m.Name.Length > 0).DistinctBy(m => (m.Table, m.Name)).ToArray();
        return new(name, 0, "", tables, values, [], 0, 0);
    }
    /// <summary>从括号标识提取名称, 保留名称中转义的右括号和特殊字符.</summary>
    private static string DaxName(string value) => value.StartsWith('[') && value.EndsWith(']') ? value[1..^1].Replace("]]", "]", StringComparison.Ordinal) : value;
    /// <summary>优先真实名称, 其次标题和唯一标识, 保留转义名称且不丢弃隐藏度量值.</summary>
    private static string MeasureName(DataRow row) => Value(row, "MEASURE_NAME") is { Length: > 0 } name ? name
        : Value(row, "MEASURE_CAPTION") is { Length: > 0 } caption ? caption
        : DaxName(Value(row, "MEASURE_UNIQUE_NAME").Replace("[Measures].", "", StringComparison.Ordinal));
    /// <summary>Initial Catalog 已绑定单个后端模型; 不用虚拟数据库名再次过滤行集, 显式包括隐藏度量值.</summary>
    public static AdomdRestrictionCollection Restrictions(string schema) => schema == "MDSCHEMA_MEASURES"
        ? [new("MEASURE_VISIBILITY", 3)] : [];
    /// <summary>缺失或数据库空值不拼接成字符串, 文化不变转换仅用于架构属性.</summary>
    private static string Value(DataRow row, string column) => row.Table.Columns.Contains(column) && row[column] is not DBNull
        ? Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? "" : "";
    /// <summary>官方 SDK 实现, 没有任意查询或用户提供的行集入口.</summary>
    private sealed class SchemaSession : IPowerBiSchemaSession
    {
        private AdomdConnection? connection;
        public string ServerVersion => connection!.ServerVersion;
        /// <summary>与 Bravo 一致在构造前提供 ClaimsToken; 不同时设置 AccessToken, 不持久化或回传凭据.</summary>
        public void Open(PowerBiServiceTarget target)
        {
            connection = new(ConnectionString(target, includeCredential: true));
            connection.Open();
        }
        /// <summary>Catalog 约束由已经打开的连接确定, 仅查询固定架构行集.</summary>
        public DataTable Read(string schema)
        {
            if (schema is not ("MDSCHEMA_DIMENSIONS" or "MDSCHEMA_HIERARCHIES" or "MDSCHEMA_MEASURES")) throw new AppException("INVALID_SCHEMA", "未授权的架构行集");
            var restrictions = Restrictions(schema);
            using var data = connection!.GetSchemaDataSet(schema, restrictions);
            if (data.Tables.Count != 1) throw new AppException("MODEL_SCHEMA_INVALID", "服务未返回唯一架构行集", 3);
            return data.Tables[0].Copy();
        }
        /// <summary>释放且只释放本会话持有的连接.</summary>
        public void Dispose() => connection?.Dispose();
    }
}
