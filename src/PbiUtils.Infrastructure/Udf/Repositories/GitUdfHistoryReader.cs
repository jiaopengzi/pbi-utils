// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitUdfHistoryReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从认证 Git 提交与目录树求精确路径最后修改时间, 保留拓扑、合并与浅历史的不确定性.

using System.Globalization;
using System.Text;

using PbiUtils.Infrastructure.Udf.Repositories.GitHub;
using PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

namespace PbiUtils.Infrastructure.Udf.Repositories;

/// <summary>按当前精确目录路径跟随首个 TREESAME 父提交, 不做重命名追踪或时间戳最大值推断.</summary>
internal sealed class GitUdfHistoryReader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IReadOnlyDictionary<string, GitSmartUdfObject> objects;
    private readonly IReadOnlySet<string> shallow;
    private readonly CancellationToken token;
    private readonly Dictionary<string, Commit> commits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TreeEntry>> trees = new(StringComparer.Ordinal);

    /// <summary>只读取已验证 PACK 的对象, 不从解析器发起任何网络或文件请求.</summary>
    private GitUdfHistoryReader(IReadOnlyDictionary<string, GitSmartUdfObject> objects, IReadOnlySet<string> shallow, CancellationToken token)
    { this.objects = objects; this.shallow = shallow; this.token = token; }

    /// <summary>校验固定快照根树与可达父链后返回各精确目录 UTC 提交时间; 可证浅边界返回 null, 损坏/缺失非浅对象失败.</summary>
    internal static IReadOnlyDictionary<string, DateTimeOffset?> Read(IReadOnlyDictionary<string, GitSmartUdfObject> objects, string head, string expectedTree,
        IReadOnlyList<string> directories, IReadOnlyCollection<string> shallow, CancellationToken token)
    {
        var reader = new GitUdfHistoryReader(objects, shallow.ToHashSet(StringComparer.Ordinal), token);
        reader.ValidateGraph(head);
        if (reader.commits[head].Tree != expectedTree) throw GitUdfCatalogSource.Invalid();
        var result = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (var path in directories)
        {
            token.ThrowIfCancellationRequested(); UdfRepositoryContent.ValidatePath(path);
            if (path.Split('/').Length > 64 || reader.Directory(expectedTree, path) is null || !result.TryAdd(path, reader.LastChange(head, path))) throw GitUdfCatalogSource.Invalid();
        }
        return result;
    }

    /// <summary>父对象只有在对应浅边界才允许缺失, 无效提交不能因某路径提前找到日期而被静默略过.</summary>
    private void ValidateGraph(string head)
    {
        var pending = new Queue<string>(); pending.Enqueue(head);
        var queued = new HashSet<string>(StringComparer.Ordinal) { head };
        while (pending.TryDequeue(out var hash))
        {
            token.ThrowIfCancellationRequested();
            if (commits.ContainsKey(hash)) continue;
            if (commits.Count >= GitUdfFetch.MaxEntries * 2) throw GitUdfCatalogSource.Invalid();
            var commit = ParseCommit(hash); commits.Add(hash, commit);
            _ = Tree(commit.Tree);
            foreach (var parent in commit.Parents)
            {
                if (objects.ContainsKey(parent)) { if (queued.Add(parent)) pending.Enqueue(parent); }
                else if (!shallow.Contains(hash)) throw GitUdfCatalogSource.Invalid();
            }
        }
        if (shallow.Any(hash => !commits.ContainsKey(hash))) throw GitUdfCatalogSource.Invalid();
    }

    /// <summary>合并只要有同目录树父提交即沿该分支继续; 仅所有父树均可证不同才归属于合并提交.</summary>
    private DateTimeOffset? LastChange(string head, string path)
    {
        var hash = head;
        for (var depth = 0; depth < 4096; depth++)
        {
            token.ThrowIfCancellationRequested();
            var commit = commits[hash]; var current = Directory(commit.Tree, path);
            string? same = null;
            foreach (var parent in commit.Parents)
                if (commits.TryGetValue(parent, out var ancestor) && Directory(ancestor.Tree, path) == current) { same = parent; break; }
            if (same is not null) { hash = same; continue; }
            if (shallow.Contains(hash) || commit.Parents.Any(parent => !commits.ContainsKey(parent))) return null;
            return commit.CommittedAt;
        }
        // 本地深度预算之外即使服务端给出额外对象也不推测; 快照本身仍可正常缓存和浏览.
        return null;
    }

    /// <summary>只解析提交头, 日期采用 committer Unix 秒, 不读取作者身份到任何产品数据或日志.</summary>
    private Commit ParseCommit(string hash)
    {
        var value = Object(hash, 1);
        var end = value.Content.AsSpan().IndexOf("\n\n"u8);
        if (end < 0) throw GitUdfCatalogSource.Invalid();
        // Git 消息与身份可使用声明的其他编码; 这里只检查固定 ASCII 字段, 不解码消息或保留身份.
        var text = Encoding.ASCII.GetString(value.Content, 0, end);
        string? tree = null; DateTimeOffset? committed = null; var parents = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            token.ThrowIfCancellationRequested();
            if (line.StartsWith("tree ", StringComparison.Ordinal))
            { if (tree is not null) throw GitUdfCatalogSource.Invalid(); tree = Hash(line[5..]); }
            else if (line.StartsWith("parent ", StringComparison.Ordinal))
            { if (parents.Count >= 4096) throw GitUdfCatalogSource.Invalid(); var parent = Hash(line[7..]); if (parents.Contains(parent, StringComparer.Ordinal)) throw GitUdfCatalogSource.Invalid(); parents.Add(parent); }
            else if (line.StartsWith("committer ", StringComparison.Ordinal))
            { if (committed is not null) throw GitUdfCatalogSource.Invalid(); committed = Timestamp(line); }
        }
        return tree is not null && committed is not null ? new(tree, parents, committed.Value) : throw GitUdfCatalogSource.Invalid();
    }

    /// <summary>Git 时间戳本身是 UTC 秒, 时区只用于合法性验证, 不再次偏移秒值或接受无时区字符串.</summary>
    private static DateTimeOffset Timestamp(string line)
    {
        var zoneAt = line.LastIndexOf(' '); var secondsAt = zoneAt > 0 ? line.LastIndexOf(' ', zoneAt - 1) : -1;
        if (secondsAt < 0 || zoneAt <= secondsAt + 1) throw GitUdfCatalogSource.Invalid();
        var zone = line[(zoneAt + 1)..];
        if (zone.Length != 5 || zone[0] is not ('+' or '-') || !zone.Skip(1).All(char.IsAsciiDigit)
            || int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture) > 23 || int.Parse(zone.AsSpan(3, 2), CultureInfo.InvariantCulture) > 59
            || !long.TryParse(line.AsSpan(secondsAt + 1, zoneAt - secondsAt - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)) throw GitUdfCatalogSource.Invalid();
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { throw GitUdfCatalogSource.Invalid(); }
    }

    /// <summary>目录查找按 Git 的 Ordinal 路径, 包版本目录内任意文件变化均由其树对象 ID 捕获.</summary>
    private string? Directory(string root, string path)
    {
        string? hash = root;
        foreach (var part in path.Split('/'))
        {
            token.ThrowIfCancellationRequested();
            if (!Tree(hash).TryGetValue(part, out var entry) || entry.Mode is not ("40000" or "040000")) { hash = null; break; }
            hash = entry.Hash;
        }
        if (hash is not null) _ = Tree(hash);
        // 不缓存 root×path 笛卡尔积; 历史树条目缓存已足够复用, 内存由 PACK 对象预算限定.
        return hash;
    }

    /// <summary>历史只需要树, blob:none 不要求下载正文; 目录模式与名称仍严格验证, 缺失树一律拒绝.</summary>
    private Dictionary<string, TreeEntry> Tree(string hash)
    {
        if (trees.TryGetValue(hash, out var cached)) return cached;
        var bytes = Object(hash, 2).Content; var offset = 0; var entries = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var space = bytes.AsSpan(offset).IndexOf((byte)' ');
            if (space is < 1 or > 6) throw GitUdfCatalogSource.Invalid();
            var mode = Encoding.ASCII.GetString(bytes, offset, space); offset += space + 1;
            var nul = bytes.AsSpan(offset).IndexOf((byte)0);
            if (nul is < 1 or > 1024 || bytes.Length - offset - nul - 1 < 20) throw GitUdfCatalogSource.Invalid();
            string name;
            try { name = Utf8.GetString(bytes, offset, nul); } catch (DecoderFallbackException) { throw GitUdfCatalogSource.Invalid(); }
            if (name.Contains('/') || mode is not ("40000" or "040000" or "100644" or "100755" or "120000" or "160000")) throw GitUdfCatalogSource.Invalid();
            UdfRepositoryContent.ValidatePath(name); offset += nul + 1;
            var target = Hash(Convert.ToHexStringLower(bytes.AsSpan(offset, 20))); offset += 20;
            if (entries.Count >= GitUdfFetch.MaxEntries || !aliases.Add(name) || !entries.TryAdd(name, new(mode, target))) throw GitUdfCatalogSource.Invalid();
        }
        trees.Add(hash, entries); return entries;
    }

    /// <summary>仅已认证 PACK 中正确类型的对象可作为提交或目录依据.</summary>
    private GitSmartUdfObject Object(string hash, int type) => objects.TryGetValue(hash, out var value) && value.Type == type ? value : throw GitUdfCatalogSource.Invalid();
    /// <summary>严格 SHA1 对象 ID, 不接受零值或可执行的引用表达式.</summary>
    private static string Hash(string value) => GitSmartUdfProtocol.IsHash(value) ? value.ToLowerInvariant() : throw GitUdfCatalogSource.Invalid();

    /// <summary>仅保留拓扑、树与 UTC 秒值, 不保留作者、消息或签名.</summary>
    private sealed record Commit(string Tree, IReadOnlyList<string> Parents, DateTimeOffset CommittedAt);
    /// <summary>树条目用于目录比较, 历史 blob 正文无需下载或执行.</summary>
    private sealed record TreeEntry(string Mode, string Hash);
}
