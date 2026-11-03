// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitHubUdfLfsTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证哈希正确的 LFS 指针在包部件及其余仓库文件中均不被当作实际内容.

using PbiUtils.Infrastructure.Udf.Repositories.GitHub;
using PbiUtils.Infrastructure.Udf.Repositories;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>所有归档内容与树大小和 Git blob 哈希一致, 仅由正式 LFS 内容边界拒绝指针.</summary>
public sealed class GitHubUdfLfsTests
{
    /// <summary>README、图标和不会保留到包的仓库文件都必须是真实字节, 不能是 LFS 占位指针.</summary>
    [Theory]
    [InlineData("packages/e/example.test/1.0.0/README.md", "\n")]
    [InlineData("packages/e/example.test/1.0.0/README.md", "\r\n")]
    [InlineData("packages/e/example.test/1.0.0/icon.png", "\n")]
    [InlineData("packages/e/example.test/1.0.0/icon.png", "\r\n")]
    [InlineData("docs/large.bin", "\n")]
    [InlineData("docs/large.bin", "\r\n")]
    public void RejectsHashVerifiedLfsPointerInEveryOrdinaryFile(string path, string newline)
    {
        var pointer = "version https://git-lfs.github.com/spec/v1" + newline + "oid sha256:" + new string('a', 64) + newline + "size 12345" + newline;
        var error = Assert.Throws<AppException>(() => Read(path, pointer));
        Assert.Equal("UDF_GIT_INVALID", error.Code);
    }

    /// <summary>内容提到 LFS 或相似版本 URL 不构成指针, 普通 README 与非包文件保持可用.</summary>
    [Theory]
    [InlineData("version https://git-lfs.github.com/spec/v10\nregular documentation")]
    [InlineData("README\nversion https://git-lfs.github.com/spec/v1\nexample")]
    [InlineData("version https://git-lfs.github.com/spec/v1 is an example")]
    public void PreservesOrdinaryDataWithSimilarText(string text) => Assert.Equal(2, Read("packages/e/example.test/1.0.0/README.md", text).Count);

    /// <summary>构造完整双文件 ZIP 和准确树, 不以缺包、错路径或错哈希造成负向结果.</summary>
    private static IReadOnlyList<GitUdfFile> Read(string path, string text)
    {
        var files = new[] { (Path: "LICENSE", Content: "MIT"u8.ToArray()), (Path: path, Content: Encoding.UTF8.GetBytes(text)) };
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var stream = archive.CreateEntry("repository-commit/" + file.Path).Open(); stream.Write(file.Content); }
        var tree = files.Select(file => new GitUdfTreeEntry(file.Path, "100644",
            Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes("blob " + file.Content.Length.ToString(CultureInfo.InvariantCulture) + "\0"), .. file.Content])), file.Content.Length)).ToArray();
        return GitHubUdfArchive.Read(output.ToArray(), tree, "repository-commit/", default);
    }
}
