// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Catalog\UdfCatalogSnapshotStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 保存不可变函数包与轻量索引, 精确读取才校验对应包体.

using PbiUtils.Infrastructure.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Udf.Packages;

namespace PbiUtils.Infrastructure.Udf.Catalog;

/// <summary>磁盘职责独立于网络刷新, 成功发布后仅回收已退役且无读者的旧代.</summary>
internal sealed partial class UdfCatalogSnapshotStore
{
    private const int MaxBytes = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string folder;
    private readonly string pointer;
    private readonly UdfSourceEntry source;

    /// <summary>路径只由宿主来源身份和配置基线摘要组成, 不采用远端名称.</summary>
    internal UdfCatalogSnapshotStore(string root, UdfSourceEntry source)
    {
        this.source = source;
        folder = Path.Combine(root, "udf", "catalog", Digest(Encoding.UTF8.GetBytes(source.Id + "\n" + source.Baseline + "\n" + source.Protocol + "\n" + source.BaseUrl + "\n" + source.GitRef)));
        pointer = Path.Combine(folder, "current.json");
    }

    /// <summary>同来源跨进程独占刷新, 读者只持有固定代的共享只读句柄.</summary>
    internal FileStream LockRefresh()
    {
        UserDataStore.Check(folder); Directory.CreateDirectory(folder); UserDataStore.Check(folder);
        return UserDataStore.Lock(Path.Combine(folder, "refresh.lock"));
    }

