// FilePath    : pbi-utils\src\PbiUtils.Application\Updates\ProductUpdateContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义独立软件更新设置、查询结果和平台适配边界.

namespace PbiUtils.Application.Updates;

/// <summary>自动检查默认开启, 来源仅允许固定的两个仓库.</summary>
public sealed record ProductUpdateSettings(bool AutomaticCheck = true, string Source = "github");
/// <summary>仅提交用户本次修改字段, 未提供的偏好由存储锁内最新值补齐.</summary>
public sealed record ProductUpdateSettingsPatch(bool? AutomaticCheck = null, string? Source = null);
/// <summary>同一正式发布中精确匹配的完整安装包, 大小未知时为 null.</summary>
public sealed record ProductUpdateAsset(string DotNetMode, string FileName, string DownloadUrl, long? Size);
/// <summary>有界纯文本更新结果, 失败仅返回固定错误码, 不携带原始异常.</summary>
public sealed record ProductUpdateCheck(string Source, string CurrentVersion, string Status, DateTimeOffset CheckedAtUtc,
    string? LatestVersion, string? ReleaseUrl, string Notes, ProductUpdateAsset[] Assets,
    string? ErrorCode = null, bool IsSimulation = false);
/// <summary>当前设置及当前所选来源的最近结果.</summary>
public sealed record ProductUpdateState(ProductUpdateSettings Settings, ProductUpdateCheck? LastCheck);
/// <summary>是否执行本轮查询以及实际结果; 关闭或节流时可能没有缓存.</summary>
public sealed record ProductUpdateCheckResponse(bool CheckedNow, ProductUpdateCheck? Check);
/// <summary>平台提供的发布字段, 不信任其 tag、正文或附件地址.</summary>
public sealed record ProductRelease(string Tag, bool Draft, bool Prerelease, string Notes, ProductReleaseAsset[] Assets);
/// <summary>平台发布附件的候选字段, 所有地址须由选择策略精确复核.</summary>
public sealed record ProductReleaseAsset(string Name, string Url, long? Size);

/// <summary>更新数据存储与模型/界面偏好分离, 锁内读最新值再合并.</summary>
public interface IProductUpdateStore
{
    /// <summary>读取当前设置和指定来源缓存; source 为 null 时读取当前所选来源.</summary>
    ProductUpdateState Read(string? source = null);
    /// <summary>锁内合并偏好补丁, 提交前支持取消和宿主提交边界回调; 空补丁/冲突/损坏抛安全错误.</summary>
    ProductUpdateState Save(ProductUpdateSettingsPatch patch, CancellationToken token, Action? committing = null);
    /// <summary>同源检查专用跨进程租约, 冲突明确拒绝, 不占设置或模型业务锁.</summary>
    IDisposable AcquireCheck(string source);
    /// <summary>锁内合并该来源结果; 自动被关闭或切源则丢弃迟到结果并返回 false.</summary>
    bool SaveCheck(ProductUpdateCheck check, bool automatic, CancellationToken token);
}

/// <summary>HTTP 平台适配必须完整分页或明确失败, 取消传播到调用方.</summary>
public interface IProductReleaseSource
{
    /// <summary>读取固定来源的所有有界发布; 网络故障抛带固定码的 AppException.</summary>
    Task<ProductRelease[]> ReadAsync(string source, CancellationToken token);
}
