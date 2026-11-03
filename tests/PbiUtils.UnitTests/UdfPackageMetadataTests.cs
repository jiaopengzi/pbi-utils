// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfPackageMetadataTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证包元数据与包内 PNG 的有界读取, 不访问远端资源或执行图片内容.

using PbiUtils.Infrastructure.Udf.Repositories;
using PbiUtils.Infrastructure.Udf.Packages;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>以真实 ZIP 入口验证元数据, 保留原函数、摘要与许可契约.</summary>
public sealed class UdfPackageMetadataTests
{
    /// <summary>固定期望分类由手工 fixture 给出, 不在重复执行时分配参数数组.</summary>
    private static readonly string[] ExpectedTags = ["DAX", "SVG", "UDF"];
    /// <summary>Git fixture 的单分类期望, 不复用产品分类处理逻辑.</summary>
    private static readonly string[] ExpectedGitTags = ["SVG"];

    /// <summary>完整元数据来自 manifest, 作者与说明保留文本, 分类去空白并按大小写去重.</summary>
    [Fact]
    public void ReadsBoundedManifestMetadataWithoutInventingLicenseOrPublication()
    {
        var bytes = Package(new()
        {
            ["authors"] = "  Alice, Bob  ",
            ["tags"] = " DAX,SVG, svg ,,UDF ",
            ["releaseNotes"] = "改进 <script>只是文本</script>\n第二行",
            ["projectUrl"] = "https://example.org/docs#start",
            ["repositoryUrl"] = "https://github.com/example/functions"
        }, ("README.md", Encoding.UTF8.GetBytes("# 原始说明")));
        var package = DaxLibPackageReader.Read(bytes, "Example.Test", "1.0.0");
        var metadata = Assert.IsType<PbiUtils.Application.Udf.UdfPackageMetadata>(package.Metadata);
        Assert.Equal("Alice, Bob", metadata.Authors);
        Assert.Equal(ExpectedTags, metadata.Tags);
        Assert.Equal("改进 <script>只是文本</script>\n第二行", metadata.ReleaseNotes);
        Assert.Equal("https://example.org/docs#start", metadata.ProjectUrl);
        Assert.Equal("https://github.com/example/functions", metadata.RepositoryUrl);
        Assert.Null(metadata.IconDataUrl);
        Assert.Empty(package.LicenseFiles);
        Assert.Equal("# 原始说明", package.Readme);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), package.Hash);
        Assert.Equal("Example.Test.One", Assert.Single(package.Functions).Name);
    }

    /// <summary>历史包没有展示元数据时保持缺省, 不能补造作者、许可和图标.</summary>
    [Fact]
    public void MissingManifestMetadataKeepsOptionalDefault()
    {
        var package = DaxLibPackageReader.Read(Package([]), "Example.Test", "1.0.0");
        Assert.Null(package.Metadata);
        Assert.Empty(package.LicenseFiles);
    }

    /// <summary>文档遵循 manifest 的包内声明, 保留全部 Markdown, 不被根目录另一 README 替换.</summary>
    [Fact]
    public void ReadsManifestDeclaredMarkdownDocument()
    {
        var package = DaxLibPackageReader.Read(Package(new() { ["readme"] = "/docs/guide.md" },
            ("docs/guide.md", "# Complete guide\n\n| A | B |\n|---|---|\n| 1 | 2 |"u8.ToArray()),
            ("README.md", "Other document"u8.ToArray())), "Example.Test", "1.0.0");
        Assert.StartsWith("# Complete guide", package.Readme, StringComparison.Ordinal);
        Assert.Contains("| 1 | 2 |", package.Readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Other document", package.Readme, StringComparison.Ordinal);
    }

    /// <summary>不安全文档路径、错误类型与缺失声明部件整包拒绝, 不发起网络或静默换文档.</summary>
    [Theory]
    [InlineData("https://example.org/readme.md")]
    [InlineData("/../readme.md")]
    [InlineData("/docs//guide.md")]
    [InlineData("/docs\\guide.md")]
    [InlineData("/docs/%2e%2e/guide.md")]
    [InlineData("/missing.md")]
    [InlineData("/guide.txt")]
    public void RejectsUnsafeOrMissingDeclaredReadme(string readme)
    {
        Assert.Equal("UDF_PACKAGE_INVALID", Assert.Throws<AppException>(() => DaxLibPackageReader.Read(Package(new() { ["readme"] = readme }), "Example.Test", "1.0.0")).Code);
    }

    /// <summary>Git 真实快照入口复用同一元数据读取, 库级图标和适用祖先许可均不丢失.</summary>
    [Fact]
    public void GitSnapshotPreservesMetadataAndApplicableLicense()
    {
        var bytes = Package(new() { ["authors"] = "Alice", ["tags"] = "SVG", ["icon"] = "/icon.png" }, ("icon.png", Png(500, 500)));
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = zip.Entries.Select(entry =>
        {
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return new GitUdfFile("packages/e/example.test/1.0.0/" + entry.FullName, "100644", output.ToArray());
        }).Append(new GitUdfFile("LICENSE", "100644", "原始仓库许可"u8.ToArray())).ToArray();
        var package = Assert.Single(GitUdfCatalogSource.ReadSnapshot(files, null, default));
        Assert.Equal("Alice", package.Metadata?.Authors);
        Assert.Equal(ExpectedGitTags, package.Metadata?.Tags);
        Assert.StartsWith("data:image/png;base64,", package.Metadata?.IconDataUrl, StringComparison.Ordinal);
        Assert.Equal("原始仓库许可", package.LicenseFiles["repository/LICENSE"]);
        Assert.Equal("1.0.0", package.Version);
    }

    /// <summary>合法 PNG 只从包内 manifest 指定部件读取, 不改变完整输入摘要或原许可.</summary>
    [Theory]
    [InlineData("/assets/icon.png", "assets/icon.png", 1)]
    [InlineData("icon.png", "icon.png", 500)]
    [InlineData("/ICON.PNG", "icon.png", 1024)]
    public void EmbedsVerifiedPackagePng(string path, string part, uint dimension)
    {
        var png = Png(dimension, dimension);
        var bytes = Package(new() { ["icon"] = path }, (part, png), ("LICENSE", "原始许可"u8.ToArray()));
        var package = DaxLibPackageReader.Read(bytes, "Example.Test", "1.0.0");
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), package.Metadata?.IconDataUrl);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), package.Hash);
        Assert.Equal("原始许可", package.LicenseFiles["LICENSE"]);
    }

    /// <summary>外部、目录逃逸和脚本图标路径只能触发本地默认, 不阻止函数读取或读取其他部件.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("/missing.png")]
    [InlineData("https://example.org/icon.png")]
    [InlineData("//example.org/icon.png")]
    [InlineData("../icon.png")]
    [InlineData("assets/../icon.png")]
    [InlineData("assets//icon.png")]
    [InlineData("%2e%2e/icon.png")]
    [InlineData("assets\\icon.png")]
    [InlineData("icon.png?next=x")]
    [InlineData("icon.svg")]
    [InlineData("javascript:alert(1)")]
    public void UnusableIconPathUsesDefault(string? path)
    {
        var package = DaxLibPackageReader.Read(Package(new() { ["icon"] = path },
            ("icon.png", Png(1, 1)), ("icon.svg", "<svg onload=\"alert(1)\"/>"u8.ToArray())), "Example.Test", "1.0.0");
        Assert.Null(package.Metadata?.IconDataUrl);
        Assert.Single(package.Functions);
    }

    /// <summary>真实图片头、非零有界尺寸、首个 IHDR 和压缩后字节预算共同约束可显示图标.</summary>
    [Theory]
    [InlineData("signature")]
    [InlineData("truncated")]
    [InlineData("ihdr")]
    [InlineData("zero")]
    [InlineData("wide")]
    [InlineData("tall")]
    [InlineData("oversize")]
    [InlineData("crc")]
    [InlineData("missingEnd")]
    public void UnusablePngUsesDefault(string mode)
    {
        var png = Png(1, 1);
        switch (mode)
        {
            case "signature": png[0] = 0; break;
            case "truncated": png = png[..24]; break;
            case "ihdr": png[12] = (byte)'J'; break;
            case "zero": png = Png(0, 1); break;
            case "wide": png = Png(1025, 1); break;
            case "tall": png = Png(1, 1025); break;
            case "oversize": Array.Resize(ref png, 128 * 1024 + 1); break;
            case "crc": png[29] ^= 1; break;
            case "missingEnd": png = png[..^12]; break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        var package = DaxLibPackageReader.Read(Package(new() { ["icon"] = "/icon.png" }, ("icon.png", png)), "Example.Test", "1.0.0");
        Assert.Null(package.Metadata?.IconDataUrl);
        Assert.Single(package.Functions);
    }

    /// <summary>128 KiB 上限按实际完整 PNG 字节而不是扩展名/压缩后 ZIP 大小判定, 等于上限仍可展示.</summary>
    [Fact]
    public void PngByteBudgetAcceptsExactLimitAndRejectsOneAdditionalByte()
    {
        var commentBytes = 131072 - Png(1, 1).Length - 12;
        var exact = Png(1, 1, commentBytes);
        var over = Png(1, 1, commentBytes + 1);
        var accepted = DaxLibPackageReader.Read(Package(new() { ["icon"] = "/icon.png" }, ("icon.png", exact)), "Example.Test", "1.0.0");
        var rejected = DaxLibPackageReader.Read(Package(new() { ["icon"] = "/icon.png" }, ("icon.png", over)), "Example.Test", "1.0.0");
        Assert.Equal(131072, exact.Length);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(exact), accepted.Metadata?.IconDataUrl);
        Assert.Null(rejected.Metadata?.IconDataUrl);
    }

    /// <summary>大于旧预算且达到实际公开历史图标最大规模的合法 PNG 继续保留, 不仅因为大于 32 KiB 就退成默认图标.</summary>
    [Theory]
    [InlineData(32769)]
    [InlineData(98839)]
    public void RetainsPngAboveFormerBudgetAtOfficialHistoricalSizes(int totalBytes)
    {
        var png = Png(1, 1, totalBytes - Png(1, 1).Length - 12);
        var package = DaxLibPackageReader.Read(Package(new() { ["icon"] = "/icon.png" }, ("icon.png", png)), "Example.Test", "1.0.0");
        Assert.Equal(totalBytes, png.Length);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), package.Metadata?.IconDataUrl);
    }

    /// <summary>链接仅作为可信 HTTPS 元数据保留, 凭据、脚本、控制字符和超长内容不向前端透传.</summary>
    [Theory]
    [InlineData("http://example.org/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret")]
    [InlineData("https://user:secret@example.org/")]
    [InlineData("https://example.org/\nnext")]
    [InlineData("//example.org/")]
    public void UnsafeMetadataLinksAreOmitted(string url)
    {
        var package = DaxLibPackageReader.Read(Package(new() { ["authors"] = "Alice", ["projectUrl"] = url, ["repositoryUrl"] = url }), "Example.Test", "1.0.0");
        Assert.NotNull(package.Metadata);
        Assert.Null(package.Metadata.ProjectUrl);
        Assert.Null(package.Metadata.RepositoryUrl);
    }

    /// <summary>超长链接不可作为离线详情的可用元数据, 正常函数继续可读取.</summary>
    [Fact]
    public void OversizedMetadataLinkIsOmitted()
    {
        var package = DaxLibPackageReader.Read(Package(new() { ["projectUrl"] = "https://example.org/" + new string('a', 2048) }), "Example.Test", "1.0.0");
        Assert.Null(package.Metadata?.ProjectUrl);
    }

    /// <summary>非字符串或超额展示文本拒绝整包, 不以截断值改变作者或版本说明事实.</summary>
    [Theory]
    [InlineData("authors", "length")]
    [InlineData("authors", "type")]
    [InlineData("tags", "count")]
    [InlineData("tags", "length")]
    [InlineData("tags", "type")]
    [InlineData("releaseNotes", "length")]
    [InlineData("releaseNotes", "type")]
    public void RejectsInvalidDisplayMetadata(string field, string mode)
    {
        object value = mode switch
        {
            "type" => new[] { "wrong manifest type" },
            "count" => string.Join(',', Enumerable.Range(0, 65).Select(index => "tag" + index)),
            "length" when field == "authors" => new string('a', 1025),
            "length" when field == "tags" => new string('a', 65),
            _ => new string('中', 5462)
        };
        var error = Assert.Throws<AppException>(() => DaxLibPackageReader.Read(Package(new() { [field] = value }), "Example.Test", "1.0.0"));
        Assert.Equal("UDF_PACKAGE_INVALID", error.Code);
    }

    /// <summary>测试包复用真实函数 fixture, 仅按输入替换 manifest 和二进制部件.</summary>
    private static byte[] Package(Dictionary<string, object?> metadata, params (string Name, byte[] Bytes)[] parts)
    {
        using var output = new MemoryStream();
        output.Write(UdfPackageTests.Package());
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            zip.GetEntry("manifest.daxlib")!.Delete();
            metadata["id"] = "Example.Test";
            metadata["version"] = "1.0.0";
            using (var stream = zip.CreateEntry("manifest.daxlib").Open()) stream.Write(JsonSerializer.SerializeToUtf8Bytes(metadata));
            foreach (var (name, bytes) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    /// <summary>生成合法单色 PNG, 压缩扫描行及 CRC 由 fixture 独立构造, 不依赖产品校验函数.</summary>
    private static byte[] Png(uint width, uint height, int commentBytes = 0)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        WriteChunk(output, "IHDR", header);
        if (commentBytes > 0) WriteChunk(output, "tEXt", Encoding.ASCII.GetBytes("Comment\0" + new string('a', commentBytes - 8)));
        using var pixels = new MemoryStream();
        using (var compressor = new ZLibStream(pixels, CompressionLevel.SmallestSize, true)) compressor.Write(new byte[checked((int)((width + 1) * height))]);
        WriteChunk(output, "IDAT", pixels.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>向 fixture 写入标准 PNG chunk 和独立 CRC, 不触及磁盘或解码器.</summary>
    private static void WriteChunk(Stream output, string name, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        var type = Encoding.ASCII.GetBytes(name);
        output.Write(type);
        output.Write(data);
        uint crc = uint.MaxValue;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }
}
