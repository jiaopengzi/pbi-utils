// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Gui\OriginPolicy.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 校验真实消息和导航来源, 限定生产及显式开发来源.

namespace PbiUtils.Infrastructure.Gui;

/// <summary>真实来源与导航共用的精确来源策略.</summary>
public sealed class OriginPolicy
{
    /// <summary>固定的生产虚拟 HTTPS 来源.</summary>
    public const string Production = "https://app.pbi-utils.local";
    /// <summary>固定的回环开发来源.</summary>
    public const string Development = "http://127.0.0.1:5173";
    /// <summary>当前会话唯一允许来源.</summary>
    public string Origin { get; }

    /// <summary>仅在 Debug 且显式开启时接受开发配置.</summary>
    /// <param name="debug">宿主编译模式.</param>
    /// <param name="devUrl">显式开发地址.</param>
    /// <exception cref="InvalidOperationException">开发配置不合法或用于 Release.</exception>
    public OriginPolicy(bool debug, string? devUrl)
    {
        if (devUrl is not null && (!debug || devUrl != Development))
            throw new InvalidOperationException("开发地址只允许 Debug 的固定回环来源");
        Origin = devUrl ?? Production;
    }

    /// <summary>校验实际 URI 的 scheme、host、port 与凭据.</summary>
    /// <param name="source">WebView2 提供的真实来源.</param>
    /// <returns>来源是否可信.</returns>
    public bool Allows(string source) => Uri.TryCreate(source, UriKind.Absolute, out var actual)
        && actual.UserInfo.Length == 0 && actual.GetLeftPart(UriPartial.Authority) == Origin;
}
