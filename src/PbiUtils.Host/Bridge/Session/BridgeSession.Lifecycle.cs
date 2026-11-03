// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Lifecycle.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一会话时限、取消、模型选择与关闭排空, 所有状态仍由同一 owner 裁决.

using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Execution;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>请求状态, 所有状态转换均在会话锁内完成.</summary>
    private sealed class Flight : IDisposable
    {
        public required string Id { get; init; }
        /// <summary>注册时冻结的方法身份, 用于撤销绑定旧文档的连接等待.</summary>
        public required string Method { get; init; }
        public CancellationTokenSource Cancellation { get; } = new();
        /// <summary>当前阶段计时器, 与请求完成路径一同释放.</summary>
        public ITimer? Timer { get; set; }
        public string? Reason { get; set; }
        public bool Committing { get; set; }
        public bool IsMcp { get; init; }
        public bool BindsConnection { get; init; }
        public long ConnectionRevision { get; set; }
        public ExecutionJournal.Execution? Execution { get; set; }
        public int TimeoutGeneration { get; set; }
        public string TimeoutStage { get; set; } = "Request";
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>释放本请求令牌.</summary>
        public void Dispose() => Cancellation.Dispose();
    }

    /// <summary>设置阶段时限; 旧阶段回调与复用 ID 的新请求均不能误取消当前请求.</summary>
    /// <param name="id">已注册的后端请求 ID.</param>
    /// <param name="flight">当前在途请求对象.</param>
    /// <param name="duration">本阶段允许的时长.</param>
    /// <param name="stage">固定安全阶段名.</param>
    /// <returns>由请求完成路径释放的计时器.</returns>
    private ITimer ArmTimeout(string id, Flight flight, TimeSpan duration, string stage)
    {
        lock (sync)
        {
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            var generation = ++flight.TimeoutGeneration;
            flight.TimeoutStage = stage;
            BusinessLog.BridgeTimeout(log, stage, duration);
            return (requestTimeProvider ?? TimeProvider.System).CreateTimer(_ =>
            {
                lock (sync)
                    if (flight.TimeoutGeneration == generation && flights.TryGetValue(id, out var current) && ReferenceEquals(current, flight))
                        Cancel(id, "TIMEOUT");
            }, null, duration, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>原子受理当前会话尚未终结的请求取消.</summary>
    /// <param name="id">目标请求 ID.</param>
    /// <param name="reason">取消或超时原因.</param>
    /// <returns>是否首次受理.</returns>
    private bool Cancel(string id, string reason)
    {
        lock (sync)
        {
            if (!flights.TryGetValue(id, out var flight) || flight.Reason is not null || flight.Committing) return false;
            flight.Reason = reason;
            BusinessLog.BridgeCancellation(log, reason, flight.TimeoutStage);
            flight.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>序列化宿主消息, 防止脚本拼接.</summary>
    /// <param name="value">响应或事件对象.</param>
    private void Send(object value) => send(JsonSerializer.Serialize(value, json));

    /// <summary>认证缓存提交与取消共用临界区, 提交之后取消返回未接受.</summary>
    private void BeginCommit(Flight flight)
    {
        lock (sync)
        {
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            RequireConnectionRevision(flight);
            flight.Execution?.CommitStarted();
            flight.Committing = true;
        }
    }

    /// <summary>模型提交开始后通知网页等待最终结果, 不用前端超时把已经提交的操作误报为取消.</summary>
    private void BeginModelCommit(string id, Flight flight)
    {
        BeginCommit(flight);
        Send(new { protocolVersion = 1, requestPhase = new { requestId = id, phase = "committing" } });
    }

    /// <summary>保护草稿, 切换、断开和重开前必须保存或明确放弃.</summary>
    private void RequireSavedModel()
    {
        if (HasPendingChanges) throw new AppException("UNSAVED_CHANGES", "当前模型有未保存的修改, 请先保存或放弃修改", 3);
    }

    /// <summary>在取消锁内提交唯一当前模型; 成功才撤销旧目标, 失败保留旧工作区.</summary>
    private void PublishProject(ProjectSnapshot snapshot, Flight flight, string? recentFile = null)
    {
        lock (sync)
        {
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            void CommitSelection()
            {
                BeginCommit(flight);
                if (recentFile is not null) recentProjects?.Record(recentFile);
            }
            // 共享模型门闩通过后才写最近记录, BUSY 或待保存状态不会留下失败连接的历史.
            if (edits is not null) edits.Select(snapshot, CommitSelection);
            else CommitSelection();
            connections?.Retain(snapshot.ProjectPath.StartsWith("model://", StringComparison.Ordinal) ? snapshot.ProjectPath : null);
            var changed = flight.Method != "project.inspect" || projectPath != snapshot.ProjectPath || workspaceId != snapshot.ProjectId;
            projectPath = snapshot.ProjectPath; workspaceId = snapshot.ProjectId; connectionKind = snapshot.Connection?.Kind ?? "pbip";
            if (changed) PublishWorkspaceChanged(snapshot, flight);
        }
    }
    /// <summary>断开只清理本窗口当前模型, Power BI 登录可供下一次连接使用.</summary>
    private void ClearProject(Flight flight)
    {
        lock (sync)
        {
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            BeginCommit(flight);
            RequireSavedModel(); edits?.Select(null);
            connections?.Disconnect(); projectPath = null; workspaceId = null; connectionKind = null;
            PublishWorkspaceChanged(null, flight);
        }
    }

    /// <summary>关闭会话时取消全部在途任务, 完成路径释放令牌.</summary>
    public async Task ShutdownAsync()
    {
        Task[] pending;
        Task<Exception?> completion;
        lock (sync)
        {
            pending = [.. flights.Values.Select(f => f.Completed.Task)];
            Dispose();
            completion = resourceCompletion.Task;
        }
        await Task.WhenAll(pending);
        if (await completion is not null) throw new AppException("SHUTDOWN_FAILED", "业务已结束但资源清理失败, 请查看诊断日志", 4);
    }

    /// <summary>关窗准备冻结新业务后取消准备阶段并排空当前请求, 不释放草稿或连接, 用户仍可取消关闭.</summary>
    public async Task CancelPendingAsync()
    {
        Task[] pending;
        lock (sync)
        {
            foreach (var id in flights.Keys.ToArray()) Cancel(id, "CANCELLED");
            pending = [.. flights.Values.Select(flight => flight.Completed.Task)];
        }
        await Task.WhenAll(pending);
    }

    /// <summary>导航撤销表单与创建身份, 取消旧文档等待、导出和统计; 原请求仍拥有模型锁直到排空, 已发布事实保留.</summary>
    public void InvalidateDocument()
    {
        lock (sync)
        {
            foreach (var item in flights.Where(item => item.Value.Method is "app.resetUserData" or "project.connectRemote" or "scaffold.openDesktop" or "model.exportTables" or "model.tableStatistics" or "model.tableRowCounts" or "model.openExportDirectory").ToArray()) Cancel(item.Key, "CANCELLED");
            remoteForm?.Invalidate(); scaffold?.Invalidate(); configuration?.Invalidate();
        }
    }

    /// <summary>关闭会话时取消全部在途任务, 完成路径释放令牌.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                if (resourceCompletion.Task.IsCompleted && shutdownFailure is not null)
                {
                    shutdownFailure = null;
                    lifecycle = "Draining";
                    resourceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var retry = resourceCompletion;
                    _ = Task.Run(() => CompleteResourceReleaseAsync(retry));
                }
                return;
            }
            disposed = true;
            lifecycle = "Stopping";
            dialogs?.CancelActive();
            remoteForm?.Invalidate();
            foreach (var id in flights.Keys.ToArray()) Cancel(id, "CANCELLED");
            // 脚手架 Shutdown 也可能同步进入文件清理, 统一放在线程池并由资源完成任务观察结果.
            scaffoldShutdown ??= Task.Run(async () => { if (scaffold is not null) await scaffold.ShutdownAsync(); });
            lifecycle = "Draining";
            if (flights.Count == 0) ReleaseResources();
        }
    }

    /// <summary>全部在途请求结束后只调度一次资源排空, 最终状态由唯一完成任务裁决.</summary>
    private void ReleaseResources()
    {
        if (resourcesReleased) return;
        resourcesReleased = true;
        var completion = resourceCompletion;
        _ = Task.Run(() => CompleteResourceReleaseAsync(completion));
    }

    /// <summary>独立拥有者全部尝试并排空后才发布终态, 前序故障不能跳过模型、配置或后续资源清理.</summary>
    /// <param name="completion">本次显式关闭唯一的完成票据, 后续重试不能改写其首次失败事实.</param>
    /// <returns>线程池上的完整清理过程; 每轮首因供公共关闭入口统一返回 SHUTDOWN_FAILED.</returns>
    private async Task CompleteResourceReleaseAsync(TaskCompletionSource<Exception?> completion)
    {
        Exception? failure = null;
        try
        {
            await BusinessLog.RunAsync(log, "Bridge.Release", async () =>
            {
                // 先启动各自拥有资源的独立清理; 共享文件工作区必须等它们全部结束后才能释放.
                connectionShutdown = RetryUnconfirmed(connectionShutdown, () => { connections?.Disconnect(); return Task.CompletedTask; });
                configurationShutdown = RetryUnconfirmed(configurationShutdown, async () => { if (configuration is not null) await configuration.DisposeAsync(); });
                modelShutdown = RetryUnconfirmed(modelShutdown, async () => { if (edits is not null) await edits.DisposeAsync(); });
                Task[] pending = [scaffoldShutdown ?? Task.CompletedTask, connectionShutdown, configurationShutdown, modelShutdown];
                foreach (var task in pending) await ObserveAsync(task);
                workspaceShutdown = RetryUnconfirmed(workspaceShutdown, () => { ownedWorkspace?.Dispose(); return Task.CompletedTask; });
                await ObserveAsync(workspaceShutdown);
                calendarShutdown = RetryUnconfirmed(calendarShutdown, () => { calendarPreviewGate.Dispose(); return Task.CompletedTask; });
                await ObserveAsync(calendarShutdown);
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            });
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            lock (sync)
            {
                shutdownFailure ??= failure;
                lifecycle = shutdownFailure is null ? "Disposed" : "ReleaseFailed";
            }
            completion.TrySetResult(failure);
        }

        /// <summary>观察单个已启动资源的真实结果, 保留首因并允许其余清理继续完成.</summary>
        async Task ObserveAsync(Task task)
        {
            try { await task; }
            catch (Exception error) { failure ??= error; }
        }
    }

    /// <summary>每轮显式关闭只重新启动未确认清理, 已成功任务保留; 脚手架关闭另由原票据排空, 不重发业务.</summary>
    /// <param name="previous">此资源上次清理任务, null 表示尚未开始.</param>
    /// <param name="cleanup">仅释放本资源的操作, 不得执行原业务或模型提交.</param>
    /// <returns>成功或在途任务原样复用, 未确认结果由线程池重新清理且保留真实异常.</returns>
    private static Task RetryUnconfirmed(Task? previous, Func<Task> cleanup) => previous is null || previous.IsCompleted && !previous.IsCompletedSuccessfully
        ? Task.Run(cleanup) : previous;

    private TaskCompletionSource<Exception?> resourceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? connectionShutdown;
    private Task? configurationShutdown;
    private Task? modelShutdown;
    private Task? workspaceShutdown;
    private Task? calendarShutdown;
}
