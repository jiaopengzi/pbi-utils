// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\DesktopModelAvailability.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一纯远端报表与未提供本地模型时的连接终态, 不推断云端权限.

using PbiUtils.Core;

namespace PbiUtils.Application.Connections;

/// <summary>Desktop 文件打开、实例发现与直接连接共用的可操作失败说明.</summary>
public static class DesktopModelAvailability
{
    public const string RemoteReportCode = "DESKTOP_REMOTE_REPORT";
    public const string NoLocalModelCode = "DESKTOP_NO_LOCAL_MODEL";
    public const string RemoteReportMessage = "此报表仅连接远端语义模型, 不包含本地模型. pbi-utils 无法通过此 Desktop 报表读取或编辑云端模型及其度量值. 如需只读查看, 请在 Power BI Service 中登录并选择原语义模型";
    public const string NoLocalModelMessage = "此 Desktop 实例未提供可读取的本地模型. 若报表仅实时连接 Power BI Service 语义模型, 请在 Power BI Service 中登录并选择原模型进行只读查看; 若模型仍在加载, 请加载完成后刷新";

    /// <summary>根据已核验的远端引用证据生成错误; 无证据时仅说明未提供本地模型.</summary>
    /// <param name="remoteReport">是否已确认仅有远端模型引用.</param>
    /// <returns>不含用户路径、连接串或模型名称的前置条件错误.</returns>
    public static AppException Error(bool remoteReport = false) => new(remoteReport ? RemoteReportCode : NoLocalModelCode,
        remoteReport ? RemoteReportMessage : NoLocalModelMessage, 3);
}
