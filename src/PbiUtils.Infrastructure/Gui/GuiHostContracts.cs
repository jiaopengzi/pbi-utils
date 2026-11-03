// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Gui\GuiHostContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义前台与专用业务宿主之间的文档、有限原生交互和关闭契约.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PbiUtils.Infrastructure.Gui;

/// <summary>真实来源先由 GUI 核验, 此信封仅由已认证的 GUI C# 通道生成.</summary>
public sealed record GuiHostRequest(long DocumentEpoch, JsonElement Message);
/// <summary>原调用的确定完成事实; 重置不依赖异步事件比结果先被处理.</summary>
public sealed record GuiHostCompletion(bool ResetScheduled);
/// <summary>业务消息带原始文档代际, 新页面不能收到旧操作的迟到结果.</summary>
public sealed record GuiHostMessage(long DocumentEpoch, JsonElement Message);
/// <summary>原生回调仅发往所属 GUI, 请求/文档/随机身份均不能由网页改写.</summary>
public sealed record GuiHostCallback(long DocumentEpoch, string RequestId, string CallbackId, string Kind, JsonElement Parameters);
/// <summary>原生选择器只返回单一选择或取消, 不附带可执行参数.</summary>
public sealed record GuiPathSelection(string? Path);
/// <summary>关窗票据绑定同一后台上下文, 不授予业务写入权限.</summary>
public sealed record GuiClosePreparation(string Ticket, bool HasPendingChanges);
/// <summary>当前后台窗口短期焦点作用域的精确票据.</summary>
public sealed record GuiFocusLease(string LeaseId);

/// <summary>内部 GUI 通道严格解析, 拒绝重复、未知及缺失字段.</summary>
public static class GuiHostJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 24
    };

    /// <summary>将固定类型序列化为独立 JSON 值, 调用方不保留文档生命周期.</summary>
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
