// FilePath    : pbi-utils\src\PbiUtils.Application\Updates\ProductUpdateService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享软件更新用例, 独立于模型锁执行节流查询与受限链接解析.

using System.Collections.Concurrent;
using PbiUtils.Core;

namespace PbiUtils.Application.Updates;

/// <summary>客户端共享更新用例, HTTP 和存储资源的生命周期由适配器/宿主拥有.</summary>
public sealed class ProductUpdateService(IProductUpdateStore store, IProductReleaseSource releases, string currentVersion, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ProductUpdateCheck> verified = new(StringComparer.Ordinal);

    /// <summary>读取独立设置及所选来源最近检查, 不触发网络或模型操作.</summary>
    public ProductUpdateState Read() => store.Read();

    /// <summary>原子合并本次更新设置补丁, 提交前可取消; 保留其他实例修改的未提供字段.</summary>
    public ProductUpdateState Save(ProductUpdateSettingsPatch patch, CancellationToken token, Action? committing = null)
    {
        ProductUpdatePolicy.ValidatePatch(patch);
        return store.Save(patch, token, committing);
    }

    /// <summary>按来源最多24小时自动查询一次, 手动绕过; 同源并发明确失败, 外部取消不写结果.</summary>
    public async Task<ProductUpdateCheckResponse> CheckAsync(string source, bool automatic, CancellationToken token)
    {
        ProductUpdatePolicy.ValidateSource(source);
        token.ThrowIfCancellationRequested();
        if (Skip(store.Read(source), source, automatic)) return new(false, store.Read(source).LastCheck);
        IDisposable lease;
        try { lease = store.AcquireCheck(source); }
        catch (AppException error) when (error.Code == "USER_DATA_BUSY")
        { return new(true, Failed(source, "UPDATE_CHECK_BUSY")); }
        using (lease)
        {
            var state = store.Read(source);
            if (Skip(state, source, automatic)) return new(false, state.LastCheck);
            ProductUpdateCheck check;
            try
            {
                var candidates = await releases.ReadAsync(source, token).ConfigureAwait(false);
                check = ProductUpdatePolicy.Select(source, currentVersion, candidates, clock.GetUtcNow());
            }
            catch (AppException error) when (error.Code.StartsWith("UPDATE_", StringComparison.Ordinal))
            { check = Failed(source, error.Code); }
            token.ThrowIfCancellationRequested();
            if (!store.SaveCheck(check, automatic, token)) return new(false, store.Read().LastCheck);
            if (check.Status is "available" or "current") verified[source] = check;
            else verified.TryRemove(source, out _);
            return new(true, check);
        }
    }

    /// <summary>只解析当前实例已成功核验结果内的固定目标; 不接受URL或仅来自磁盘的权限.</summary>
    public Uri ResolveLink(string source, string version, string target)
    {
        var expected = ProductUpdatePolicy.Link(source, version, target);
        if (!verified.TryGetValue(source, out var check) || check.LatestVersion != version
            || (target == "release" ? check.ReleaseUrl != expected : !check.Assets.Any(asset => asset.DotNetMode == target && asset.DownloadUrl == expected)))
            throw new AppException("UPDATE_LINK_UNVERIFIED", "请先重新检查此来源的更新", 3);
        return new Uri(expected, UriKind.Absolute);
    }

    /// <summary>自动开关/来源及来源专属24小时窗口共同决定是否跳过.</summary>
    private bool Skip(ProductUpdateState state, string source, bool automatic) => automatic &&
        (!state.Settings.AutomaticCheck || state.Settings.Source != source || state.LastCheck is { } last && last.CurrentVersion == currentVersion
            && clock.GetUtcNow() - last.CheckedAtUtc < TimeSpan.FromHours(24));

    /// <summary>构建安全失败结果, 不包含异常正文和部分响应.</summary>
    private ProductUpdateCheck Failed(string source, string code) => new(source, currentVersion, "failed", clock.GetUtcNow(), null, null, "", [], code);
}
