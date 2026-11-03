// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopPendingQueries.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 只读检查受信 PBIP/PBIR 的真实模型待应用查询文件, 不读取正文或处理查询.

using PbiUtils.Windows.Desktop;
using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>保存前后保护独立于 Bridge clean 标志的 Power Query 待应用状态.</summary>
public static class DesktopPendingQueries
{
    /// <summary>检查已由调用方核实 Desktop 身份的文件绑定, PBIX 不在本检测能力内.</summary>
    /// <param name="target">宿主已核实进程、启动时间和实际文件路径的目标.</param>
    /// <param name="token">只读路径解析与检查的取消令牌.</param>
    /// <returns>无待应用文件时完成, 不授予覆盖或应用查询的权限.</returns>
    /// <exception cref="AppException">存在挂起文件或无法安全解析真实工程绑定时拒绝, 不读取或修复其正文.</exception>
    public static Task RequireNoneAsync(DesktopFileTarget target, CancellationToken token) => Task.Run(async () =>
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var path = DesktopModelFileIdentity.Normalize(target.Path);
            var extension = Path.GetExtension(path);
            // 官方 Bridge 不暴露待应用查询状态, PBIX 也没有可安全读取的模型文件夹; 不猜测旁边目录.
            if (extension.Equals(".pbix", StringComparison.OrdinalIgnoreCase)) return;
            var entry = extension.Equals(".pbir", StringComparison.OrdinalIgnoreCase) ? BoundProject(path, token)
                : extension.Equals(".pbip", StringComparison.OrdinalIgnoreCase) ? DesktopModelFileIdentity.Read(path).EntryPath
                : throw new AppException("LOCAL_MODEL_FILE_UNSUPPORTED", "请选择 .pbip 或 .pbix 文件", 2);
            var snapshot = await new ProjectReader().ReadAsync(entry, token);
            var definitions = snapshot.Files.Where(file => Path.GetFileName(file.Path).Equals("definition.pbism", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (definitions.Length != 1) throw Ambiguous();
            var model = Path.GetDirectoryName(Path.Combine(Path.GetDirectoryName(entry)!, definitions[0].Path))!;
            var pending = Path.Combine(model, ".pbi", "unappliedChanges.json");
            UserDataStore.Check(pending);
            token.ThrowIfCancellationRequested();
            try { _ = File.GetAttributes(pending); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            // 沿用离线入口的存在即拒绝规则: 空壳、损坏、锁定或未知版本不能被当作查询已清洁.
            throw new AppException("DESKTOP_QUERY_CHANGES_PENDING", "Desktop 存在未应用的 Power Query 更改, 请先核对并处理后重试; 未自动应用或丢弃查询", 3);
        }
        catch (UnauthorizedAccessException) { throw new AppException("PROJECT_ACCESS_DENIED", "无法读取所选工程, 请检查权限", 3); }
        catch (IOException) { throw new AppException("PROJECT_IO_ERROR", "工程读取失败或文件正在变化", 3); }
    }, token);

    /// <summary>仅按候选 PBIP 的真实报告绑定匹配当前 PBIR, 不把唯一兄弟项目或遍历顺序当成身份.</summary>
    /// <param name="report">官方 Desktop 状态已返回并核实的完整 PBIR 路径.</param>
    /// <param name="token">有界候选遍历的取消令牌.</param>
    /// <returns>唯一精确绑定当前报告的 PBIP 入口.</returns>
    /// <exception cref="AppException">候选超过预算、绑定未知或存在多个匹配入口.</exception>
    internal static string BoundProject(string report, CancellationToken token)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(report))!;
        UserDataStore.Check(root);
        var candidates = Directory.EnumerateFiles(root, "*.pbip", SearchOption.TopDirectoryOnly).Take(129).ToArray();
        if (candidates.Length > 128) throw Ambiguous();
        List<string> matches = [];
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var identity = DesktopModelFileIdentity.Read(candidate);
            if (identity.Matches(report)) matches.Add(identity.EntryPath);
        }
        return matches.Count == 1 ? matches[0] : throw Ambiguous();
    }

    /// <summary>无法唯一证明模型绑定时使用稳定错误, 不泄露路径或候选信息.</summary>
    private static AppException Ambiguous() => new("DESKTOP_FILE_AMBIGUOUS", "无法唯一核实 Desktop 工程的模型绑定, 未处理待应用查询", 3);
}
