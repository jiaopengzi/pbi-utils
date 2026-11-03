// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义只读函数包、冻结依赖及本地函数存储契约.

namespace PbiUtils.Application.Udf;

/// <summary>官方目录中的包摘要, 缓存时间不能代表当前最新.</summary>
public sealed record UdfPackageSummary(string Id, string Version, string Description);
/// <summary>明确声明的精确包依赖.</summary>
public sealed record UdfPackageDependency(string Id, string Version);
/// <summary>保留完整限定名、表达式及原来源注解的函数.</summary>
public sealed record UdfFunction(string Name, string Expression, string Description, IReadOnlyDictionary<string, string> Annotations);
/// <summary>有界验证后的精确包, LicenseFiles 保留逐包许可原文; CommittedAt 为该版本目录最后修改提交的 UTC 提交者时间, 不可证明时为 null.</summary>
public sealed record UdfPackage(string Id, string Version, string Hash, string Description, string Readme,
    IReadOnlyDictionary<string, string> LicenseFiles, IReadOnlyList<UdfPackageDependency> Dependencies,
    IReadOnlyList<UdfFunction> Functions, DateTimeOffset RetrievedAt, bool FromCache = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] UdfPackageMetadata? Metadata = null,
    [property: System.Text.Json.Serialization.JsonRequired]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] DateTimeOffset? CommittedAt = null);
/// <summary>包内已验证的展示元数据; 图标仅为有界 PNG, 不接受远端资源.</summary>
public sealed record UdfPackageMetadata(string Authors, string[] Tags, string ReleaseNotes, string? ProjectUrl, string? RepositoryUrl, string? IconDataUrl = null);
/// <summary>包级模糊查询及逐包精确版本, Offset/Limit 按实际传输页计算.</summary>
public sealed record UdfPackageQuery(string Query, bool IncludePrerelease, int Offset = 0, int Limit = 50, IReadOnlyDictionary<string, string>? PackageVersions = null);
/// <summary>包是浏览和搜索的最小单位; CommittedAt 仅来自对应版本目录的 Git 历史, 不代表正式发布日期, 未知保持 null.</summary>
public sealed record UdfCatalogPackage(string Id, string Version, string Hash, string Description, string Authors, string[] Tags,
    DateTimeOffset? CommittedAt, string? IconKey, int FunctionCount);
/// <summary>只含包摘要的一页; 字节预算可导致短页, 后续必须使用 NextOffset.</summary>
public sealed record UdfPackagePage(IReadOnlyList<UdfCatalogPackage> Items, int Total, int? NextOffset, UdfCatalogStatus Status);
/// <summary>已经身份核验的包内图标, 缺失时 DataUrl 为 null.</summary>
public sealed record UdfCatalogIcon(string Key, string? DataUrl);
/// <summary>轻量包浏览与图标读取, 全部使用同一完整本地目录; 完整详情复用精确包读取.</summary>
public interface IUdfCatalogBrowser
{
    /// <summary>查询包身份、说明、作者、标签和函数名/说明; 基线过期、参数非法或单包超预算时失败.</summary>
    Task<UdfPackagePage> QueryPackagesAsync(UdfPackageQuery query, string? expectedBaseline, CancellationToken cancellationToken);
    /// <summary>图标身份必须匹配当前精确包, 不接受网页路径或 URL.</summary>
    Task<UdfCatalogIcon> GetIconAsync(string packageId, string version, string iconKey, string? expectedBaseline, CancellationToken cancellationToken);
}
/// <summary>冻结后的函数及其明确选择或自动补入来源.</summary>
public sealed record ResolvedUdfFunction(UdfFunction Function, string Source, string PackageId, string Version, string Baseline, bool IsDependency, string? SourceBaseline = null);
/// <summary>部分安装锁清单, 不声称整包安装.</summary>
public sealed record UdfPackageLock(string Id, string Version, string Hash, bool Partial, string[] SelectedFunctions,
    string[] DependencyFunctions, IReadOnlyDictionary<string, string> LicenseFiles, UdfPackageDependency[] Dependencies,
    string Source = "daxlib", string? SourceBaseline = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CatalogBaseline = null);
