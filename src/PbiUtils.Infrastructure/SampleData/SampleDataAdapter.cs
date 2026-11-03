// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\SampleDataAdapter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 隔离生成, 安全目录复核, 基线冲突检测与两目录发布恢复.

using PbiUtils.Application.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;

using static PbiUtils.Infrastructure.SampleData.SampleOutputDirectory;
using static PbiUtils.Infrastructure.SampleData.SampleDataPublication;

namespace PbiUtils.Infrastructure.SampleData;

/// <summary>完整上游数据与模板的外部适配器, 不调用 Go, Desktop 或远程服务.</summary>
public sealed class SampleDataAdapter(ILogger<SampleDataAdapter>? logger = null) : ISampleDataAdapter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger logger = logger ?? NullLogger<SampleDataAdapter>.Instance;

    /// <summary>轻量选择检查不解析 CSV, 让损坏数据仍可在明确覆盖后重新生成.</summary>
    public Task<SampleDataState> SelectAsync(string outputPath, CancellationToken cancellationToken) =>
        ExecuteAsync(outputPath, root =>
        {
            var storage = new SampleCsvStorage(Path.Combine(root, "data"));
            // 选择状态保守认定任何已知产物都需要覆盖确认, 包括基础表缺失的目录.
            return new SampleDataState(root, SampleTables.AllFiles.Any(storage.Exists) || Directory.Exists(Path.Combine(root, "pbip")), null, []);
        }, cancellationToken);

    /// <summary>在后台读取实际 CSV 统计, 同一目录的管理操作串行.</summary>
    public Task<SampleDataState> InspectAsync(string outputPath, CancellationToken cancellationToken) =>
        ExecuteAsync(outputPath, root => Inspect(root, root, cancellationToken), cancellationToken);

    /// <summary>复制受控两子树后流式生成, 完成模板和统计后一次发布; 失败保留原目录.</summary>
    /// <param name="beforeCommit">基线核验后等待的可靠回执钩子; 等待结束后再次核验基线与取消.</param>
    public Task<SampleDataResponse> GenerateAsync(string outputPath, SampleDataOptions options, bool incremental, bool overwrite,
        IProgress<SampleDataProgress>? progress, Action? beginCommit, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null) =>
        ExecuteAwaitableAsync(outputPath, async root =>
        {
            options.Validate();
            var existing = new SampleCsvStorage(Path.Combine(root, "data"));
            if (incremental && !new[] { SampleTables.FileProduct, SampleTables.FileStore, SampleTables.FileCustomer, SampleTables.FileOrder }.All(existing.Exists))
                throw new AppException("SAMPLE_NO_BASE_DATA", "目录没有基础示例数据, 请先全量生成.", 3);
            if (!incremental && (SampleTables.AllFiles.Any(existing.Exists) || Directory.Exists(Path.Combine(root, "pbip"))) && !overwrite)
                throw new AppException("SAMPLE_OVERWRITE_REQUIRED", "目录已有示例数据, 请先明确确认覆盖.", 3);
            var baseline = Fingerprint(root, cancellationToken);
            var stage = CreateStage(root);
            var retainStage = false;
            try
            {
                CopyTree(Path.Combine(root, "data"), Path.Combine(stage, "data"), cancellationToken);
                CopyTree(Path.Combine(root, "pbip"), Path.Combine(stage, "pbip"), cancellationToken);
                var storage = new SampleCsvStorage(Path.Combine(stage, "data"));
                var lastPercent = 0.0;
                var generator = new SampleDataGenerator(options, SampleResources.Load(), new InlineProgress(value =>
                {
                    // 直接使用生成进度, 不拼接第二段百分比; 发布前不宣称整个任务完成.
                    lastPercent = Math.Clamp(value.Percent, lastPercent, 99);
                    if (value.Stage != "stageDone") progress?.Report(value with { Percent = lastPercent });
                }));
                var result = incremental ? generator.AppendIncremental(storage, cancellationToken) : generator.GenerateAll(storage, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(lastPercent, "stagePbip"));
                if (incremental) SampleResources.ExpandCalendar(Path.Combine(stage, "pbip"), options);
                else SampleResources.Install(Path.Combine(stage, "pbip"), Path.Combine(root, "data"), options, cancellationToken);
                progress?.Report(new(lastPercent, "stageSummary"));
                var state = Inspect(stage, root, cancellationToken);
                if (beforeCommit is not null)
                {
                    ValidateBaseline(root, baseline, cancellationToken);
                    await beforeCommit(cancellationToken).ConfigureAwait(false);
                }
                // 回执持久化期间允许取消或外部更改; Publish 再核基线后才切入不可取消的文件事务.
                Publish(root, stage, baseline, beginCommit, ref retainStage, cancellationToken);
                // 发布事实优先于进度观察者; 显示或 stderr 失败不能把已生成产物改报失败.
                try { progress?.Report(new(100, "stageDone")); }
                catch (Exception error)
                {
                    using var operation = BusinessLog.Begin(logger, "SampleData.Progress");
                    operation.Fail(error);
                }
                return new SampleDataResponse(state, result);
            }
            finally { if (!retainStage) CleanupStage(stage); }
        }, cancellationToken);

    /// <summary>在隔离目录删除固定 CSV 和整个 PBIP, 原生确认范围由调用宿主负责.</summary>
    public Task<SampleDataState> ClearAsync(string outputPath, Action? beginCommit, CancellationToken cancellationToken) =>
        ExecuteAsync(outputPath, root =>
        {
            if (!Directory.Exists(root)) return Inspect(root, root, cancellationToken);
            var baseline = Fingerprint(root, cancellationToken);
            var stage = CreateStage(root);
            var retainStage = false;
            try
            {
                CopyTree(Path.Combine(root, "data"), Path.Combine(stage, "data"), cancellationToken);
                var data = Path.Combine(stage, "data");
                foreach (var name in SampleTables.AllFiles.Where(_ => Directory.Exists(data)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(Path.Combine(data, name));
                }
                if (Directory.Exists(data) && !Directory.EnumerateFileSystemEntries(data).Any()) Directory.Delete(data);
                var state = Inspect(stage, root, cancellationToken);
                Publish(root, stage, baseline, beginCommit, ref retainStage, cancellationToken);
                return state;
            }
            finally { if (!retainStage) CleanupStage(stage); }
        }, cancellationToken);

    /// <summary>用纯词法目录键取得门闩, 在后台完整验证文件系统后执行业务, 不阻塞宿主 UI 或泄漏异常正文.</summary>
    private static Task<T> ExecuteAsync<T>(string outputPath, Func<string, T> action, CancellationToken cancellationToken) =>
        ExecuteAwaitableAsync(outputPath, root => Task.FromResult(action(root)), cancellationToken);

    /// <summary>持有同一目录门闩直到异步回执与发布排空; 同步文件 I/O 和清理仍在线程池执行.</summary>
    /// <param name="outputPath">宿主绑定或 CLI 明确提供的输出目录.</param>
    /// <param name="action">后台生成或读取操作, 必须等待其所有提交前钩子.</param>
    /// <param name="cancellationToken">准备阶段令牌; 提交后由具体事务排空.</param>
    /// <returns>业务实际结果; 文件系统错误转换为稳定错误.</returns>
    private static async Task<T> ExecuteAwaitableAsync<T>(string outputPath, Func<string, Task<T>> action, CancellationToken cancellationToken)
    {
        var root = NormalizeOutput(outputPath);
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(async () =>
            {
                ValidateOutput(root);
                return await action(root).ConfigureAwait(false);
            }, cancellationToken);
        }
        catch (IOException) { throw new AppException("SAMPLE_IO_FAILED", "示例文件读写失败, 请关闭占用文件的程序并检查目录权限.", 4); }
        catch (UnauthorizedAccessException) { throw new AppException("SAMPLE_ACCESS_DENIED", "无法访问示例目录, 请检查目录权限或重新选择.", 3); }
        finally { gate.Release(); }
    }

    /// <summary>从当前完整表结构统计行数和字节及订单截止日期, 缺表只跳过该表.</summary>
    private static SampleDataState Inspect(string directory, string displayRoot, CancellationToken cancellationToken)
    {
        var before = LocalSampleDataCache.CaptureStamps(directory);
        var data = Path.Combine(directory, "data");
        var storage = new SampleCsvStorage(data);
        var hasData = SampleTables.AllFiles.Any(storage.Exists) || Directory.Exists(Path.Combine(directory, "pbip"));
        var stats = new List<SampleTableStat>();
        DateOnly? last = null;
        foreach (var name in SampleTables.AllFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!storage.Exists(name)) continue;
            long rows = 0;
            foreach (var row in storage.Read(name))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows++;
                if (name == SampleTables.FileOrder)
                {
                    var date = row[3];
                    if (DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && (last is null || value > last)) last = value;
                }
            }
            stats.Add(new(Path.GetFileNameWithoutExtension(name), rows, new FileInfo(Path.Combine(data, name)).Length));
        }
        var after = LocalSampleDataCache.CaptureStamps(directory);
        return new(displayRoot, hasData, last?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), stats)
        {
            // 发布沿用这些文件的时间戳; 外部文件在统计期间变化时不生成可复用缓存.
            FileStamps = before.SequenceEqual(after) ? after : null,
        };
    }

    /// <summary>保持缓存读写调用方的目录校验入口, 所有规则由共享目录策略负责.</summary>
    /// <param name="outputPath">宿主或 CLI 绑定的绝对目录.</param>
    /// <returns>安全规范路径; 非法、根目录或重解析目标抛出稳定错误.</returns>
    internal static string ValidateOutput(string outputPath) => SampleOutputDirectory.ValidateOutput(outputPath);

    /// <summary>核验隔离目录再清理; 发布后清理失败仅记录安全日志, 不改写已提交事实.</summary>
    private void CleanupStage(string stage)
    {
        try
        {
            if (!Path.GetFileName(stage).StartsWith(".pbi-utils-sample-", StringComparison.Ordinal)) throw InvalidOutput();
            RejectLink(stage);
            foreach (var entry in Walk(stage)) RejectLink(entry);
            Directory.Delete(stage, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AppException)
        {
            using var operation = BusinessLog.Begin(logger, "SampleData.Cleanup");
            operation.Fail(new AppException("SAMPLE_CLEANUP_REQUIRED", "隔离目录清理失败.", 4));
        }
    }

    /// <summary>在生成后台线程同步转发进度, 防止提交完成后才派发旧阶段.</summary>
    private sealed class InlineProgress(Action<SampleDataProgress> report) : IProgress<SampleDataProgress>
    {
        /// <summary>立即转发当前阶段.</summary>
        public void Report(SampleDataProgress value) => report(value);
    }
}
