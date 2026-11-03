// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\SmartHttp\GitSmartUdfProtocol.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 解析有界 Git v0/v1 pkt-line 广告并协商固定对象的浅 PACK 获取.

using System.Globalization;
using System.Text;

using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

/// <summary>只协商 shallow 与可选 ofs-delta; 不请求 thin-pack、side-band 或服务器外部资源.</summary>
internal static class GitSmartUdfProtocol
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>严格读取服务前导、引用和能力, 默认使用 HEAD; 短名歧义和未知对象格式失败.</summary>
    internal static GitSmartUdfReference Resolve(byte[] advertisement, string? reference)
    {
        var offset = 0;
        if (Text(Packet(advertisement, ref offset)) != "# service=git-upload-pack" || Packet(advertisement, ref offset).Length != 0) throw GitUdfCatalogSource.Invalid();
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        string[]? capabilities = null;
        var finished = false;
        var versionSeen = false;
        while (offset < advertisement.Length)
        {
            var payload = Packet(advertisement, ref offset);
            if (payload.Length == 0) { finished = true; break; }
            var line = Text(payload);
            if (line == "version 1" && refs.Count == 0 && capabilities is null && !versionSeen) { versionSeen = true; continue; }
            var nul = line.IndexOf('\0');
            if (capabilities is null)
            {
                if (nul < 0) throw GitUdfCatalogSource.Invalid();
                capabilities = line[(nul + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                line = line[..nul];
            }
            else if (nul >= 0) throw GitUdfCatalogSource.Invalid();
            if (line.Length < 42 || line[40] != ' ' || !IsHash(line[..40]) || refs.Count >= GitUdfFetch.MaxEntries) throw GitUdfCatalogSource.Invalid();
            var name = line[41..];
            if (name != "HEAD") GitUdfCatalogSource.ValidateReference(name.EndsWith("^{}", StringComparison.Ordinal) ? name[..^3] : name);
            if (!refs.TryAdd(name, line[..40].ToLowerInvariant())) throw GitUdfCatalogSource.Invalid();
        }
        if (!finished || offset != advertisement.Length || refs.Count == 0 || capabilities is null || !capabilities.Contains("shallow", StringComparer.Ordinal)
            || capabilities.Any(value => value.StartsWith("object-format=", StringComparison.Ordinal) && value != "object-format=sha1")) throw GitUdfCatalogSource.Invalid();
        string requested;
        if (reference is null)
        {
            var heads = capabilities.Where(value => value.StartsWith("symref=HEAD:", StringComparison.Ordinal)).ToArray();
            if (heads.Length > 1) throw GitUdfCatalogSource.Invalid();
            requested = heads.Length == 1 ? heads[0][12..] : "HEAD";
            if (!refs.TryGetValue(requested, out var head) || refs.TryGetValue("HEAD", out var advertisedHead) && advertisedHead != head) throw GitUdfCatalogSource.Invalid();
        }
        else requested = reference;
        string hash;
        if (IsHash(requested)) hash = requested.ToLowerInvariant();
        else
        {
            var candidates = requested.StartsWith("refs/", StringComparison.Ordinal) || requested == "HEAD"
                ? new[] { requested } : ["refs/heads/" + requested, "refs/tags/" + requested];
            var matches = candidates.Where(refs.ContainsKey).ToArray();
            if (matches.Length != 1) throw GitUdfCatalogSource.Invalid();
            hash = refs[matches[0]];
        }
        return new(hash, capabilities.Contains("ofs-delta", StringComparer.Ordinal), capabilities.Contains("filter", StringComparer.Ordinal));
    }

    /// <summary>引用先解析为固定对象 ID; 不发送分支名, 不协商缺少本地基础对象的 thin pack.</summary>
    internal static byte[] Request(GitSmartUdfReference reference, bool history = false) => [.. Encode("want " + reference.Hash + (reference.OfsDelta ? " ofs-delta" : "")
        + (history && reference.Filter ? " filter" : "") + "\n"), .. Encode(history ? "deepen 4096\n" : "deepen 1\n"),
        .. (history && reference.Filter ? Encode("filter blob:none\n") : []), .. "0000"u8, .. Encode("done\n")];

    /// <summary>浅边界与 NAK 必须按顺序出现; 未请求的 side-band、ACK 和远端 ERR 均拒绝.</summary>
    internal static byte[] ReadPack(byte[] response) => ReadHistoryPack(response).Pack;

    /// <summary>保留服务器声明的浅边界, 防止把截断历史中的最老提交误当成真正根提交.</summary>
    internal static (byte[] Pack, IReadOnlySet<string> Shallow) ReadHistoryPack(byte[] response)
    {
        var offset = 0;
        var shallowSection = false;
        var shallowFinished = false;
        var shallow = new HashSet<string>(StringComparer.Ordinal);
        while (offset < response.Length)
        {
            var packet = Packet(response, ref offset);
            if (packet.Length == 0)
            {
                if (shallowFinished) throw GitUdfCatalogSource.Invalid();
                shallowFinished = true; continue;
            }
            var line = Text(packet);
            if (line.StartsWith("shallow ", StringComparison.Ordinal) && !shallowFinished && IsHash(line[8..]))
            { if (!shallow.Add(line[8..].ToLowerInvariant())) throw GitUdfCatalogSource.Invalid(); shallowSection = true; continue; }
            if (line != "NAK" || shallowSection && !shallowFinished || offset >= response.Length || !response.AsSpan(offset).StartsWith("PACK"u8)) throw GitUdfCatalogSource.Invalid();
            return (response[offset..], shallow);
        }
        throw GitUdfCatalogSource.Invalid();
    }

    /// <summary>pkt-line 长度包括四字节头, 0000 表示 flush; 截断、特殊 v2 分隔符和超长包失败.</summary>
    internal static ReadOnlySpan<byte> Packet(byte[] bytes, ref int offset)
    {
        if (offset < 0 || bytes.Length - offset < 4) throw GitUdfCatalogSource.Invalid();
        var header = Encoding.ASCII.GetString(bytes, offset, 4);
        if (!int.TryParse(header, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length)) throw GitUdfCatalogSource.Invalid();
        offset += 4;
        if (length == 0) return [];
        if (length is < 5 or > 65520 || length - 4 > bytes.Length - offset) throw GitUdfCatalogSource.Invalid();
        var result = bytes.AsSpan(offset, length - 4); offset += length - 4; return result;
    }

    /// <summary>Git 对象格式限 SHA-1 且拒绝零对象 ID, 不截断服务器输入.</summary>
    internal static bool IsHash(string value) => value.Length == 40 && value.All(Uri.IsHexDigit) && value.Any(character => character != '0');

    /// <summary>仅移除规范的一个末尾 LF, 不修剪会改变 ref 意义的空白.</summary>
    private static string Text(ReadOnlySpan<byte> bytes)
    {
        try { var value = Utf8.GetString(bytes); return value.EndsWith('\n') ? value[..^1] : value; }
        catch (DecoderFallbackException) { throw GitUdfCatalogSource.Invalid(); }
    }

    /// <summary>请求只包含已验证的 ASCII 对象 ID 和固定协议关键字.</summary>
    private static byte[] Encode(string value) => [.. Encoding.ASCII.GetBytes((value.Length + 4).ToString("x4", CultureInfo.InvariantCulture)), .. Encoding.ASCII.GetBytes(value)];
}

/// <summary>广告解析得到的唯一固定对象与服务端允许的受控压缩能力.</summary>
internal sealed record GitSmartUdfReference(string Hash, bool OfsDelta, bool Filter = false);
