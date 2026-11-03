// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Export\ModelTableExportService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 编排固定多表读取、逐表身份复核与整轮发布, 不依赖文件系统或 SDK.

using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Code.Dax;
using PbiUtils.Core;

namespace PbiUtils.Application.Models.Export;

/// <summary>同步流式行游标, 生命周期由平台适配器持有, 不得在消费回调外保留.</summary>
public interface IModelTableRows
{
    IReadOnlyList<string> Columns { get; }
    /// <summary>移动到下一行, SDK 读取错误原样交给平台安全边界处理.</summary>
    bool Read();
    /// <summary>取得当前行指定列的原始标量, DBNull 可作为空值交给写入器处理.</summary>
    object? GetValue(int ordinal);
}

/// <summary>仅查询已核验快照内的固定表, 连接、认证和同步 SDK 均由平台适配器持有.</summary>
public interface IModelTableDataReader
{
    /// <summary>在后台身份范围内消费流式结果, 取消在 SDK 调用前后检查, 不承诺立即中断.</summary>
    /// <param name="snapshot">宿主刚核验的当前模型结构及身份.</param>
    /// <param name="table">当前模型精确表名.</param>
    /// <param name="rowLimit">模型侧无排序抽样上限, 正整数或null表示全部行.</param>
    /// <param name="consume">同步消费模型实际返回的行, 不得保留游标.</param>
    /// <param name="token">同步SDK边界及逐行取消令牌.</param>
    /// <returns>实际消费行数; 不按请求上限伪造完整表总量.</returns>
    Task<long> ReadAsync(ProjectSnapshot snapshot, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token);
}

/// <summary>只创建宿主已经通过系统选择器授权的单个输出文件或全新CSV子目录, 不接受网页路径.</summary>
public interface IModelTableFileWriter
{
    /// <summary>记录原目标基线并创建同目录临时文件; 返回资源由调用方总是释放.</summary>
    Task<IModelTableExportFile> CreateAsync(string destination, ModelTableExportRequest request, CancellationToken token);
}

/// <summary>私有临时文件或CSV目录, 完整写成及模型核验后才允许发布.</summary>
public interface IModelTableExportFile : IDisposable
{
    string FileName { get; }
    /// <summary>按已选表的顺序流式写入一个工作表或CSV文件, 失败或取消不得改变发布目标.</summary>
    long Write(string table, IModelTableRows rows, CancellationToken token);
    /// <summary>全部表写完后完成格式尾部和刷新, 发布前仍受理取消.</summary>
    void Complete(CancellationToken token);
    /// <summary>复核目标文件基线, 在 beginCommit 竞争裁决之后不可取消地发布完整文件.</summary>
    Task CommitAsync(Action? beginCommit, CancellationToken token);
}

