// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateDirectoryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板根说明文件的一次创建, 并发, 用户字节及链接保护.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>所有说明和权限测试均限制在本实例的临时模板根.</summary>
public sealed class TemplateDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-template-directory-" + Guid.NewGuid().ToString("N"));
    private string Templates => Path.Combine(root, "templates", "pbip");

    /// <summary>列表读取不创建根目录, 管理操作才生成完整双语说明.</summary>
    [Fact]
    public async Task ListingStaysReadOnlyAndManagementCreatesRootReadme()
    {
        var catalog = new ScaffoldTemplateCatalog(root);
        var before = await catalog.ListAsync(default);
        Assert.False(Directory.Exists(root));
        var helper = new TemplateDirectory(root);
        Assert.Equal(Templates, helper.RootPath);
        Assert.Equal(Templates, await helper.EnsureReadmeAsync(default));
        var text = await File.ReadAllTextAsync(Path.Combine(Templates, "readme.txt"));
        Assert.Contains("每个直接子目录", text, StringComparison.Ordinal);
        Assert.Contains("Each direct child directory", text, StringComparison.Ordinal);
        Assert.Contains("blank", text, StringComparison.Ordinal);
        var after = await catalog.ListAsync(default);
        Assert.Equal(before.Select(entry => (entry.Id, entry.Baseline)), after.Select(entry => (entry.Id, entry.Baseline)));
        Assert.Equal(["readme.txt"], Directory.EnumerateFileSystemEntries(Templates).Select(Path.GetFileName));
    }

    /// <summary>已有说明按任意原始字节保留, 无需读取或替换用户内容.</summary>
    [Fact]
    public async Task ExistingUserBytesAndTemplateBaselinesArePreserved()
    {
        var folder = Path.Combine(Templates, "custom");
        PbipFixture.Create(folder);
        var catalog = new ScaffoldTemplateCatalog(root);
        var before = (await catalog.ListAsync(default)).Single(entry => entry.Source == "user");
        byte[] bytes = [0xff, 0xfe, 0x00, 0xd8, 0x11, 0x00];
        var readme = Path.Combine(Templates, "readme.txt");
        await File.WriteAllBytesAsync(readme, bytes);
        Assert.Equal(Templates, await new TemplateDirectory(root).EnsureReadmeAsync(default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(readme));
        var after = (await catalog.ListAsync(default)).Single(entry => entry.Source == "user");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Baseline, after.Baseline);
        Assert.Single(Directory.EnumerateFiles(Templates));
    }

    /// <summary>多个实例并发首次创建只能发布一个完整说明, 不留临时文件.</summary>
    [Fact]
    public async Task ConcurrentFirstCreationPublishesOneCompleteFile()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => new TemplateDirectory(root).EnsureReadmeAsync(default)));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Templates, "readme.txt"));
        Assert.True(bytes.Length > 1000);
        Assert.Equal(["readme.txt"], Directory.EnumerateFileSystemEntries(Templates).Select(Path.GetFileName));
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => new TemplateDirectory(root).EnsureReadmeAsync(default)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(Templates, "readme.txt")));
    }

    /// <summary>已取消操作不创建目录或说明, 不能伪报目录已就绪.</summary>
    [Fact]
    public async Task CancelledManagementDoesNotCreateDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TemplateDirectory(root).EnsureReadmeAsync(cancellation.Token));
        Assert.False(Directory.Exists(root));
    }

    /// <summary>说明位置被目录占用时返回失败, 不伪报成功或删除现有内容.</summary>
    [Fact]
    public async Task ReadmeDirectoryIsFailureAndRemainsUntouched()
    {
        var occupied = Path.Combine(Templates, "readme.txt");
        Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Combine(occupied, "keep.txt"), "keep");
        var error = await Assert.ThrowsAsync<AppException>(() => new TemplateDirectory(root).EnsureReadmeAsync(default));
        Assert.Equal("TEMPLATE_DIRECTORY_FAILED", error.Code);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(occupied, "keep.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(Templates));
    }

    /// <summary>真实拒绝新建文件权限时报告安全错误, 不留下部分说明或临时文件.</summary>
    [Fact]
    public async Task WritePermissionFailureIsReportedWithoutPartialFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Templates);
        var folder = new DirectoryInfo(Templates);
        var original = folder.GetAccessControl();
        var denied = folder.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
        folder.SetAccessControl(denied);
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => new TemplateDirectory(root).EnsureReadmeAsync(default));
            Assert.Equal("TEMPLATE_ACCESS_DENIED", error.Code);
            Assert.DoesNotContain(Templates, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { folder.SetAccessControl(original); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Templates));
    }

    /// <summary>模板根或祖先是目录联接时拒绝, 外部目标中不会生成说明.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionRootOrAncestorCannotReceiveReadme(bool ancestor)
    {
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        var link = ancestor ? Path.Combine(root, "templates", "linked") : Templates;
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, outside },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var helper = new TemplateDirectory(ancestor ? link : root);
            var error = await Assert.ThrowsAsync<AppException>(() => helper.EnsureReadmeAsync(default));
            Assert.Equal("TEMPLATE_PATH_UNSAFE", error.Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>测试结束只删除已恢复权限且没有链接的自有目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
