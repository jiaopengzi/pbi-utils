// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Gui\LearningViewRequest.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 学习网站固定来源与原生显示区域的受限输入, 不携带地址或宿主能力.

using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Gui;

/// <summary>Vue 指定的 CSS 显示区域; 视口尺寸用于宿主独立换算 DPI, 不接受路径或地址.</summary>
/// <param name="Visible">是否显示外站视图.</param>
/// <param name="Left">显示区域左坐标.</param>
/// <param name="Top">显示区域上坐标.</param>
/// <param name="Width">区域宽度.</param>
/// <param name="Height">区域高度.</param>
/// <param name="ViewportWidth">当前网页视口宽度.</param>
/// <param name="ViewportHeight">当前网页视口高度.</param>
public sealed record LearningViewRequest(bool Visible, double Left, double Top, double Width, double Height, double ViewportWidth, double ViewportHeight)
{
    /// <summary>唯一可在独立学习视图打开的顶级站点, 不是应用可信来源.</summary>
    public const string Site = "https://jiaopengzi.com/";

    /// <summary>限定 HTTPS、精确主机、默认端口与无凭据, 拒绝伪造域名及其他协议.</summary>
    /// <param name="address">真实导航目标.</param>
    /// <returns>仅学习站点自身地址返回 true.</returns>
    public static bool AllowsNavigation(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "jiaopengzi.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    /// <summary>拒绝非有限值、负坐标、越界和异常视口; 隐藏请求仍必须使用完整严格 DTO.</summary>
    /// <exception cref="AppException">输入不能用于当前窗口布局.</exception>
    public void Validate()
    {
        if (new[] { Left, Top, Width, Height, ViewportWidth, ViewportHeight }.Any(value => !double.IsFinite(value) || value < 0 || value > 32768)
            || ViewportWidth < 1 || ViewportHeight < 1 || Left + Width > ViewportWidth + 1 || Top + Height > ViewportHeight + 1
            || Visible && (Width < 1 || Height < 1))
            throw new AppException("VALIDATION_ERROR", "学习区域参数无效");
    }
}