/// <summary>固定多表导出共享用例, 每表依次读取并复核, 完成全部选择后才一次发布.</summary>
/// <param name="reader">原始权威模型读取器, 不得传入已持有读范围的 ModelEditService.</param>
/// <param name="data">复用当前连接身份的固定表数据读取器.</param>
/// <param name="writer">格式与受锁文件发布适配器.</param>
/// <param name="progressTimeProvider">进度通知节流的单调时钟, 默认系统时钟, 不影响业务或取消预算.</param>
public sealed class ModelTableExportService(IProjectReader reader, IModelTableDataReader data, IModelTableFileWriter writer, TimeProvider? progressTimeProvider = null)
{
    private const string CleanupFailure = "PbiUtils.ModelTableExport.CleanupFailed";
    /// <summary>导出指定表集合, 每表查询后及格式完成后复核目标和实际可见基线, 全部成功后才发布.</summary>
    /// <param name="projectPath">宿主已绑定的当前连接句柄.</param>
    /// <param name="request">绑定模型身份、结构基线与精确表的请求.</param>
    /// <param name="destination">系统保存选择器授权的文件路径.</param>
    /// <param name="token">发布前可取消; 同步 SDK 在每次调用边界检查.</param>
    /// <param name="beginCommit">宿主的取消与提交裁决, 返回后必须排空最终发布.</param>
    /// <param name="progress">可选同步有序通知, 仅提供真实计数和固定阶段, 不将读完当成发布成功.</param>
    /// <returns>已发布文件或目录名、总行数、格式和逐表实际行数.</returns>
    /// <exception cref="AppException">无效请求、过期身份、不可查询来源或文件失败.</exception>
    public async Task<ModelTableExportResult> ExportAsync(string projectPath, ModelTableExportRequest request, string destination, CancellationToken token, Action? beginCommit = null, Action<ModelTableExportProgress>? progress = null)
    {
        ValidateRequest(request);
        request = request with { Tables = request.Tables.ToArray() };
        var snapshot = await reader.ReadAsync(projectPath, token);
        ValidateSnapshot(snapshot, request);
        var file = await writer.CreateAsync(destination, request, token);
        var reporter = new ModelTableExportProgressReporter(progress, request.Tables.Count, progressTimeProvider ?? TimeProvider.System);
        Exception? failure = null;
        try
        {
            List<ModelTableExportItem> items = [];
            long count = 0;
            foreach (var table in request.Tables)
            {
                token.ThrowIfCancellationRequested();
                reporter.StartTable();
                var rows = await data.ReadAsync(snapshot, table, request.RowLimit,
                    (values, cancellation) => file.Write(table, reporter.Observe(request.RowLimit is { } limit ? new RowLimitGuard(values, limit, cancellation) : values, cancellation), cancellation), token);
                if (request.RowLimit is { } maximum && rows > maximum) throw RowLimitExceeded();
                count = checked(count + rows);
                items.Add(new(table, rows));
                reporter.Verifying();
                await VerifyAsync();
                reporter.Verified();
            }
            // 完成包尾部及同步刷盘由文件适配持有, 在线程池排空, 不阻塞宿主UI线程或承诺即时中断刷盘.
            reporter.Stage("finalizing");
            await Task.Run(() => file.Complete(token), token);
            await VerifyAsync();
            reporter.Stage("publishing");
            await file.CommitAsync(beginCommit, token);
            return new(file.FileName, count, request.Format, items, RowLimit: request.RowLimit);

            /// <summary>每张表完成后重新读取同一宿主目标, 不以表名、统计或部分数据推断模型身份相等.</summary>
            async Task VerifyAsync()
            {
                token.ThrowIfCancellationRequested();
                var observed = await reader.ReadAsync(projectPath, token);
                token.ThrowIfCancellationRequested();
                ValidateSnapshot(observed, request);
                if (observed.ProjectPath != snapshot.ProjectPath || !ModelConnectionInfo.SameTarget(observed.Connection, snapshot.Connection))
                    throw new AppException("STALE_BASELINE", "导出目标已变化, 请刷新后重试", 3);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { file.Dispose(); }
            catch (Exception) when (failure is not null) { failure.Data[CleanupFailure] = true; }
        }
    }

    /// <summary>保留导出首错和取消码, 仅为确实发生的临时清理失败追加安全事实.</summary>
    /// <param name="failure">原始失败, 不读取其正文或内部异常.</param>
    /// <param name="safeMessage">调用方已经脱敏的提示.</param>
    /// <returns>必要时附加清理失败事实的提示, 不包含文件路径或业务数据.</returns>
    public static string DecorateCleanupFailure(Exception? failure, string safeMessage) => failure?.Data[CleanupFailure] is true
        ? safeMessage + "; 导出临时文件清理失败, 请检查目标目录" : safeMessage;

    /// <summary>在系统选择器前及查询前后统一复核来源、身份、基线和表, 不建立查询或写文件.</summary>
    /// <param name="current">当前权威模型快照.</param>
    /// <param name="request">本次精确表集合请求, 全部表均在外部查询或文件创建前检查.</param>
    /// <exception cref="AppException">输入、来源、基线、未保存状态或实体列不符合导出要求.</exception>
    public static void ValidateSnapshot(ProjectSnapshot current, ModelTableExportRequest request)
    {
        ValidateRequest(request);
        var limitedService = current.Connection is { Kind: "powerbi-service", IsLimited: true };
        // Service 的架构限制只描述元数据范围, 数据读取由已注册目标的固定 ADOMD 查询及实际游标核验.
        if (current.Connection is not { Kind: "desktop" or "ssas-xmla" or "powerbi-service" }
            || current.Connection.IsLimited && !limitedService
            || !current.ProjectPath.StartsWith("model://", StringComparison.Ordinal))
            throw new AppException("EXPORT_SOURCE_UNSUPPORTED", "当前来源不支持表数据导出, 请连接完整在线模型", 3);
        if (current.HasPendingChanges || current.SaveResult?.ErrorCode is not null
            || current.SaveResult is { ModelSaved: true, DesktopSynchronized: false })
            throw new AppException("UNSAVED_CHANGES", "请先完成模型保存或核验后导出", 3);
        if (current.ProjectId != request.ProjectId || current.BaselineId != request.BaselineId)
            throw new AppException("STALE_BASELINE", "模型已变化, 请刷新后重新导出", 3);
        foreach (var name in request.Tables)
        {
            var tables = current.Model.Tables.Where(t => t.Name == name).ToArray();
            if (tables.Length != 1) throw new AppException("EXPORT_TABLE_UNAVAILABLE", "表已不存在, 请刷新后重试", 3);
            // 受限 Service 可能未提供字段架构, 不据此推定数据会话没有列; 实际零列由平台读取器明确拒绝.
            if (!limitedService && !tables[0].Columns.Any(c => c.Kind != "RowNumber"))
                throw new AppException("EXPORT_TABLE_EMPTY", "该表没有可导出的数据列", 3);
        }
    }

    /// <summary>固定格式和语言白名单在任何外部读取前核验.</summary>
    private static void ValidateRequest(ModelTableExportRequest request)
    {
        if (request.Format is not ("xlsx" or "csv")) throw new AppException("EXPORT_FORMAT_INVALID", "请选择 Excel 或 CSV 格式");
        if (request.Format == "csv" && request.IncludeLog) throw new AppException("EXPORT_LOG_UNSUPPORTED", "CSV 不支持导出日志");
        if (request.RowLimit is <= 0 || request.Locale is not ("zh-cn" or "en-us") || request.Tables is null || request.Tables.Count is < 1 or > 2000
            || request.Tables.Any(table => string.IsNullOrWhiteSpace(table) || table.Length > 512 || table.Contains('\0'))
            || request.Tables.Distinct(StringComparer.Ordinal).Count() != request.Tables.Count
            || string.IsNullOrWhiteSpace(request.ProjectId) || request.ProjectId.Length > 200
            || string.IsNullOrWhiteSpace(request.BaselineId) || request.BaselineId.Length > 200
            || request.ProjectId.Contains('\0') || request.BaselineId.Contains('\0'))
            throw new AppException("VALIDATION_ERROR", "导出参数无效");
    }

    /// <summary>模型侧抽样若异常返回超量, 明确失败而非静默裁剪后发布.</summary>
    private static AppException RowLimitExceeded() => new("EXPORT_ROW_LIMIT_EXCEEDED", "模型返回的数据超过抽样行数上限, 未导出文件", 4);

    /// <summary>只检测源查询异常, 不在客户端实现抽样; 最多额外读取一行即拒绝整轮发布.</summary>
    private sealed class RowLimitGuard(IModelTableRows source, int limit, CancellationToken token) : IModelTableRows
    {
        private long count;
        public IReadOnlyList<string> Columns => source.Columns;
        /// <summary>源仍提供原始行流; 取消优先于异常超量, 不返回伪造的流结束.</summary>
        public bool Read()
        {
            token.ThrowIfCancellationRequested();
            var available = source.Read();
            token.ThrowIfCancellationRequested();
            if (available && ++count > limit) throw RowLimitExceeded();
            return available;
        }
        /// <summary>原样借用当前标量, 不转换或缓存业务值.</summary>
        public object? GetValue(int ordinal) => source.GetValue(ordinal);
    }
}

/// <summary>仅从已核验模型结构生成固定单表查询, 不接受自由查询文本.</summary>
public static class ModelTableExportQuery
{
    /// <summary>通过 SELECTCOLUMNS 排除内部行号并保留原表重复行, 转义标识符和别名后使用共享 long 布局.</summary>
    /// <param name="table">权威快照中的实际表与列.</param>
    /// <param name="rowLimit">无排序模型侧抽样上限, null表示全部行.</param>
    /// <param name="cancellationToken">生成入口、逐列投影和共享排版期间检查取消; 不返回部分查询.</param>
    /// <returns>仅投影指定表实体列的 long DAX 查询, 不受交互式格式化容量限制.</returns>
    /// <exception cref="AppException">表没有可导出的实体列, 行数上限无效或查询无法安全排版.</exception>
    /// <exception cref="OperationCanceledException">调用方在生成开始前或生成过程中取消.</exception>
    public static string Create(TableInfo table, int? rowLimit = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var columns = table.Columns.Where(column =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return column.Kind != "RowNumber";
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (columns.Length == 0) throw new AppException("EXPORT_TABLE_EMPTY", "该表没有可导出的数据列", 3);
        var name = TableName(table.Name);
        var projections = string.Join(",", columns.Select(column =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 文本别名与方括号标识符分别编码, 不把实际列名解释为 DAX 片段.
            var alias = column.Name.Replace("\"", "\"\"", StringComparison.Ordinal);
            var reference = $"{name}[{column.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
            return $"""
                "{alias}",{reference}
                """;
        }));
        cancellationToken.ThrowIfCancellationRequested();
        // 名称转义仍由固定模板负责; 复用 long 规划但保留合法大模型投影原有的查询容量.
        return DaxFormatter.FormatGeneratedQuery($"""EVALUATE SELECTCOLUMNS({Source(name, rowLimit)},{projections})""", cancellationToken);
    }

