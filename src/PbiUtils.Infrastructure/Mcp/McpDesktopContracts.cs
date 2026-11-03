// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpDesktopContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 保留共享宿主 MCP 上下文身份与真实业务结果分类, 不包含旧桌面进程发现或管道实现.

namespace PbiUtils.Infrastructure.Mcp;

/// <summary>当前客户端独占的共享后台上下文, 不含模型、路径或凭据正文.</summary>
/// <param name="Id">不可跨客户端或重连复用的上下文身份.</param>
/// <param name="ProcessId">承载此上下文的共享后台进程.</param>
/// <param name="Title">业务显示名称, 未提供时为空.</param>
/// <param name="Background">是否由无 UI 后台承载.</param>
public sealed record McpDesktopSession(string Id, int ProcessId, string Title, bool Background);

/// <summary>宿主实际业务值及类型化结果分类, 不将部分失败冒充完整成功.</summary>
/// <param name="Value">原始业务结果, 保留已完成阶段与未完成事实.</param>
/// <param name="IsError">业务结果是否仍含失败.</param>
/// <param name="ErrorCode">稳定业务错误码, 成功时为空.</param>
/// <param name="NeedsVerification">真实提交、回读或回执是否仍待核验.</param>
/// <param name="ConnectionRevision">本次业务实际观察的连接代际, 无关操作或失败不授予新目标身份.</param>
public sealed record McpDesktopResult(object? Value, bool IsError, string? ErrorCode, bool NeedsVerification, long? ConnectionRevision = null);
