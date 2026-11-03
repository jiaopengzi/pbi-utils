// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitHubUdfFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 提供真实 Git blob 指纹、完整树及 ZIP 的内存 GitHub 测试传输.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PbiUtils.UnitTests;

/// <summary>仅替代 HTTP, 包布局、解包、来源隔离和发布仍由正式实现处理.</summary>
internal sealed class GitHubUdfFixture
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly GitUdfHistoryFixture history = new();

    /// <summary>将标准包展开为同一提交的 packages 布局, 保留仓库根许可和精确原始字节.</summary>
    internal GitHubUdfFixture(params byte[][] packages)
    {
        files.Add("LICENSE", "fixture repository MIT"u8.ToArray());
        foreach (var package in packages)
        {
            using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
            using var manifest = archive.GetEntry("manifest.daxlib")!.Open();
            using var document = JsonDocument.Parse(manifest);
            var id = document.RootElement.GetProperty("id").GetString()!.ToLowerInvariant();
            var version = document.RootElement.GetProperty("version").GetString()!;
            foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
            {
                using var stream = entry.Open(); using var content = new MemoryStream(); stream.CopyTo(content);
                files.Add($"packages/{id[0]}/{id}/{version}/{entry.FullName}", content.ToArray());
            }
        }
        Tree = history.Tree(files);
        Commit = history.Commit(Tree, 1700000000);
    }

    internal string Commit { get; }
    internal string Tree { get; }
    internal bool Truncated { get; init; }
    internal bool RepeatTreeEntry { get; init; }
    internal bool OmitArchiveFile { get; init; }

    /// <summary>返回约定的元数据、固定提交、完整树或同提交 ZIP; 未知地址立即暴露测试配置错误.</summary>
    internal HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host == "github.com")
        {
            if (!uri.AbsolutePath.Contains(".git/", StringComparison.Ordinal)) throw new InvalidOperationException("Unnormalized clone path");
            if (request.Method == HttpMethod.Get && uri.PathAndQuery.EndsWith("/info/refs?service=git-upload-pack", StringComparison.Ordinal))
                return GitResponse("advertisement", [.. GitSmartUdfFixture.Packet("# service=git-upload-pack\n"), .. "0000"u8,
                    .. GitSmartUdfFixture.Packet(Commit + " HEAD\0shallow ofs-delta filter symref=HEAD:refs/heads/main\n"), .. "0000"u8]);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (request.Method != HttpMethod.Post || !uri.AbsolutePath.EndsWith("/git-upload-pack", StringComparison.Ordinal)
                || !body.Contains("want " + Commit, StringComparison.Ordinal) || !body.Contains("deepen 4096", StringComparison.Ordinal)
                || !body.Contains("filter blob:none", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected history request");
            return GitResponse("result", [.. GitSmartUdfFixture.Packet("NAK\n"), .. history.Pack()]);
        }
        if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Unexpected fixture method");
        if (uri.Host == "codeload.github.com")
        {
            if (parts.Length != 4 || parts[2] != "zip" || parts[3] != Commit || uri.Query.Length != 0) throw new InvalidOperationException("Unexpected fixture archive route");
            return Archive(parts[1]);
        }
        if (uri.Host != "api.github.com" || parts.Length < 3 || parts[0] != "repos") throw new InvalidOperationException("Unexpected fixture API route");
        if (parts.Length == 5 && parts[3] == "commits" && parts[4].Length > 0 && uri.Query.Length == 0)
            return Json(new { sha = Commit, commit = new { tree = new { sha = Tree } } });
        if (parts.Length == 6 && parts[3] == "git" && parts[4] == "trees" && parts[5] == Tree && uri.Query == "?recursive=1")
        {
            var tree = files.Select(file => new { path = file.Key, mode = "100644", type = "blob", sha = BlobHash(file.Value), size = file.Value.Length }).ToList();
            if (RepeatTreeEntry) tree.Add(tree[0]);
            return Json(new { sha = Tree, truncated = Truncated, tree });
        }
        if (parts.Length == 3 && uri.Query.Length == 0) return Json(new { default_branch = "main" });
        throw new InvalidOperationException("Unexpected fixture API endpoint or query");
    }

    /// <summary>所有压缩成员使用单一公共根及确定时间, 可模拟树存在但压缩包遗漏的文件.</summary>
    private HttpResponseMessage Archive(string repository)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files.Take(files.Count - (OmitArchiveFile ? 1 : 0)))
            {
                var entry = zip.CreateEntry(repository + "-" + Commit + "/" + file.Key);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(file.Value);
            }
        }
        var content = new ByteArrayContent(output.ToArray()); content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return new(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Git 对象指纹包含 blob 类型和原始长度, 不能使用普通文件 SHA1 替代.</summary>
    private static string BlobHash(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        return Convert.ToHexStringLower(SHA1.HashData([.. header, .. bytes]));
    }

    /// <summary>GitHub JSON 严格携带媒体类型, 不把 HTML 或任意正文当作有效协议.</summary>
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    /// <summary>历史响应仍为标准 Git 协议, 不将自定义 JSON 日期注入目录.</summary>
    private static HttpResponseMessage GitResponse(string kind, byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/x-git-upload-pack-" + kind) } } };

    /// <summary>复用独立标准包夹具, 保持原始 TMDL、manifest、ZIP 时间及精确字节约定.</summary>
    internal static byte[] Package(string id, string version, (string Name, string Expression, string Description)[] functions,
        string readme = "", object[]? dependencies = null)
        => PbiUtils.Tests.Support.UdfPackageFixture.Create(id, version, functions, readme, dependencies);

    private static readonly (string Name, string Expression, string Description)[] CatalogFunctions =
        [("Example.Helper", "() => 1", "helper description"), ("Example.Other", "() => 2", "other description")];
    private static readonly string[] OriginalVersions = ["1.0.0", "2.0.0"];
    private static readonly string[] NewerVersions = ["1.0.0", "2.0.0", "3.0.0"];

    /// <summary>标准目录含全部历史版本, 可显式增加版本以验证不可变快照和冻结失效.</summary>
    internal static GitHubUdfFixture Catalog(bool newer = false) => new([.. (newer ? NewerVersions : OriginalVersions)
        .Select(version => Package("Example.One", version, CatalogFunctions, version == "1.0.0" ? "Historical" : "Current"))]);

    /// <summary>同名包在来源之间使用不同函数前缀, 依赖仍通过真实 manifest 解析.</summary>
    internal static GitHubUdfFixture Dependencies(string prefix) => new(
        Package("Team.Library", "1.0.0", [(prefix + ".One", "() => " + prefix + ".Helper()", "")], dependencies: [new { id = "Shared", version = "1.0.0" }]),
        Package("Shared", "1.0.0", [(prefix + ".Helper", "() => 1", "")]));
}