    /// <summary>有限Service直接查询实际数据列, 不依赖可能缺失的字段架构或引入去重.</summary>
    /// <param name="table">权威模型精确表名, 不接受查询正文.</param>
    /// <param name="rowLimit">正整数抽样上限, null表示全部行; 无排序且不保证重复运行稳定.</param>
    /// <param name="cancellationToken">生成入口和共享排版期间检查取消; 不返回部分查询.</param>
    /// <returns>模型侧限制的固定 long EVALUATE 查询, 所有实际字段由数据游标读取.</returns>
    /// <exception cref="AppException">行数上限不是正整数或查询无法安全排版.</exception>
    /// <exception cref="OperationCanceledException">调用方在生成开始前或生成过程中取消.</exception>
    public static string Create(string table, int? rowLimit = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DaxFormatter.FormatGeneratedQuery($"""EVALUATE {Source(TableName(table), rowLimit)}""", cancellationToken);
    }

    /// <summary>表标识只做DAX单引号转义, 不把业务名称当表达式执行.</summary>
    private static string TableName(string table) => "'" + table.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>生成待统一排版的源表或 SAMPLE, 不增加业务排序、分组或去重.</summary>
    /// <param name="name">已完成 DAX 标识符转义的实际表名.</param>
    /// <param name="rowLimit">正整数抽样上限, null保持完整表路径.</param>
    /// <returns>仅由安全名称与固定参数构成的源表表达式; 不限制合法模型名的总长度.</returns>
    /// <exception cref="AppException">行数上限不是正整数.</exception>
    private static string Source(string name, int? rowLimit)
    {
        if (rowLimit is <= 0) throw new AppException("VALIDATION_ERROR", "导出参数无效");
        // 实际SDK要求至少三个参数; 固定标量0满足语法, 不选择业务排序列或承诺稳定/均匀抽样.
        return rowLimit is { } limit
            ? $"""SAMPLE({limit.ToString(System.Globalization.CultureInfo.InvariantCulture)},{name},0)"""
            : name;
    }
}
