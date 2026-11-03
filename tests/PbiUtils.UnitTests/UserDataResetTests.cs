// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UserDataResetTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅用随机临时目录验证全量重置、跨宿主租约、文件占用、链接防护及全部工作区账本保护.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Logging;
using System.Diagnostics;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>业务文件仅在随机临时目录; 外置租约只绑定该临时根, 不访问实际用户数据、凭据、模型或其他进程.</summary>
public sealed class UserDataResetTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "pbi-reset-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temporary, ".pbi-utils");

    /// <summary>所有已知分区与未知自定义内容均删除, 相邻用户工程保持原字节且校验不解密凭据.</summary>
    [Fact]
    public async Task ClearsEveryContentIncludingUnknownFilesWithoutTouchingSiblingProject()
    {
        foreach (var path in new[] { "Credentials/deepseek.dpapi", "Sessions/history.json", "cache/result.json", "Templates/custom/template.txt",
            "Backups/model.tmdl", "logs/desktop.clef", "WebView2/Default/cache.bin", "WebView2Learning/Default/cache.bin", "unknown/custom.bin", "ui-settings.json" })
            Write(path, "isolated-test-content");
        var project = Path.Combine(temporary, "external.pbip");
        await File.WriteAllTextAsync(project, "user-project-preserved");
        await new UserDataReset(Root).ResetAsync(default);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
        Assert.Equal("user-project-preserved", await File.ReadAllTextAsync(project));
    }

    /// <summary>任何文件仍被占用时先完成整体验证再拒绝, 排序在其前面的其他内容也不得先删除.</summary>
    [Fact]
    public async Task LockedFileRejectsBeforeAnyDeletionAndCanResetAfterRelease()
    {
        Write("a-preserved.txt", "unchanged");
        var path = Write("z-locked.txt", "locked");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default));
            Assert.Equal("USER_DATA_BUSY", failure.Code);
            Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(Root, "a-preserved.txt")));
        }
        await new UserDataReset(Root).ResetAsync(default);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    /// <summary>正常宿主共享同一根, 独占重置在任一活跃日志会话存在时零删除拒绝.</summary>
    [Fact]
    public async Task ActiveSessionsPreventResetAndDoNotDisableNormalMultipleInstances()
    {
        Write("preserved.txt", "original");
        using (var first = new LoggingRuntime(Root, "test"))
        using (var second = new LoggingRuntime(Root, "test"))
        {
            var failure = await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default));
            Assert.Equal("USER_DATA_BUSY", failure.Code);
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Root, "preserved.txt")));
        }
        await new UserDataReset(Root).ResetAsync(default);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    /// <summary>实际会话与维护租约共用 Profile 外置根, 全量重置不会删除或替换仍用于协调的文件.</summary>
    [Fact]
    public async Task ProfileCoordinationLeasesSurviveUserDataReset()
    {
        Write("reset-target.txt", "isolated-test-content");
        var runtimeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pbi-utils-runtime");
        string sessionPath;
        using (var session = UserDataSessionLease.Acquire(Root, exclusive: false))
        {
            sessionPath = session.Name;
            Assert.Equal(runtimeRoot, Path.GetDirectoryName(sessionPath));
        }
        using var maintenance = UserDataSessionLease.ReserveMaintenance(Root);
        Assert.Equal(runtimeRoot, Path.GetDirectoryName(maintenance.Name));

        await new UserDataReset(Root).ResetAsync(default);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
        Assert.True(File.Exists(sessionPath));
        Assert.True(File.Exists(maintenance.Name));
        Assert.True(UserDataSessionLease.IsMaintenanceReserved(Root));
        Assert.Equal("BUSINESS_HOST_MAINTENANCE", Assert.Throws<AppException>(() => UserDataSessionLease.Acquire(Root, exclusive: false)).Code);
    }

    /// <summary>GUI预约后已有日志仍可排空, 所有新日志宿主在触碰用户树前拒绝;预约跨实际重置保留.</summary>
    [Fact]
    public async Task MaintenanceReservationBlocksEveryNewLoggingSessionUntilResetCompletes()
    {
        var existing = new LoggingRuntime(Root, "host");
        using (UserDataSessionLease.ReserveMaintenance(Root))
        {
            Assert.True(UserDataSessionLease.IsMaintenanceReserved(Root));
            foreach (var kind in new[] { "cli", "desktop", "host" })
            {
                var error = Assert.Throws<AppException>(() => new LoggingRuntime(Root, kind));
                Assert.Equal("BUSINESS_HOST_MAINTENANCE", error.Code);
            }
            existing.Dispose();
            await new UserDataReset(Root).ResetAsync(default);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
            Assert.Equal("BUSINESS_HOST_MAINTENANCE", Assert.Throws<AppException>(() => new LoggingRuntime(Root, "cli")).Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
        }
        Assert.False(UserDataSessionLease.IsMaintenanceReserved(Root));
        using var resumed = new LoggingRuntime(Root, "cli");
        Assert.Equal("Information", resumed.Read().Level);
    }

    /// <summary>重置独占租约取得后, 新宿主必须在创建用户根之前失败, 不能在清理期间启动写入.</summary>
    [Fact]
    public void ExclusiveResetBlocksNewHostBeforeUserDirectoryCreation()
    {
        using var exclusive = UserDataSessionLease.Acquire(Root, exclusive: true);
        Assert.Equal("USER_DATA_BUSY", Assert.Throws<AppException>(() => new LoggingRuntime(Root, "test")).Code);
        Assert.False(Directory.Exists(Root));
    }

    /// <summary>直接新建或 CLI 留下的 committing 计划也属于未知发布, prepared 草稿可按用户重置意图清除.</summary>
    [Theory]
    [InlineData("committing")]
    [InlineData("prepared")]
    public async Task ProtectsUnresolvedScaffoldPlansButAllowsPreparedDrafts(string state)
    {
        var preserved = Write("preserved.txt", "original");
        var request = new ScaffoldRequest(1, "builtin:blank", new string('a', 64), "IsolatedProject", "zh-cn", null, null, null, null, null);
        var plan = new ScaffoldPlan(Guid.NewGuid().ToString("N"), "", DateTimeOffset.UtcNow.AddMinutes(5),
            Path.Combine(temporary, "external-output"), request, "IsolatedProject.pbip", [], [], [], 1702);
        plan = plan with { Digest = ScaffoldPlanStore.Digest(plan) };
        var store = new ScaffoldPlanStore(Root);
        using (store.Lease(plan.Digest)) store.Write(new(1, plan, state));
        if (state == "committing")
        {
            Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default))).Code);
            Assert.Equal("original", await File.ReadAllTextAsync(preserved));
            Assert.Equal("committing", store.Read(plan.Digest).State);
        }
        else
        {
            await new UserDataReset(Root).ResetAsync(default);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
        }
        Assert.False(Directory.Exists(plan.OutputPath));
    }

    /// <summary>计划协议损坏不能以空计划列表跳过, 全部内容在首个删除前保留.</summary>
    [Fact]
    public async Task CorruptScaffoldPlanPreventsReset()
    {
        var preserved = Write("preserved.txt", "original");
        var plan = Write("scaffold/plans/" + new string('b', 64) + ".json", "not-json");
        Assert.Equal("INVALID_SCAFFOLD_PLAN", (await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default))).Code);
        Assert.Equal("original", await File.ReadAllTextAsync(preserved));
        Assert.Equal("not-json", await File.ReadAllTextAsync(plan));
    }

    /// <summary>目录联接不会被递归跟随; 失败发生在第一项删除前, 相邻真实目录与目标文件保持原字节.</summary>
    [Fact]
    public async Task JunctionRejectsWholeResetAndPreservesExternalTarget()
    {
        Write("a-preserved.txt", "original");
        var outside = Path.Combine(temporary, "external-directory");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(target, "outside-preserved");
        var link = Path.Combine(Root, "z-link");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Equal("UNSAFE_USER_DATA_PATH", (await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default))).Code);
            Assert.Equal("outside-preserved", await File.ReadAllTextAsync(target));
            Assert.True(File.Exists(Path.Combine(Root, "a-preserved.txt")));
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>取得原对象句柄后祖先和子目录不能被外部改名换成联接, 删除不再依赖先检后开的路径.</summary>
    [Fact]
    public void PinnedTreeRejectsDirectoryReplacementBeforeDeletion()
    {
        Write("nested/keep.txt", "owned");
        using (var tree = new UserDataResetTree(Root, default))
        {
            Assert.Throws<IOException>(() => Directory.Move(Path.Combine(Root, "nested"), Path.Combine(Root, "replacement")));
            Assert.Throws<IOException>(() => Directory.Move(Root, Path.Combine(temporary, "renamed-root")));
            tree.ValidateReceipts(default);
            tree.DeleteContents();
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    /// <summary>已知不可删除的只读文件在整体预检中拒绝, 其他文件不能先被清除.</summary>
    [Fact]
    public async Task ReadOnlyFileRejectsBeforeDeletingOtherContent()
    {
        var preserved = Write("a-preserved.txt", "original");
        var readOnly = Write("z-readonly.txt", "read-only");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal("USER_DATA_BUSY", (await Assert.ThrowsAsync<AppException>(() => new UserDataReset(Root).ResetAsync(default))).Code);
            Assert.Equal("original", await File.ReadAllTextAsync(preserved));
        }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
    }

    /// <summary>不参与应用租约的外部程序新增内容时明确返回未完成, 不把局部删除或剩余内容标为成功.</summary>
    [Fact]
    public void UnexpectedNewFileReportsIncompleteReset()
    {
        var original = Write("original.txt", "owned");
        using var tree = new UserDataResetTree(Root, default);
        var late = Write("late.txt", "external-write");
        Assert.Equal("USER_DATA_RESET_INCOMPLETE", Assert.Throws<AppException>(tree.DeleteContents).Code);
        Assert.False(File.Exists(original));
        Assert.Equal("external-write", File.ReadAllText(late));
    }

    /// <summary>确认后的首个删除前仍支持取消, 取消不能产生空成功或已删除内容.</summary>
    [Fact]
    public async Task CancellationBeforeDeletePreservesContent()
    {
        var path = Write("preserved.txt", "original");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UserDataReset(Root).ResetAsync(cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    /// <summary>只为隔离根创建测试材料, 参数为相对分区和不含秘密的固定文本.</summary>
    private string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>测试拥有的联接已在用例内先解除, 最后仅删除本类随机临时目录.</summary>
    public void Dispose()
    {
        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
    }
}
