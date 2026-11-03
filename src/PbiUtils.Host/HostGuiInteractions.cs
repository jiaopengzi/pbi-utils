// FilePath    : pbi-utils\src\PbiUtils.Host\HostGuiInteractions.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将已核验业务需要的有限原生交互发送到所属 GUI, 绑定文档和请求并等待实际结果.

using PbiUtils.Windows.Desktop;
using PbiUtils.Application.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;
using PbiUtils.Windows;

namespace PbiUtils.Host;

/// <summary>逐 GUI 连接的交互适配; MCP/CLI 上下文没有此能力.</summary>
internal sealed class HostGuiInteractions(BusinessHostPeer peer, ILogger<HostGuiInteractions> logger)
{
    private readonly ILogger<HostGuiInteractions> log = logger;
    private readonly AsyncLocal<RequestScope?> current = new();
    internal sealed record RequestScope(long Epoch, string RequestId);

    /// <summary>冻结当前异步调用的文档与业务请求, SDK 线程池延续同一上下文.</summary>
    public IDisposable Enter(long epoch, string requestId)
    {
        var previous = current.Value;
        current.Value = new(epoch, requestId);
        return new Scope(() => current.Value = previous);
    }

    /// <summary>当前发送事件使用原请求文档, 非请求事件显式使用宿主最新文档.</summary>
    public long? CurrentEpoch => current.Value?.Epoch;

    /// <summary>仅请求固定用途的文件选择, 返回取消或前台真实选择路径.</summary>
    public Task<string?> SelectProjectAsync(CancellationToken token) => SelectAsync("selectProject", new { }, token);
    /// <summary>只请求示例目录选择, 后台仍重新核验目录和生成归属.</summary>
    public Task<string?> SelectSampleAsync(CancellationToken token) => SelectAsync("selectSample", new { }, token);
    /// <summary>只请求新建父目录, 不接受客户端脚本或任意程序参数.</summary>
    public Task<string?> SelectScaffoldAsync(CancellationToken token) => SelectAsync("selectScaffold", new { }, token);
    /// <summary>导出位置与已核验格式/表集合关联, 选择阶段不写文件.</summary>
    public Task<string?> SelectExportAsync(ModelTableExportRequest request, CancellationToken token) => SelectAsync("selectExport", request, token);

    /// <summary>打开后台已核验目录, 前台仍按固定目录动作检查.</summary>
    public Task OpenDirectoryAsync(string directory, CancellationToken token) => InvokeVoidAsync("openDirectory", new { directory }, token);
    /// <summary>布局只使用固定学习 DTO, 不提供 URL 或脚本输入.</summary>
    public Task UpdateLearningAsync(LearningViewRequest bounds, CancellationToken token) => InvokeVoidAsync("learningUpdate", bounds, token);
    /// <summary>关闭所属 GUI 的学习视图, 不影响其他窗口.</summary>
    public Task CloseLearningAsync(CancellationToken token) => InvokeVoidAsync("learningClose", new { }, token);
    /// <summary>仅打开编译固定的学习网站.</summary>
    public Task OpenLearningAsync(CancellationToken token) => InvokeVoidAsync("learningOpen", new { }, token);

    /// <summary>由真实模型目标请求所属前台短期焦点辅助; 辅助失败不能阻断模型提交.</summary>
    public async Task<IAsyncDisposable> BeginFocusAsync(DesktopProcessTarget target)
    {
        var scope = current.Value;
        if (scope is null) return EmptyLease.Instance;
        try
        {
            var value = await InvokeAsync("focusBegin", new { target.ProcessId, started = target.Started }, CancellationToken.None);
            var lease = value.Deserialize<GuiFocusLease>(GuiHostJson.Options) ?? throw new AppException("INVALID_CALLBACK", "焦点回复无效", 3);
            return new FocusLease(this, scope, lease.LeaseId);
        }
        catch (Exception error) when (error is AppException or IOException or JsonException or OperationCanceledException)
        {
            using var operation = BusinessLog.Begin(log, "Host.GuiFocus");
            operation.Fail(error);
            return EmptyLease.Instance;
        }
    }

    private async Task<string?> SelectAsync<T>(string kind, T parameters, CancellationToken token)
    {
        var value = await InvokeAsync(kind, parameters, token);
        return (value.Deserialize<GuiPathSelection>(GuiHostJson.Options) ?? throw new AppException("INVALID_CALLBACK", "选择回复无效", 3)).Path;
    }

    private async Task InvokeVoidAsync<T>(string kind, T parameters, CancellationToken token)
    {
        var value = await InvokeAsync(kind, parameters, token);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1
            || !value.TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.True)
            throw new AppException("INVALID_CALLBACK", "原生交互结果无效", 3);
    }

    private Task<JsonElement> InvokeAsync<T>(string kind, T parameters, CancellationToken token)
    {
        var scope = current.Value ?? throw new AppException("GUI_INTERACTION_UNAVAILABLE", "当前请求没有所属前台交互上下文", 3);
        var callback = new GuiHostCallback(scope.Epoch, scope.RequestId,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), kind, GuiHostJson.Element(parameters));
        return peer.InvokeAsync("gui.callback", GuiHostJson.Element(callback), token);
    }

    private sealed class Scope(Action exit) : IDisposable
    {
        public void Dispose() => exit();
    }

    private sealed class EmptyLease : IAsyncDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FocusLease(HostGuiInteractions owner, RequestScope scope, string leaseId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var request = owner.Enter(scope.Epoch, scope.RequestId);
            try { await owner.InvokeVoidAsync("focusEnd", new GuiFocusLease(leaseId), CancellationToken.None); }
            catch (Exception error) when (error is AppException or IOException or JsonException or OperationCanceledException)
            {
                using var operation = BusinessLog.Begin(owner.log, "Host.GuiFocusRelease");
                operation.Fail(error);
            }
        }
    }
}
