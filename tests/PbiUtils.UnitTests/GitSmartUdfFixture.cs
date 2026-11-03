// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitSmartUdfFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在内存构造标准 Git smart HTTP 广告、真实对象和 PACK, 不启动 Git 或网络.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PbiUtils.UnitTests;

/// <summary>协议夹具独立生成真实对象指纹, 正式传输仍负责解析和安全验证.</summary>
internal sealed class GitSmartUdfFixture
{
    private readonly List<(int Type, byte[] Content, string Hash)> objects = [];
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    internal string Commit { get; }
    internal List<Uri> Requests { get; } = [];
    internal bool CorruptPack { get; init; }
    internal bool OmitBlob { get; init; }
    internal bool UseDelta { get; init; }
    internal bool UseReferenceDelta { get; init; }
    internal int DeltaDepth { get; init; } = 1;
    internal bool IgnoreWantedObject { get; init; }
    internal bool AnnotatedTag { get; init; }
    internal string FileMode { get; init; } = "100644";

    /// <summary>标准 packages 输入展开为真实 Git 树, 模式参数用于链接与可执行文件边界.</summary>
    internal GitSmartUdfFixture(string mode = "100644", string? extraPath = null)
    {
        FileMode = mode;
        using var zip = new ZipArchive(new MemoryStream(PbiUtils.Tests.Support.UdfPackageFixture.Create("Example.One", "1.0.0", [("Example.Helper", "() => 1", "helper")], "# Example")), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output);
            files.Add("packages/e/example.one/1.0.0/" + entry.FullName, output.ToArray());
        }
        if (extraPath is not null) files.Add(extraPath, "extra"u8.ToArray());
        var tree = Tree("");
        Commit = Add(1, Encoding.UTF8.GetBytes($"tree {tree}\nauthor Fixture <fixture@example.test> 0 +0000\ncommitter Fixture <fixture@example.test> 0 +0000\n\nFixture\n"));
    }

    /// <summary>仅响应固定仓库的两种协议请求, 断言不会继承凭据或使用平台 API.</summary>
    internal HttpResponseMessage Respond(HttpRequestMessage request)
    {
        Requests.Add(request.RequestUri!);
        if (request.Headers.Authorization is not null || request.Headers.Contains("Cookie")) throw new InvalidOperationException("Unexpected credentials");
        if (request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery.EndsWith("/info/refs?service=git-upload-pack", StringComparison.Ordinal))
        {
            var tag = AnnotatedTag ? Tag() : Commit;
            return Response("application/x-git-upload-pack-advertisement", [.. Packet("# service=git-upload-pack\n"), .. "0000"u8,
                .. Packet(Commit + " HEAD\0shallow ofs-delta symref=HEAD:refs/heads/main\n"),
                .. Packet(Commit + " refs/heads/main\n"), .. Packet(tag + " refs/tags/v1\n"),
                .. (AnnotatedTag ? Packet(Commit + " refs/tags/v1^{}\n") : []), .. "0000"u8]);
        }
        if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/git-upload-pack", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected protocol route");
        var wanted = Encoding.ASCII.GetString(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        if (!IgnoreWantedObject && !wanted.Contains("want " + Commit, StringComparison.Ordinal) && !(AnnotatedTag && wanted.Contains("want " + Tag(), StringComparison.Ordinal))) throw new InvalidOperationException("Unpinned request");
        var history = wanted.Contains("deepen 4096", StringComparison.Ordinal);
        if ((!wanted.Contains("deepen 1", StringComparison.Ordinal) && !history) || wanted.Contains("thin-pack", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fetch negotiation");
        var pack = Pack();
        if (CorruptPack) pack[^1] ^= 1;
        return Response("application/x-git-upload-pack-result", [.. (history ? [] : Packet("shallow " + Commit + "\n")),
            .. (history ? [] : "0000"u8), .. Packet("NAK\n"), .. pack]);
    }

    /// <summary>递归构造内存树, 子路径仅按 Git 格式写入而不调用被测解析器.</summary>
    private string Tree(string prefix)
    {
        using var content = new MemoryStream();
        foreach (var child in files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).Select(path => path[prefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var path = prefix + child;
            var directory = !files.ContainsKey(path);
            var hash = directory ? Tree(path + "/") : Add(3, files[path]);
            content.Write(Encoding.UTF8.GetBytes((directory ? "40000" : FileMode) + " " + child + "\0"));
            content.Write(Convert.FromHexString(hash));
        }
        return Add(2, content.ToArray());
    }

    /// <summary>追加带完整 Git 类型/长度头的对象, 返回用于父对象引用的真实 SHA-1.</summary>
    private string Add(int type, byte[] content)
    {
        var name = type switch { 1 => "commit", 2 => "tree", 3 => "blob", _ => "tag" };
        var hash = Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes(name + " " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0"), .. content]));
        objects.Add((type, content, hash)); return hash;
    }

    /// <summary>注解标签的对象指向固定 commit, 重复响应不重复增加对象.</summary>
    private string Tag() => objects.FirstOrDefault(item => item.Type == 4).Hash ?? Add(4, Encoding.ASCII.GetBytes($"object {Commit}\ntype commit\ntag v1\ntagger Fixture <fixture@example.test> 0 +0000\n\nTag\n"));

    /// <summary>生成 PACK v2, 可使用 ofs/ref delta 及故意缺失的 blob 模拟真实服务器边界.</summary>
    private byte[] Pack()
    {
        using var output = new MemoryStream(); output.Write("PACK"u8);
        var entries = objects.Where(item => !OmitBlob || item.Type != 3).ToArray();
        Span<byte> integer = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(integer, 2); output.Write(integer);
        BinaryPrimitives.WriteInt32BigEndian(integer, entries.Length + (UseDelta ? DeltaDepth : 0)); output.Write(integer);
        long deltaBase = 0;
        foreach (var entry in entries)
        {
            if (entry == entries[0]) deltaBase = output.Position;
            Header(output, entry.Type, entry.Content.Length); Compress(output, entry.Content);
        }
        var baseContent = entries[0].Content;
        var baseHash = entries[0].Hash;
        for (var depth = 0; UseDelta && depth < DeltaDepth; depth++)
        {
            using var delta = new MemoryStream(); Variable(delta, baseContent.Length); Variable(delta, baseContent.Length + 1);
            for (var index = 0; index < baseContent.Length; index += 100)
            { var length = Math.Min(100, baseContent.Length - index); delta.WriteByte((byte)length); delta.Write(baseContent, index, length); }
            delta.WriteByte(1); delta.WriteByte((byte)'!');
            var offset = output.Position;
            Header(output, UseReferenceDelta ? 7 : 6, checked((int)delta.Length));
            if (UseReferenceDelta) output.Write(Convert.FromHexString(baseHash));
            else
            {
                var distance = offset - deltaBase; var encoded = new List<byte> { (byte)(distance & 127) };
                while ((distance >>= 7) != 0) { distance--; encoded.Insert(0, (byte)(128 | (distance & 127))); }
                output.Write(encoded.ToArray());
            }
            Compress(output, delta.ToArray());
            deltaBase = offset;
            baseContent = [.. baseContent, (byte)'!'];
            var typeName = entries[0].Type switch { 1 => "commit", 2 => "tree", 3 => "blob", _ => "tag" };
            baseHash = Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes(typeName + " " + baseContent.Length.ToString(CultureInfo.InvariantCulture) + "\0"), .. baseContent]));
        }
        output.Write(SHA1.HashData(output.ToArray())); return output.ToArray();
    }

    /// <summary>写 Git 变长对象头, 无平台端序依赖.</summary>
    private static void Header(Stream output, int type, int size)
    {
        var first = (byte)((type << 4) | (size & 15)); size >>= 4;
        output.WriteByte((byte)(first | (size > 0 ? 128 : 0)));
        while (size > 0) { var next = size & 127; size >>= 7; output.WriteByte((byte)(next | (size > 0 ? 128 : 0))); }
    }

    /// <summary>delta 头使用 little-endian 7-bit 变长整数.</summary>
    private static void Variable(Stream output, int value)
    { do { var next = value & 127; value >>= 7; output.WriteByte((byte)(next | (value > 0 ? 128 : 0))); } while (value > 0); }

    /// <summary>每个对象是独立 zlib 流, 与 ZIP/裸 deflate 区别由正式实现处理.</summary>
    private static void Compress(Stream output, byte[] content)
    { using var compressed = new ZLibStream(output, CompressionLevel.Optimal, true); compressed.Write(content); }

    /// <summary>协议长度包括四字节 ASCII 十六进制头.</summary>
    internal static byte[] Packet(string text)
    { var content = Encoding.UTF8.GetBytes(text); return [.. Encoding.ASCII.GetBytes((content.Length + 4).ToString("x4", CultureInfo.InvariantCulture)), .. content]; }

    /// <summary>响应声明精确 Git 协议媒体类型, 不模拟认证或 HTML 页面.</summary>
    private static HttpResponseMessage Response(string type, byte[] content) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(content) { Headers = { ContentType = new(type) } } };
}
