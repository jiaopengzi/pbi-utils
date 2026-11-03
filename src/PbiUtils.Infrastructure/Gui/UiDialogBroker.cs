// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Gui\UiDialogBroker.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将宿主确认与输入请求绑定到一次性前端弹窗, 严格验证回复和取消生命周期.

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Gui;

/// <summary>宿主声明的弹窗内容与回复白名单, 数据只用于展示, 不授予业务写入权限.</summary>
/// <param name="Kind">前端已实现的固定弹窗类型.</param>
/// <param name="TitleKey">本地字典标题键.</param>
/// <param name="MessageKey">可选的本地字典正文键.</param>
/// <param name="MessageArgs">可选的正文插值参数.</param>
/// <param name="Data">当前宿主绑定的展示数据, 不包含已保存密钥或密码.</param>
/// <param name="Actions">允许的操作, 默认仅 confirm; cancel 始终允许.</param>
/// <param name="Fields">允许返回的字符串字段, 默认不接收字段.</param>
public sealed record UiDialogRequest(string Kind, string TitleKey, string? MessageKey = null,
    object? MessageArgs = null, object? Data = null, string[]? Actions = null, string[]? Fields = null);

/// <summary>一次性前端回复, 必须在原宿主操作中继续核验业务参数.</summary>
/// <param name="Action">已经宿主白名单校验的操作.</param>
/// <param name="Values">已经字段白名单和长度校验的输入.</param>
public sealed record UiDialogReply(string Action, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>诊断输出只包含操作和字段数量, 防止输入的密钥或密码被隐式格式化.</summary>
    /// <returns>不包含输入值的诊断描述.</returns>
    public override string ToString() => $"UiDialogReply {{ Action = {Action}, FieldCount = {Values.Count} }}";
}

/// <summary>单窗口弹窗通道, 随机身份和一次性受理阻止旧页面回复重放.</summary>
/// <param name="send">可信 WebView 文档的结构化消息发送器.</param>
/// <param name="interactionAvailable">后台宿主返回 false, 需要人工交互时立即报错而非等待不可见弹窗.</param>
public sealed class UiDialogBroker(Action<string> send, Func<bool>? interactionAvailable = null) : IDisposable
{
    private readonly Action<string> sendMessage = send ?? throw new ArgumentNullException(nameof(send));
    private readonly Lock sync = new();
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private Pending? active;
    private bool disposed;

    /// <summary>保存当前待答任务和宿主产生的不可预测身份.</summary>
    private sealed class Pending(string[] actions, string[] fields)
    {
        public string Id { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        public HashSet<string> Actions { get; } = new(actions, StringComparer.Ordinal);
        public HashSet<string> Fields { get; } = new(fields, StringComparer.Ordinal);
        public TaskCompletionSource<UiDialogReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; set; }
    }

