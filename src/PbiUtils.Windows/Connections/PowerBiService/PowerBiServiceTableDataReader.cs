// FilePath    : pbi-utils\src\PbiUtils.Windows\Connections\PowerBiService\PowerBiServiceTableDataReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过当前服务身份流式读取共享容量单表, 以实际数据游标代替不完整架构列标题.

using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Data.Common;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Connections.PowerBiService;

/// <summary>仅打开当前后台目标中的固定单表, 会话拥有连接、命令和游标的完整生命周期.</summary>
public interface IPowerBiTableDataSession : IDisposable
{
    /// <summary>使用已解析目标的内存凭据打开固定表查询; 不接受自由 DAX, 返回游标只在会话内有效.</summary>
    /// <param name="target">宿主当前账号、云和模型目标, 不得记录其中令牌.</param>
    /// <param name="table">权威快照中唯一存在的实际表名.</param>
    /// <param name="rowLimit">模型侧抽样正整数上限, null表示全部行; 不接受网页排序表达式.</param>
    /// <param name="token">同步 SDK 调用前后及逐行读取检查的取消令牌.</param>
    /// <returns>包含实际字段名称、原始类型和值的流式游标.</returns>
    IModelTableRows OpenTable(PowerBiServiceTarget target, string table, int? rowLimit, CancellationToken token);
}

