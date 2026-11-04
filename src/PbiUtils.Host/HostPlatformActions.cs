// FilePath    : pbi-utils\src\PbiUtils.Host\HostPlatformActions.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 保留模型和已建工程的权威文件身份校验, 平台打开仅执行已绑定目标.

using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using System.Diagnostics;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Windows;

namespace PbiUtils.Host;

internal static class HostPlatformActions
{
    /// <summary>已创建工程若在 Desktop 打开必须改走连接模型通道, 不能旁路写磁盘.</summary>
    public static async Task VerifyCreatedConfigurationAsync(ProjectSnapshot snapshot, CancellationToken token)
    {
        var identity = await Task.Run(() => DesktopModelFileIdentity.Read(snapshot.ProjectPath), token);
        var states = await new DesktopModelFileRuntime().InspectAsync(token);
        if (states.Any(state => state.ErrorCode is not null))
            throw new AppException("DESKTOP_FILE_STATE_UNKNOWN", "无法核实已打开的 Desktop 文件身份, 请恢复 Desktop Bridge 后重试", 3);
        if (states.Any(state => state.Path is not null && identity.Matches(state.Path)))
            throw new AppException("CREATED_PROJECT_OPEN_IN_DESKTOP", "该工程已在 Desktop 中打开, 请连接该模型后应用配置", 3);
    }

    /// <summary>前后核验创建时文件身份, 经同上下文连接服务打开并读取 Desktop.</summary>
    public static async Task<ProjectSnapshot> OpenCreatedDesktopAsync(string path, string creationIdentity,
        ModelConnectionService connections, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ProjectFileIdentity.Read(path) != creationIdentity) throw CreatedUnavailable();
        var snapshot = await connections.FileAsync(path, token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (ProjectFileIdentity.Read(path) != creationIdentity) throw CreatedUnavailable();
            return snapshot;
        }
        catch { connections.Forget(snapshot.ProjectPath); throw; }
    }

    /// <summary>回执绑定的项目文件没有被替换时才解析其目录, 不重新采样授权.</summary>
    public static string CreatedDirectory(string path, string creationIdentity)
    {
        if (ProjectFileIdentity.Read(path) != creationIdentity) throw CreatedUnavailable();
        return PbipProjectLayout.ProjectRoot(path);
    }

    /// <summary>后台核验目录后由 GUI 执行受限回调; 无 GUI 的显式工具也只打开该绑定目录.</summary>
    public static async Task OpenDirectoryAsync(string directory, HostGuiInteractions? gui, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        UserDataStore.Check(directory);
        if (!Directory.Exists(directory)) throw new AppException("DIRECTORY_UNAVAILABLE", "目录不存在或不可用", 3);
        if (gui is not null) await gui.OpenDirectoryAsync(directory, token);
        else
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true, Verb = "open" });
        }
    }

    /// <summary>MCP 显式打开固定学习站点, 不依赖 GUI, 也不接受工具提供的网址或程序参数.</summary>
    public static Task OpenLearningSiteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo { FileName = LearningViewRequest.Site, UseShellExecute = true });
        return Task.CompletedTask;
    }

    /// <summary>仅 GUI 显式点击打开固定仓库发布根页, 不接受资产或其他网页地址.</summary>
    /// <param name="uri">共享策略根据所选来源构造的 releases 根地址.</param>
    /// <param name="token">进入系统打开之前的取消令牌.</param>
    /// <returns>已向系统浏览器提交打开请求; 无有效目标或系统打开失败时抛错.</returns>
    public static Task OpenUpdateLinkAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.Host is not ("github.com" or "gitee.com") || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/jiaopengzi/pbi-utils/releases" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new AppException("UPDATE_LINK_UNAVAILABLE", "软件发布页面地址无效", 3);
        using var process = Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        return Task.CompletedTask;
    }

    private static AppException CreatedUnavailable() => new("CREATED_PROJECT_NOT_AVAILABLE", "请先创建并核验工程", 3);
}
