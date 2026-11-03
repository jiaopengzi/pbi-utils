// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Storage\UserDataReset.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在宿主完全释放资源后清空固定用户数据根, 删除前整体验证链接、回执与占用.

using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;

using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Infrastructure.Storage;

/// <summary>只管理固定用户数据根的清理; 生产构造不接受网页目录, 测试通过内部构造使用隔离目录.</summary>
public sealed class UserDataReset
{
    private readonly string root;

    /// <summary>绑定当前操作系统用户的唯一产品数据目录, 不创建目录.</summary>
    public UserDataReset() : this(RuntimeEnvironment.UserDataDirectory()) { }

    /// <summary>测试指定随机隔离根, 仍要求末级目录为 .pbi-utils 并拒绝根目录及链接.</summary>
    internal UserDataReset(string root)
    {
        this.root = Path.GetFullPath(root);
        if (Path.GetFileName(this.root) != ".pbi-utils") throw new ArgumentException("重置目标必须为产品用户数据目录", nameof(root));
        UserDataStore.Check(this.root);
    }

    /// <summary>在线程池只读枚举整个根并验证当前脚手架执行账本, 避免大目录阻塞宿主消息泵; 损坏或未知提交拒绝重置, 不加载凭据或其他设置.</summary>
    /// <param name="token">仅取消检查, 不进行删除.</param>
    public async Task ValidateAsync(CancellationToken token)
    {
        _ = await Task.Run(() => Snapshot(token), token);
        new ScaffoldPlanStore(root).RequireResetSafe(token);
        new ExecutionJournal(root).RequireResetSafe();
    }

    /// <summary>宿主与日志均释放后取得独占会话锁, 在首次删除前完整检查全部文件占用和账本, 失败不冒充完成.</summary>
    /// <param name="token">首个删除前可取消; 删除开始后由宿主排空至终态.</param>
    /// <exception cref="AppException">其他宿主、文件锁、链接或待核验账本阻止重置; 删除期间失败明确报告可能只完成部分清理.</exception>
    public async Task ResetAsync(CancellationToken token)
    {
        using var session = UserDataSessionLease.Acquire(root, exclusive: true);
        await ValidateAsync(token);
        // 所有内容与祖先身份在首次删除前固定, 原句柄贯穿账本回核和删除, 不留下释放预检锁后的路径替换空隙.
        using var tree = new UserDataResetTree(root, token);
        tree.ValidateReceipts(token);
        token.ThrowIfCancellationRequested();
        tree.DeleteContents();
    }

    /// <summary>先验证每个目录再单层枚举, 不递归跟随 reparse; 排序后的快照用于删除前复核和由深到浅删除.</summary>
    private List<Entry> Snapshot(CancellationToken token)
    {
        UserDataStore.Check(root);
        List<Entry> entries = [];
        if (File.Exists(root)) throw new AppException("UNSAFE_USER_DATA_PATH", "用户数据根不是目录, 不允许重置", 3);
        if (!Directory.Exists(root)) return entries;
        void Visit(string directory)
        {
            token.ThrowIfCancellationRequested();
            UserDataStore.Check(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                UserDataStore.Check(path);
                var isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                entries.Add(new(path, isDirectory));
                if (isDirectory) Visit(path);
            }
        }
        Visit(root);
        return entries;
    }

    private sealed record Entry(string Path, bool Directory);
}
