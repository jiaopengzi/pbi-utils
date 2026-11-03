// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Editing\DesktopConfigurationCommitGuard.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将全部 Desktop 最终提交与配置资产回执核验串行化, 关闭跨引擎检查后提交竞态.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

namespace PbiUtils.Windows.Models.Editing;

/// <summary>持有到原模型提交、完整回读及资产尾部排空; 无未知资产记录时不要求 Desktop 文件能力.</summary>
public sealed class DesktopConfigurationCommitGuard : IDisposable
{
    private readonly string directory;
    private readonly FileStream lease;
    private bool disposed;
    /// <summary>当前已核实工程的完成槽清理失败仅告警, 不表示模型结果未知.</summary>
    public Exception? CleanupFailure { get; private set; }

    /// <summary>在宿主事务父目录取得全局配置提交锁, 另一实例在途时明确拒绝, 不忙等或重放提交.</summary>
    private DesktopConfigurationCommitGuard(string transactionRoot)
    {
        directory = Path.GetDirectoryName(Path.GetFullPath(transactionRoot))!;
        UserDataStore.Check(directory); Directory.CreateDirectory(directory);
        lease = UserDataStore.Lock(Path.Combine(directory, "configuration-assets.commit.lock"));
    }

    /// <summary>取得最终提交锁后重新核验未知资产事务, 成功返回的锁由调用方持有到提交后排空.</summary>
    /// <param name="transactionRoot">宿主当前模型的事务目录, 同一用户全部 Desktop 模型共享其父目录.</param>
    /// <param name="baseline">本轮原引擎的完整提交前基线.</param>
    /// <param name="resolveRoot">有未知事务或completed清理候选时读取官方文件绑定, 不使用模型或网页声明的路径.</param>
    /// <param name="token">锁定和只读核验期间的取消令牌, 不控制后续已开始的模型提交.</param>
    /// <returns>已核验并仍独占的提交 guard, 失败会先释放锁.</returns>
    /// <exception cref="AppException">其他提交在途、无法核实真实文件绑定或同工程未知提交未能回读证明.</exception>
    public static async Task<DesktopConfigurationCommitGuard> AcquireAsync(string transactionRoot, string baseline,
        Func<CancellationToken, Task<string?>> resolveRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var guard = new DesktopConfigurationCommitGuard(transactionRoot);
        try
        {
            var pending = DesktopConfigurationAssetTransaction.HasPendingTransactions(transactionRoot);
            var cleanupCandidate = false;
            try { cleanupCandidate = DesktopConfigurationAssetTransaction.HasCompletedCleanupCandidates(transactionRoot); }
            catch (Exception cleanup) when (!pending) { guard.CleanupFailure = cleanup; }
            if (pending || cleanupCandidate)
            {
                string? root = null;
                try { root = await resolveRoot(token); }
                catch (Exception cleanup) when (!pending && !token.IsCancellationRequested) { guard.CleanupFailure = cleanup; }
                token.ThrowIfCancellationRequested();
                if (root is null)
                {
                    if (pending) throw new AppException("EXECUTION_NEEDS_VERIFICATION", "存在未核验配置资产事务且无法核实当前工程绑定, 请重新读取核验; 不得重复提交", 4);
                }
                else
                {
                    if (pending) guard.CleanupFailure = DesktopConfigurationAssetTransaction.VerifyPending(transactionRoot, baseline, root, guard);
                    else guard.CleanupFailure = DesktopConfigurationAssetTransaction.RetryCompletedCleanup(transactionRoot, root, guard);
                }
            }
            token.ThrowIfCancellationRequested();
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    /// <summary>资产事务或只读恢复入口在自行负责精确工程核验时取得同一 guard.</summary>
    internal static DesktopConfigurationCommitGuard AcquireUnverified(string transactionRoot) => new(transactionRoot);

    /// <summary>拒绝借用已释放或属于另一宿主事务根的 guard, 不把任意对象当作锁持有证明.</summary>
    internal void RequireActive(string transactionRoot)
    {
        if (disposed || !directory.Equals(Path.GetDirectoryName(Path.GetFullPath(transactionRoot)), StringComparison.OrdinalIgnoreCase))
            throw new AppException("CONFIGURATION_ASSET_CONFLICT", "配置资产提交锁已失效或不属于当前宿主", 3);
    }

    /// <summary>排空后释放文件锁; 未知提交回执继续持久保留, 不执行资产或模型回滚.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lease.Dispose();
    }
}