/// <summary>准备阶段冻结结果, 创建阶段不重新解析最新版本.</summary>
public sealed record UdfResolution(IReadOnlyList<ResolvedUdfFunction> Functions, IReadOnlyList<UdfPackageLock> Packages);
/// <summary>本地函数声明依赖, Source 为 local、daxlib 或宿主登记的自定义来源 ID.</summary>
public sealed record LocalUdfDependency(string Source, string PackageId, string Version, string Function, string? Baseline = null, string? SourceBaseline = null);
/// <summary>严格本地编辑输入, 单函数表达式最多 32 KiB UTF-8.</summary>
public sealed record LocalUdfDefinition(int SchemaVersion, string Name, string Version, string Expression, string Description, LocalUdfDependency[] Dependencies);
/// <summary>宿主分配稳定 ID, 基线绑定实际存储内容.</summary>
public sealed record LocalUdfEntry(string Id, string Baseline, LocalUdfDefinition Definition);
/// <summary>已由宿主绑定的单一只读来源, 查询方法不接受外部 URL.</summary>
public interface IUdfPackageProvider
{
    /// <summary>搜索包, 预发行开关只控制可见目录.</summary>
    Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken);
    /// <summary>读取可用精确版本.</summary>
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken);
    /// <summary>读取并验证本地完整快照中的精确包, 不隐式联网.</summary>
    Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken);
}
/// <summary>已完整发布的本地目录状态, 时间仅表示最近成功获取.</summary>
public sealed record UdfCatalogStatus(bool Initialized, string? Baseline, DateTimeOffset? RetrievedAt, int PackageCount, int VersionCount, int FunctionCount);
/// <summary>有界目录更新阶段及已完成数量.</summary>
public sealed record UdfCatalogProgress(string Stage, int Completed, int Total);
/// <summary>浏览只读取本地完整快照, 仅显式更新允许访问远端.</summary>
public interface IUdfCatalogProvider : IUdfPackageProvider, IUdfCatalogBrowser
{
    /// <summary>读取并验证当前完整快照状态, 缺快照返回未初始化状态.</summary>
    Task<UdfCatalogStatus> GetStatusAsync(CancellationToken cancellationToken);
    /// <summary>完整获取并原子发布; committing 之前可取消, 之后排空并返回实际结果.</summary>
    Task<UdfCatalogStatus> RefreshAsync(IProgress<UdfCatalogProgress>? progress, CancellationToken cancellationToken, Action? committing = null);

}
/// <summary>一次解析固定的完整不可变目录, 调用者必须释放读者以允许旧缓存回收.</summary>
public interface IUdfCatalogSnapshotProvider : IUdfPackageProvider, IUdfCatalogBrowser, IDisposable
{
    /// <summary>与所有读取内容同代的目录状态.</summary>
    UdfCatalogStatus Status { get; }
}
/// <summary>生产目录支持绑定单代读取, 避免解析期间混合两次刷新结果.</summary>
public interface IUdfCatalogSnapshotSource
{
    /// <summary>完整验证并固定当前目录; 未初始化、损坏或配置漂移时明确失败.</summary>
    Task<IUdfCatalogSnapshotProvider> OpenSnapshotAsync(CancellationToken cancellationToken);
}
/// <summary>来源身份由宿主分配, 基线绑定名称和规范端点; 内置来源不可改写.</summary>
public sealed record UdfSourceEntry(string Id, string Name, string? BaseUrl, string? Baseline, string Kind, string Protocol = "git", string? GitRef = null);
/// <summary>持久来源配置不联网, 所有变更均绑定稳定身份和当前基线.</summary>
public interface IUdfSourceRegistry
{
    /// <summary>列出固定来源和用户来源, 损坏或未知版本明确失败.</summary>
    Task<IReadOnlyList<UdfSourceEntry>> ListAsync(CancellationToken cancellationToken);
    /// <summary>按稳定身份获取来源.</summary>
    Task<UdfSourceEntry> GetAsync(string id, CancellationToken cancellationToken);
    /// <summary>原子新增或基线更新; committing 后排空并返回真实提交结果.</summary>
    Task<UdfSourceEntry> SaveAsync(string name, string baseUrl, string? id, string? baseline, CancellationToken cancellationToken, Action? committing = null, string protocol = "git", string? gitRef = null);
    /// <summary>基线删除用户来源, 调用者负责明确确认.</summary>
    Task DeleteAsync(string id, string baseline, CancellationToken cancellationToken, Action? committing = null);
}
/// <summary>只按宿主持久来源身份路由, 自定义来源要求选择时的配置基线.</summary>
public interface IUdfSourceProvider
{
    /// <summary>用于宿主来源管理的共享存储.</summary>
    IUdfSourceRegistry Sources { get; }
    /// <summary>返回精确来源提供者, 不跨源回退.</summary>
    Task<IUdfPackageProvider> GetProviderAsync(string source, string? baseline, CancellationToken cancellationToken);
}
/// <summary>受限本地函数 CRUD, 所有更新与删除均须当前基线.</summary>
public interface ILocalUdfStore
{
    /// <summary>读取本地清单, 损坏文件明确失败.</summary>
    Task<IReadOnlyList<LocalUdfEntry>> ListAsync(CancellationToken cancellationToken);
    /// <summary>按稳定 ID 读取.</summary>
    Task<LocalUdfEntry> GetAsync(string id, CancellationToken cancellationToken);
    /// <summary>分配新 ID 并原子创建; 名称不区分大小写, 同一精确 tag 已存在或无法完整核验本地库时拒绝写入.</summary>
    /// <param name="committing">完整校验后、原子写入前的可靠回执边界, 失败不发布定义.</param>
    Task<LocalUdfEntry> CreateAsync(LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null);
    /// <summary>复核基线后原子更新; 同一逻辑名和精确 tag 不得与其他稳定 ID 冲突, 失败保留原记录.</summary>
    /// <param name="committing">精确基线复核后、原子写入前的可靠回执边界.</param>
    Task<LocalUdfEntry> UpdateAsync(string id, string baseline, LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null);
    /// <summary>复核基线后删除, 调用者负责明确确认.</summary>
    /// <param name="committing">精确基线复核后、真实删除前的可靠回执边界.</param>
    Task DeleteAsync(string id, string baseline, CancellationToken cancellationToken, Action? committing = null);
}