    /// <summary>只读取一次当前指针和有界索引, 不加载无关包的表达式或许可正文.</summary>
    internal Snapshot? Read()
    {
        // 清理与固定读者竞争时, 只重读已变化的当前指针, 不恢复或搜索任何旧代.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var head = ReadHead();
            if (head is null) return null;
            try { return ReadGeneration(head.Value.Head) with { Revision = head.Value.Revision }; }
            catch (AppException error) when (error.Code == "UDF_CATALOG_INVALID" && ReadHead()?.Revision != head.Value.Revision) { }
        }
        throw Changed();
    }

    /// <summary>校验当前索引及必需仓库追溯并固定不可变代; 旧索引格式保留文件但拒绝使用.</summary>
    private Snapshot ReadGeneration(Pointer current)
    {
        FileStream? lease = null;
        try
        {
            var generation = Path.Combine(folder, current.Generation);
            var manifestPath = Path.Combine(generation, "manifest.json");
            UserDataStore.Check(manifestPath);
            lease = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (lease.Length > 16 * 1024 * 1024) throw Invalid();
            var manifestBytes = new byte[(int)lease.Length]; lease.ReadExactly(manifestBytes);
            if (Digest(manifestBytes) != current.ManifestHash || current.Baseline != current.ManifestHash) throw Invalid();
            using var document = UdfJson.Parse(manifestBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion)) throw Invalid();
            var repository = ReadRepository(generation, current.RepositoryHash);
            if (schemaVersion is 2 or 3 or 4) throw new AppException("UDF_CATALOG_UPDATE_REQUIRED", "函数缓存展示格式已更新, 请显式更新来源以建立包目录", 3);
            var manifest = document.RootElement.Deserialize<Manifest>(Json) ?? throw Invalid();
            if (manifest.SchemaVersion != 5 || manifest.Packages is null || manifest.Packages.Length > 4096) throw Invalid();
            var entries = manifest.Packages;
            ValidateIndex(entries);
            var snapshot = new Snapshot(current.Baseline, current.RetrievedAt, entries, item => LoadPackage(generation, item, current.RetrievedAt), repository, lease);
            lease = null;
            return snapshot;
        }
        catch (Exception exception) when (exception is JsonException or IOException or FormatException or ArgumentException or NullReferenceException)
        { throw Invalid(); }
        finally { lease?.Dispose(); }
    }

    /// <summary>显式更新绑定完整指针修订; 当前格式完整核验, 已停用的索引仅允许整体重建, 不读取其包体或迁移内容.</summary>
    internal string? ReadRefreshRevision(CancellationToken token)
    {
        var head = ReadHead();
        if (head is null) return null;
        token.ThrowIfCancellationRequested();
        try
        {
            using var snapshot = ReadGeneration(head.Value.Head);
            snapshot.ReadAll(token);
        }
        catch (AppException error) when (error.Code == "UDF_CATALOG_UPDATE_REQUIRED")
        {
            // 格式标记、索引字节摘要、当前指针和仓库追溯已核验; 仅显式刷新可整体替换不支持的索引.
        }
        token.ThrowIfCancellationRequested();
        return head.Value.Revision;
    }

    /// <summary>精确包正文必须匹配索引摘要和身份, 损坏不能降级到其他版本.</summary>
    private static UdfPackage LoadPackage(string generation, PackageFile item, DateTimeOffset retrievedAt)
    {
        try
        {
            if (!Hash(item.Digest)) throw Invalid();
            UdfValidation.PackageIdentity(item.Id, item.Version);
            var bytes = ReadBounded(Path.Combine(generation, item.Digest + ".json"), MaxBytes);
            if (Digest(bytes) != item.Digest) throw Invalid();
            var package = Deserialize<UdfPackage>(bytes);
            if (package.Id != item.Id || package.Version != item.Version || item.Hash is not null && package.Hash != item.Hash
                || package.Functions is not { Count: > 0 and <= 128 } || package.Dependencies is null || package.LicenseFiles is null
                || package.Description is null || package.Readme is null || package.Metadata is { Tags: null } || package.CommittedAt is { } committed && committed.Offset != TimeSpan.Zero) throw Invalid();
            if (item.Functions is not null)
            {
                var actual = Index(package, item.Digest);
                if (actual.Description != item.Description || actual.IconKey != item.IconKey || actual.Authors != item.Authors || actual.CommittedAt != item.CommittedAt ||
                    !actual.Tags!.SequenceEqual(item.Tags!) || !actual.Functions!.SequenceEqual(item.Functions)) throw Invalid();
            }
            return package with { RetrievedAt = retrievedAt, FromCache = true };
        }
        catch (Exception exception) when (exception is JsonException or IOException or FormatException or ArgumentException or NullReferenceException)
        { throw Invalid(); }
    }

    /// <summary>先写并验证独立代; 必需仓库追溯单独受指针摘要保护, 不改变包内容基线; 取消不改已发布指针.</summary>
    internal byte[] Stage(IReadOnlyList<UdfPackage> packages, UdfRepositoryRevision repository, CancellationToken token)
    {
        Validate(packages);
        ValidateRepository(repository);
        var generation = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        UserDataStore.Check(generation); Directory.CreateDirectory(generation); UserDataStore.Check(generation);
        try
        {
            var files = new List<PackageFile>(); long total = 0;
            foreach (var package in packages.OrderBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var normalized = package with { RetrievedAt = DateTimeOffset.UnixEpoch, FromCache = false, CommittedAt = package.CommittedAt?.ToUniversalTime() };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, Json);
                if ((total += bytes.Length) > MaxBytes) throw new AppException("UDF_CATALOG_LIMIT", "完整函数目录超出 256 MiB 限制");
                var digest = Digest(bytes);
                UserDataStore.Write(Path.Combine(generation, digest + ".json"), bytes);
                files.Add(Index(normalized, digest));
            }
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest(5, [.. files]), Json);
            if (manifest.Length > 16 * 1024 * 1024) throw new AppException("UDF_CATALOG_LIMIT", "完整函数目录索引超出限制");
            UserDataStore.Write(Path.Combine(generation, "manifest.json"), manifest);
            var hash = Digest(manifest);
            var revision = JsonSerializer.SerializeToUtf8Bytes(repository, Json);
            if (revision.Length > 4096) throw Invalid();
            UserDataStore.Write(Path.Combine(generation, "repository.json"), revision);
            var repositoryHash = Digest(revision);
            token.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToUtf8Bytes(new Pointer(1, Path.GetFileName(generation), hash, hash, DateTimeOffset.UtcNow, repositoryHash), Json);
        }
        catch (Exception original)
        {
            try { DeleteOwnedGeneration(generation); }
            catch (Exception cleanup) { throw CleanupFailure(original, cleanup); }
            throw;
        }
    }

    /// <summary>只清理本轮失败且未发布候选; 无失败代表已成功提交, 当前引用的目录在此入口保留.</summary>
    internal void CleanupCandidate(byte[] candidate, Exception? original)
    {
        // 已跨原子发布边界但回读未确认时保留候选; 指针丢失也不能证明此代从未发布.
        if (original is null or AppException { Code: "UDF_CATALOG_UNCONFIRMED" or "UDF_CATALOG_CLEANUP_FAILED" }) return;
        try
        {
            var staged = Deserialize<Pointer>(candidate);
            ValidatePointer(staged, candidate: true);
            var published = ReadHead()?.Head;
            if (published?.Generation == staged.Generation) return;
            DeleteOwnedGeneration(Path.Combine(folder, staged.Generation));
        }
        catch (Exception cleanup) { throw CleanupFailure(original, cleanup); }
    }

    /// <summary>保持首错误/取消退出码并附加安全清理原因, 不泄露路径.</summary>
    private static Exception CleanupFailure(Exception? original, Exception cleanup)
    {
        var detail = "本轮候选目录清理失败 (" + cleanup.GetType().Name + ").";
        Exception result = original switch
        {
            AppException first => new AppException(first.Code, first.Message + "; " + detail, first.ExitCode),
            OperationCanceledException cancelled => new OperationCanceledException("目录更新已取消; " + detail, cancelled, cancelled.CancellationToken),
            null => new AppException("UDF_CATALOG_CLEANUP_FAILED", detail, 4),
            _ => new AppException("UDF_CATALOG_REFRESH_FAILED", "目录更新失败; " + detail, 4)
        };
        UdfCleanupNotice.Mark(result, messageIncludesNotice: true); return result;
    }

    /// <summary>删除前核绝对父路径、GUID及所有后代无链接, 目标仅为本轮候选.</summary>
    private void DeleteOwnedGeneration(string generation)
    {
        var target = Path.GetFullPath(generation);
        if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) throw Invalid();
        UserDataStore.Check(target);
        if (!Directory.Exists(target)) return;
        foreach (var item in Directory.EnumerateFileSystemEntries(target))
        { UserDataStore.Check(item); if (Directory.Exists(item)) throw Invalid(); }
        Directory.Delete(target, true);
    }

    /// <summary>建立无表达式的受摘要保护索引, 图标本体仍留在精确包文件.</summary>
    private static PackageFile Index(UdfPackage package, string digest) => new(package.Id, package.Version, digest, package.Hash, package.Description,
        [.. package.Functions.Select(function => new FunctionFile(function.Name, function.Description))],
        package.Metadata?.IconDataUrl is { } icon ? Digest(Encoding.UTF8.GetBytes(icon)) : null,
        package.Metadata?.Authors ?? "", package.Metadata?.Tags is { } tags ? [.. tags] : [], package.CommittedAt);

    /// <summary>完整发布仍校验全部精确依赖和包身份, 不以索引替代发布证明.</summary>
    private static void Validate(IReadOnlyList<UdfPackage> packages)
    {
        if (packages.Count > 4096 || packages.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1024) throw Invalid();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            UdfValidation.PackageIdentity(package.Id, package.Version);
            if (!Hash(package.Hash) || package.Functions is not { Count: > 0 and <= 128 } || package.Dependencies is null || package.LicenseFiles is null
                || !identities.Add(package.Id + "|" + package.Version) || package.Functions.Any(item => item is null || item.Annotations is null)
                || package.Functions.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Functions.Count) throw Invalid();
        }
        foreach (var package in packages)
            foreach (var dependency in package.Dependencies)
                if (!identities.Contains(dependency.Id + "|" + dependency.Version)) throw new AppException("UDF_CATALOG_INCOMPLETE", "完整函数目录缺少声明的精确依赖, 已保留旧目录", 3);
        if (Latest(packages, true).Sum(item => item.Functions.Count) > 8192) throw new AppException("UDF_CATALOG_LIMIT", "完整函数目录函数数量超出限制");
    }

    /// <summary>索引也有独立身份和数量限制, 缺失函数摘要不能充当可用目录.</summary>
    private static void ValidateIndex(PackageFile[] entries)
    {
        if (entries.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1024) throw Invalid();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            UdfValidation.PackageIdentity(entry.Id, entry.Version);
            if (!Hash(entry.Digest) || !Hash(entry.Hash) || entry.Description is null || entry.Authors is null || entry.Authors.Length > 1024 ||
                entry.Tags is null || entry.Tags.Length > 64 || entry.Tags.Any(tag => tag is null || tag.Length > 64) || entry.CommittedAt is { } committed && committed.Offset != TimeSpan.Zero || entry.Functions is not { Length: > 0 and <= 128 }
                || !identities.Add(entry.Id + "|" + entry.Version) || entry.IconKey is not null && !Hash(entry.IconKey)
                || entry.Functions.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 512 || item.Description is null)
                || entry.Functions.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Functions.Length) throw Invalid();
        }
        if (LatestEntries(entries, true).Sum(item => item.Functions!.Length) > 8192) throw Invalid();
    }

    /// <summary>提供者完整查询采用最高可见版本; 浏览页优先稳定版并允许显式版本覆盖.</summary>
    internal static UdfPackage[] Latest(IReadOnlyList<UdfPackage> packages, bool includePrerelease) => [.. packages
        .Where(item => includePrerelease || !UdfValidation.IsPrerelease(item.Version)).GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(item => Version.Parse(item.Version.Split('-', '+')[0])).ThenBy(item => UdfValidation.IsPrerelease(item.Version)).ThenByDescending(item => item.Version, StringComparer.Ordinal).First())];

    /// <summary>轻量索引采用与既有包目录相同的版本排序.</summary>
    internal static PackageFile[] LatestEntries(IReadOnlyList<PackageFile> entries, bool includePrerelease) => [.. entries
        .Where(item => includePrerelease || !UdfValidation.IsPrerelease(item.Version)).GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(item => Version.Parse(item.Version.Split('-', '+')[0])).ThenBy(item => UdfValidation.IsPrerelease(item.Version)).ThenByDescending(item => item.Version, StringComparer.Ordinal).First())];

    /// <summary>拒绝缺失/超限/链接文件, 不把损坏伪装成未初始化.</summary>
    private static byte[] ReadBounded(string path, int limit)
    {
        UserDataStore.Check(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > limit) throw Invalid();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    /// <summary>重复JSON属性同样视为损坏.</summary>
    private static T Deserialize<T>(byte[] bytes) where T : class
    { using var document = UdfJson.Parse(bytes); return document.RootElement.Deserialize<T>(Json) ?? throw Invalid(); }
    /// <summary>缓存地址和发布修订采用确定性SHA256.</summary>
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    /// <summary>摘要只接受64个小写十六进制字符.</summary>
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    /// <summary>任何未知版本或损坏均保留原文件.</summary>
    private static AppException Invalid() => new("UDF_CATALOG_INVALID", "本地完整函数目录损坏或版本未知, 已保留原文件", 3);
    private sealed record Manifest(int SchemaVersion, PackageFile[] Packages);
    internal sealed record PackageFile(string Id, string Version, string Digest, string? Hash = null, string? Description = null, FunctionFile[]? Functions = null,
        string? IconKey = null, string? Authors = null, string[]? Tags = null, [property: JsonRequired] DateTimeOffset? CommittedAt = null);
    internal sealed record FunctionFile(string Name, string Description);

    /// <summary>绑定不可变代和索引, 精确包按需读取并再次验证摘要.</summary>
    internal sealed record Snapshot(string Baseline, DateTimeOffset RetrievedAt, IReadOnlyList<PackageFile> Entries, Func<PackageFile, UdfPackage> Load,
        UdfRepositoryRevision Repository, FileStream Lease) : IDisposable
    {
        /// <summary>释放固定代的共享句柄, 之后不再允许从此快照读取包体.</summary>
        public void Dispose() => Lease.Dispose();
        /// <summary>当前完整指针字节的内部见证, 与纯包内容基线分离, 不对外替代CatalogBaseline.</summary>
        internal string? Revision { get; init; }

        internal UdfCatalogStatus Status => new(true, Baseline, RetrievedAt, Entries.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Entries.Count, LatestEntries(Entries, true).Sum(item => item.Functions!.Length));
        /// <summary>提交验证逐包检查取消, 不向外部返回部分集合.</summary>
        internal UdfPackage[] ReadAll(CancellationToken token)
        {
            var packages = new List<UdfPackage>(); long total = 0;
            foreach (var entry in Entries)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Lease.SafeFileHandle.IsClosed, this);
                var package = Load(entry);
                if ((total += JsonSerializer.SerializeToUtf8Bytes(package with { RetrievedAt = DateTimeOffset.UnixEpoch, FromCache = false }, Json).Length) > MaxBytes) throw Invalid();
                packages.Add(package);
            }
            Validate(packages); return [.. packages];
        }
    }
}
