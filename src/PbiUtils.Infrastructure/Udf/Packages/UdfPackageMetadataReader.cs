// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Packages\UdfPackageMetadataReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从已验证的包内 manifest 和部件读取有界展示元数据及静态 PNG, 不联网或解码图片.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Udf.Packages;

/// <summary>展示信息与可执行函数解析分离, 不把网站侧栏信息或外部资源补成包事实.</summary>
internal static class UdfPackageMetadataReader
{
    /// <summary>从 manifest 与已验证的包内部件提取可选元数据; 文本格式或预算错误拒绝包, 不可用图片与链接返回默认值.</summary>
    internal static UdfPackageMetadata? Read(JsonElement root, Dictionary<string, byte[]> files)
    {
        var authors = Text(root, "authors", 1024);
        var tagText = Text(root, "tags", 64 * 65);
        var tags = tagText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Length > 64 || tags.Any(tag => tag.Length > 64)) throw Invalid();
        var notes = Text(root, "releaseNotes", 16 * 1024, 16 * 1024);
        var project = Link(root, "projectUrl");
        var repository = Link(root, "repositoryUrl");
        var icon = Icon(root, files);
        return authors.Length == 0 && tags.Length == 0 && notes.Length == 0 && project is null && repository is null && icon is null
            ? null : new(authors, tags, notes, project, repository, icon);
    }

    /// <summary>读取非空白的原始文本, 不截断说明事实; 缺失/null 使用空值, 错误类型或超额抛稳定包错误.</summary>
    private static string Text(JsonElement root, string name, int maxCharacters, int? maxBytes = null)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return "";
        if (property.ValueKind != JsonValueKind.String) throw Invalid();
        var value = property.GetString()!;
        if (value.Length > maxCharacters || maxBytes is { } limit && Encoding.UTF8.GetByteCount(value) > limit) throw Invalid();
        return value.Trim();
    }

    /// <summary>只保留有界且无凭据/控制字符的 HTTPS 链接; 不请求、解析远端文档或把不可信值变成可用链接.</summary>
    private static string? Link(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return null;
        var value = property.GetString()!;
        if (value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\')) return null;
        value = value.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Length != 0 && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    }

    /// <summary>仅选择包内相对根路径指向的 PNG; 不使用 URL、文件系统路径、SVG 或任意外部图片, 失败时使用 UI 默认图标.</summary>
    private static string? Icon(JsonElement root, Dictionary<string, byte[]> files)
    {
        if (!root.TryGetProperty("icon", out var property) || property.ValueKind != JsonValueKind.String) return null;
        var path = property.GetString()!;
        if (path.Length is 0 or > 512 || path.Any(char.IsControl) || path.IndexOfAny(['\\', ':', '%', '?', '#']) >= 0) return null;
        if (path.StartsWith('/')) path = path[1..];
        if (path.Split('/').Any(part => part is "" or "." or "..") || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            !files.TryGetValue(path, out var bytes) || !Png(bytes)) return null;
        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }

    /// <summary>验证静态 PNG 的实际签名、IHDR/尺寸、chunk 边界与 CRC; 最多 128 KiB/1024² 像素, 不解压或执行图片.</summary>
    private static bool Png(byte[] bytes)
    {
        if (bytes.Length is < 45 or > 128 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 1024 || height is 0 or > 1024 || bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1 ||
            !ValidColor(bytes[24], bytes[25])) return false;
        var offset = 8;
        var hasData = false;
        while (offset <= bytes.Length - 12)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (size > bytes.Length - offset - 12) return false;
            var count = (int)size;
            var type = bytes.AsSpan(offset + 4, 4);
            var chunk = bytes.AsSpan(offset + 4, count + 4);
            if (Crc(chunk) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + count + 8, 4)) ||
                type.SequenceEqual("IHDR"u8) && offset != 8 || type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)) return false;
            if (type.SequenceEqual("IDAT"u8)) hasData = true;
            if (type.SequenceEqual("IEND"u8)) return size == 0 && hasData && offset + 12 == bytes.Length;
            offset += count + 12;
        }
        return false;
    }

    /// <summary>依据 PNG 规范校验颜色类型与位深组合, 拒绝只有尺寸正确的伪造头.</summary>
    private static bool ValidColor(byte depth, byte color) => color switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        _ => false
    };

    /// <summary>计算 PNG chunk 的标准 CRC-32, 输入始于类型并包含完整数据, 不解释图片数据.</summary>
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }

    /// <summary>拒绝不完整或超额展示事实时保留统一包错误分类, 不泄露远端元数据正文.</summary>
    private static AppException Invalid() => new("UDF_PACKAGE_INVALID", "函数包格式、身份或安全限制不符合要求");
}