/// <summary>元数据受限不等于数据不可查询; 固定表数据读取前重新核验同一目标的可见架构基线.</summary>
/// <param name="schema">与原连接相同的架构读取器; 不宣称其包含完整 TOM 定义.</param>
/// <param name="createSession">可控平台测试会话, 默认使用真实 ADOMD.</param>
/// <param name="logger">只记录异常类型及数字码的既有安全日志.</param>
public sealed class PowerBiServiceTableDataReader(PowerBiSchemaReader schema, Func<IPowerBiTableDataSession>? createSession = null, ILogger? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger.Instance;

    /// <summary>读取所选单表的全部行或模型侧样本并在当前线程消费; 身份复核和发布由共享服务持有.</summary>
    /// <param name="snapshot">查询开始前的宿主当前快照.</param>
    /// <param name="target">本轮重新解析的账号、云和模型.</param>
    /// <param name="table">当前快照中唯一实际表名.</param>
    /// <param name="modelId">原服务目录的后台句柄.</param>
    /// <param name="rowLimit">在模型侧执行的无排序抽样上限, null表示全部行.</param>
    /// <param name="consume">同步文件消费器, 不得保留游标.</param>
    /// <param name="token">取消只在同步 SDK 边界检查, 不承诺立即中断内部调用.</param>
    /// <returns>完整消费的数据行数; 不重试已开始的数据查询.</returns>
    /// <exception cref="AppException">身份、可见架构、查询、字段或消费失败; 不回传 SDK 原文.</exception>
    /// <exception cref="OperationCanceledException">查询前或同步调用返回后取消, 总会释放会话.</exception>
    public long Read(ProjectSnapshot snapshot, PowerBiServiceTarget target, string table, string? modelId, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var current = schema.Read(snapshot.ProjectPath, target, token, modelId);
            ModelTableExportService.ValidateSnapshot(current, new(snapshot.ProjectId, snapshot.BaselineId, [table], "csv", RowLimit: rowLimit));
            if (current.ProjectPath != snapshot.ProjectPath || !ModelConnectionInfo.SameTarget(current.Connection, snapshot.Connection))
                throw new AppException("STALE_BASELINE", "导出目标已变化, 请刷新后重试", 3);
            token.ThrowIfCancellationRequested();
            var session = createSession?.Invoke() ?? new DataSession();
            Exception? failure = null;
            try
            {
                var rows = session.OpenTable(target, table, rowLimit, token);
                token.ThrowIfCancellationRequested();
                // 架构列来自可选层次标题, 不用其数量或名称裁剪实际数据列, 空表仍保留真实标题.
                if (rows.Columns.Count == 0) throw new AppException("EXPORT_TABLE_EMPTY", "该表没有可导出的数据列", 3);
                var count = consume(rows, token);
                token.ThrowIfCancellationRequested();
                return count;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try { session.Dispose(); }
                // 清理次错只记录类型和数字码, 不覆盖原业务失败或取消; 单独清理失败仍交给外层安全失败边界.
                catch (Exception cleanup) when (failure is not null) { SsasConnection.LogFailure(log, "ServiceTableExportCleanup", cleanup); }
            }
        }
        catch (AppException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            token.ThrowIfCancellationRequested();
            SsasConnection.LogFailure(log, "ServiceTableExport", error);
            throw new AppException("EXPORT_QUERY_FAILED", "无法读取表数据, 请检查连接、权限及模型处理状态", 4);
        }
    }

    /// <summary>仅移除实际表的精确限定前缀; SDK 返回格式有歧义时保留原始标题, 不猜测或合并字段.</summary>
    /// <param name="table">当前查询的精确表名.</param>
    /// <param name="name">SDK 按序号返回的完整字段名.</param>
    /// <returns>无损的实际字段标题; 名称内部括号保留原文.</returns>
    public static string ColumnName(string table, string name)
    {
        var prefixes = new[] { table + "[", "'" + table.Replace("'", "''", StringComparison.Ordinal) + "'[" };
        var prefix = prefixes.FirstOrDefault(value => name.StartsWith(value, StringComparison.Ordinal));
        return prefix is not null && name.EndsWith(']') ? name[prefix.Length..^1] : name;
    }

    /// <summary>只供当前调用的真实 ADOMD 会话, 失败时逆序释放所有已建立资源.</summary>
    private sealed class DataSession : IPowerBiTableDataSession
    {
        private AdomdConnection? connection;
        private AdomdCommand? command;
        private AdomdDataReader? reader;
        /// <summary>构造前提供 pbiazure ClaimsToken, 不同时设置 AccessToken, 不保存或回传连接串; 查询生成检查取消, 同步 SDK 只在前后检查.</summary>
        /// <inheritdoc/>
        public IModelTableRows OpenTable(PowerBiServiceTarget target, string table, int? rowLimit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var settings = new DbConnectionStringBuilder { ConnectionString = PowerBiSchemaReader.ConnectionString(target, includeCredential: true) };
            try { settings["Timeout"] = 120; connection = new(settings.ConnectionString); }
            finally { settings.Clear(); }
            connection.Open(); token.ThrowIfCancellationRequested();
            command = connection.CreateCommand(); command.CommandText = ModelTableExportQuery.Create(table, rowLimit, token); command.CommandTimeout = 120;
            token.ThrowIfCancellationRequested(); reader = command.ExecuteReader(); token.ThrowIfCancellationRequested();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(index => ColumnName(table, reader.GetName(index))).ToArray();
            return new Rows(reader, columns, token);
        }
        /// <summary>逐层finally保证单个SDK释放失败也不阻断其余资源释放.</summary>
        public void Dispose()
        {
            try { reader?.Dispose(); }
            finally { try { command?.Dispose(); } finally { connection?.Dispose(); } }
        }
    }

    /// <summary>游标只借用会话资源, 原始标量不经过 JSON 或字符串转换, 每次读取前后检查取消.</summary>
    private sealed class Rows(AdomdDataReader reader, IReadOnlyList<string> columns, CancellationToken token) : IModelTableRows
    {
        public IReadOnlyList<string> Columns => columns;
        /// <summary>逐行读取前后检查取消; SDK 失败由调用方脱敏并释放整个会话.</summary>
        /// <returns>是否还有实际数据行, 空表首次返回 false.</returns>
        public bool Read() { token.ThrowIfCancellationRequested(); var result = reader.Read(); token.ThrowIfCancellationRequested(); return result; }
        /// <summary>取得当前行原始标量, 不转换大整数、日期或布尔类型.</summary>
        /// <param name="ordinal">实际游标字段序号, 由同步消费器在 Columns 范围内使用.</param>
        /// <returns>原始值或 null; SDK 读取错误交给调用方脱敏.</returns>
        public object? GetValue(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
    }
}
