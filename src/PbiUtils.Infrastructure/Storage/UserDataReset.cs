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
        if (!Path.IsPathFullyQualified(root)) throw new AppException("UNSAFE_USER_DATA_PATH", "无法定位当前用户的数据目录", 3);
        this.root = Path.GetFullPath(root);
        if (Path.GetFileName(this.root) != ".pbi-utils") throw new ArgumentException("重置目标必须为产品用户数据目录", nameof(root));
        UserDataStore.Check(this.root);
    }

    /// <summary>在线程池只读核验待重置内容与执行账本; 损坏或未知提交拒绝重置, 不加载凭据.</summary>
    /// <param name="token">仅取消检查, 不进行删除.</param>
    public Task ValidateAsync(CancellationToken token) => Task.Run(() => Validate(includeRuntime: false, token), token);

    /// <summary>宿主与日志均释放后取得独占会话锁, 核验并清空应用数据, 保留运行协调目录.</summary>
    /// <param name="token">首个删除前可取消; 删除开始后由宿主排空至终态.</param>
    /// <exception cref="AppException">其他宿主、文件锁、链接或待核验账本阻止重置; 删除期间失败明确报告可能只完成部分清理.</exception>
    public Task ResetAsync(CancellationToken token) => Task.Run(() =>
    {
        using var session = UserDataSessionLease.Acquire(root, exclusive: true);
        Clear(removeRoot: false, token);
    }, token);

    /// <summary>仅由明确选择清理的卸载入口调用, 阻止新会话并删除包括 runtime 在内的固定数据根.</summary>
    /// <param name="token">首个删除前可取消; 首次删除后处理到终态.</param>
    /// <exception cref="AppException">仍在使用的数据或协调文件、链接及未知回执阻止删除.</exception>
    public Task RemoveForUninstallAsync(CancellationToken token) => Task.Run(() =>
    {
        // 卸载由 Windows 内核锁阻止打开新租约, 本身不打开待删除的 runtime 文件.
        using var gate = UserDataRuntimeGate.Enter(root);
        Clear(removeRoot: true, token);
    }, token);

    /// <summary>持重置租约或卸载互斥锁时清空内容, 卸载用固定的原句柄删除数据根.</summary>
    private void Clear(bool removeRoot, CancellationToken token)
    {
        Validate(includeRuntime: removeRoot, token);
        // 所有内容与祖先身份在首次删除前固定, 原句柄贯穿账本回核和删除, 不留下释放预检锁后的路径替换空隙.
        using var tree = new UserDataResetTree(root, token, removeRoot);
        tree.ValidateReceipts(token);
        token.ThrowIfCancellationRequested();
        tree.DeleteContents();
        if (removeRoot) tree.DeleteRoot();
    }

    /// <summary>先核验各目录和文件再进入子树, 不跟随链接; 卸载一并核验 runtime 子树.</summary>
    private void Validate(bool includeRuntime, CancellationToken token)
    {
        UserDataStore.Check(root);
        if (File.Exists(root)) throw new AppException("UNSAFE_USER_DATA_PATH", "用户数据根不是目录, 不允许重置", 3);
        if (!Directory.Exists(root)) return;
        var runtime = RuntimeEnvironment.RuntimeCoordinationDirectory(root);
        void Visit(string directory)
        {
            token.ThrowIfCancellationRequested();
            UserDataStore.Check(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                UserDataStore.Check(path);
                if (!includeRuntime && string.Equals(path, runtime, StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) Visit(path);
            }
        }
        Visit(root);
        new ScaffoldPlanStore(root).RequireResetSafe(token);
        new ExecutionJournal(root).RequireResetSafe();
    }
}