    /// <summary>展示一个宿主绑定的弹窗并等待一次回复, 不占用模型操作门闩.</summary>
    /// <param name="request">宿主内容和允许的操作及输入字段.</param>
    /// <param name="token">原操作的取消或阶段超时令牌.</param>
    /// <returns>通过白名单验证的回复; 用户关闭时 Action 为 cancel.</returns>
    /// <exception cref="AppException">已经有活动弹窗.</exception>
    /// <exception cref="OperationCanceledException">原请求取消、超时或通道关闭.</exception>
    public async Task<UiDialogReply> ShowAsync(UiDialogRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        if (interactionAvailable?.Invoke() == false) throw new AppException("MCP_INTERACTION_REQUIRED", "此操作需要人工输入; 请结束 MCP 后在桌面端完成", 3);
        var actions = ValidateNames(request.Actions ?? ["confirm"], 16, 64);
        var fields = ValidateNames(request.Fields ?? [], 32, 100);
        var pending = new Pending(actions, fields);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (active is not null) throw new AppException("BUSY", "请先完成当前对话框", 3);
            active = pending;
            try
            {
                sendMessage(JsonSerializer.Serialize(new
                {
                    protocolVersion = 1,
                    uiDialog = new { id = pending.Id, request.Kind, request.TitleKey, request.MessageKey, request.MessageArgs, request.Data, actions }
                }, json));
            }
            catch { active = null; throw; }
        }
        using var cancellation = token.Register(() => Cancel(pending, token));
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>只接受当前弹窗的首次有效回复, 不记录或回传输入值.</summary>
    /// <param name="dialogId">宿主发出的弹窗身份.</param>
    /// <param name="action">允许的操作或 cancel.</param>
    /// <param name="values">字符串字段对象, 拒绝重复、未知、非字符串或超过 8192 字符的值.</param>
    /// <returns>是否首次接受当前弹窗的回复, 旧身份返回 false.</returns>
    /// <exception cref="AppException">当前弹窗的回复违反字段或操作白名单.</exception>
    public bool TryRespond(string dialogId, string action, JsonElement values)
    {
        lock (sync)
        {
            if (disposed || active is not { } pending || pending.Id != dialogId) return false;
            if (action != "cancel" && !pending.Actions.Contains(action))
                throw new AppException("VALIDATION_ERROR", "对话框操作无效");
            if (values.ValueKind != JsonValueKind.Object) throw new AppException("VALIDATION_ERROR", "对话框输入必须为对象");
            Dictionary<string, string> inputs = new(StringComparer.Ordinal);
            foreach (var field in values.EnumerateObject())
            {
                if (action == "cancel" || !pending.Fields.Contains(field.Name) || field.Value.ValueKind != JsonValueKind.String
                    || field.Value.GetString()!.Length > 8192 || !inputs.TryAdd(field.Name, field.Value.GetString()!))
                    throw new AppException("VALIDATION_ERROR", "对话框输入字段无效");
            }
            active = null;
            Close(pending);
            pending.Completion.TrySetResult(new(action, new ReadOnlyDictionary<string, string>(inputs)));
            return true;
        }
    }

    /// <summary>取消原操作尚未回答的弹窗, 旧回复随后不再受理.</summary>
    /// <returns>是否取消了一个待答弹窗.</returns>
    public bool CancelActive()
    {
        lock (sync)
        {
            if (active is not { } pending) return false;
            Cancel(pending, CancellationToken.None);
            return true;
        }
    }

    /// <summary>只判断回复是否属于本 broker 当前提示, 不泄漏字段或把未知身份转发到其他上下文.</summary>
    public bool Owns(string dialogId)
    {
        lock (sync) return !disposed && active?.Id == dialogId;
    }

    /// <summary>取消指定待答任务, 迟到的旧令牌不能关闭新的弹窗.</summary>
    /// <param name="pending">令牌绑定的原弹窗.</param>
    /// <param name="token">原操作令牌, 供取消任务关联.</param>
    private void Cancel(Pending pending, CancellationToken token)
    {
        lock (sync)
        {
            if (!ReferenceEquals(active, pending)) return;
            active = null;
            Close(pending);
            pending.Completion.TrySetCanceled(token);
        }
    }

    /// <summary>关闭事件只发送一次, WebView 已退出时仍必须完成原业务任务的排空.</summary>
    /// <param name="pending">当前终态弹窗.</param>
    private void Close(Pending pending)
    {
        if (pending.Closed) return;
        pending.Closed = true;
        try { sendMessage(JsonSerializer.Serialize(new { protocolVersion = 1, uiDialogClosed = new { id = pending.Id } }, json)); }
        catch (Exception) { /* 文档或宿主可能已释放, 不把发送失败转成输入或重放业务. */ }
    }

    /// <summary>复制宿主字段白名单, 防止外部修改数组扩大后续回复权限.</summary>
    /// <param name="names">宿主声明名称.</param>
    /// <param name="maximumCount">允许的名称数量.</param>
    /// <param name="maximumLength">每个名称的最大长度.</param>
    /// <returns>通过数量、长度与唯一性核验的独立数组.</returns>
    private static string[] ValidateNames(string[] names, int maximumCount, int maximumLength)
    {
        var copy = names.ToArray();
        if (copy.Length > maximumCount || copy.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > maximumLength)
            || copy.Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("弹窗白名单无效", nameof(names));
        return copy;
    }

    /// <summary>停止新弹窗并排空当前等待, 不保留输入值或允许迟到回复.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            CancelActive();
        }
    }
}
