// FilePath    : pbi-utils\src\PbiUtils.Application\Scaffolding\ScaffoldService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一脚手架预览, 精确批准, 结果事实及安全业务日志.

using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Scaffolding;

/// <summary>阶段来自真实业务, 提交后 CanCancel 为 false.</summary>
public sealed record ScaffoldProgress(double Percent, string Stage, bool CanCancel = true);
/// <summary>实际文件清单, 文本仅对发生变更且有界的定义返回.</summary>
public sealed record ScaffoldFileRecord(string Path, string Sha256, long Bytes, string? Before = null, string? After = null);
/// <summary>实际数据文件统计, State 为 populated 或 empty.</summary>
public sealed record ScaffoldTableResult(string Name, long RowCount, long Bytes, string State);
/// <summary>所选与补入函数的预览身份, 只描述实际冻结的来源.</summary>
public sealed record ScaffoldUdfPreview(string Name, string Source, string PackageId, string Version, bool IsDependency, string? Baseline = null, string? SourceBaseline = null);
/// <summary>全部内容和目标冻结的持久计划; Digest 供用户精确批准.</summary>
public sealed record ScaffoldPlan(string Id, string Digest, DateTimeOffset ExpiresAt, string OutputPath, ScaffoldRequest Request,
    string EntryFile, IReadOnlyList<ScaffoldFileRecord> Files, IReadOnlyList<ScaffoldTableResult> Tables,
    IReadOnlyList<string> Warnings, int CompatibilityLevel, IReadOnlyList<ScaffoldUdfPreview>? UdfFunctions = null);
/// <summary>发布事实与核验/Git 分开, 后处理失败不得抹去已创建项目.</summary>
public sealed record ScaffoldResult(string PlanId, string State, bool ProjectCreated, bool Verified, bool GitInitialized,
    string ProjectPath, string? BaselineId, IReadOnlyList<ScaffoldTableResult> Tables, IReadOnlyList<string> Warnings, string? ErrorCode = null);
/// <summary>环境适配仅执行授权范围内的准备与发布.</summary>
public interface IScaffoldWorkspace : IDisposable
{
    /// <summary>在私有目录准备真实项目并冻结清单.</summary>
    Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken);
    /// <summary>只消费完整匹配的摘要和目标, 等待提交前钩子成功后才进入同步提交边界; 提交后取消不撤销结果.</summary>
    /// <param name="beforeCommit">可选可靠回执钩子, 接收准备阶段取消令牌; 失败或取消不得发布.</param>
    Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress,
        CancellationToken cancellationToken, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null);
    /// <summary>丢弃未提交计划, 不删除已发布工程.</summary>
    Task<bool> DiscardAsync(string digest, CancellationToken cancellationToken);
}
/// <summary>只提供共用业务用例, 不包含窗口或文件系统类型.</summary>
public sealed class ScaffoldService(IScaffoldWorkspace workspace, ILogger<ScaffoldService>? logger = null) : IDisposable
{
    private readonly ILogger log = logger ?? NullLogger<ScaffoldService>.Instance;
    private readonly Lock sync = new();
    private bool busy;
    private bool disposed;

    /// <summary>参数先验证, 同实例业务不堆积, 不在填写表单时持有门闩.</summary>
    public Task<ScaffoldPlan> PreviewAsync(ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress, CancellationToken token) =>
        RunAsync("Scaffold.Preview", () => { ScaffoldValidation.Validate(request); return workspace.PreviewAsync(request, output, progress, token); });

    /// <summary>所有入口都经精确计划创建, 日志不输出提案或目录.</summary>
    /// <param name="beforeCommit">透传到真实文件提交前的可等待钩子, 失败或取消不发布工程.</param>
    public Task<ScaffoldResult> CreateAsync(string digest, ScaffoldRequest request, string output, IProgress<ScaffoldProgress>? progress,
        CancellationToken token, Action? committing = null, Func<CancellationToken, Task>? beforeCommit = null) => RunAsync("Scaffold.Create", () =>
        {
            ScaffoldValidation.Validate(request);
            if (!InputValidation.IsDigest(digest)) throw new AppException("INVALID_SCAFFOLD_PLAN", "请选择有效的脚手架预览", 3);
            return workspace.CreateAsync(digest, request, output, progress, token, committing, beforeCommit);
        });

    /// <summary>关闭或改选时丢弃未发布的计划.</summary>
    public Task<bool> DiscardAsync(string digest, CancellationToken token) => RunAsync("Scaffold.Discard", () => workspace.DiscardAsync(digest, token));

    /// <summary>原子进入业务并在唯一终态后释放, 不队列积压重复点击.</summary>
    private async Task<T> RunAsync<T>(string operation, Func<Task<T>> action)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (busy) throw new AppException("SCAFFOLD_BUSY", "脚手架操作尚未结束", 3);
            busy = true;
        }
        try { return await BusinessLog.RunAsync(log, operation, action); }
        finally { lock (sync) { busy = false; if (disposed) workspace.Dispose(); } }
    }

    /// <summary>停止接受操作, 已开始调用结束后再释放适配器.</summary>
    public void Dispose() { lock (sync) { disposed = true; if (!busy) workspace.Dispose(); } }
}
