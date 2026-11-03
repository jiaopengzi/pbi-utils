// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\LocalModelExportLocationStoreTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证导出目录单条跨会话记录、随机身份、持久失败回退和磁盘链接安全边界.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Models.Export;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅使用独立临时用户目录, 不打开资源管理器或触及用户真实导出文件.</summary>
public sealed class LocalModelExportLocationStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-export-location-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Completed = new(2026, 10, 25, 1, 0, 0, TimeSpan.Zero);
    private string StoreRoot => Path.Combine(root, "profile");
    private string RecordPath => Path.Combine(StoreRoot, "model-export-location.json");
    private string LockPath => Path.Combine(StoreRoot, "model-export-location.lock");

    /// <summary>未导出时不建立记录, 成功记录的随机身份与完整目录跨实例保持.</summary>
    [Fact]
    public void SuccessfulLocationPersistsWithBoundIdentity()
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        Assert.Equal(new ModelExportLocation(null, null, false), store.Read());
        Assert.False(Directory.Exists(StoreRoot));
        var output = Output("中文 输出");
        var recorded = store.Remember(output, Completed);
        Assert.True(recorded.Available);
        Assert.True(Guid.TryParseExact(recorded.Id, "N", out _));
        Assert.Equal(Path.GetFullPath(output), recorded.DisplayPath);
        Assert.Null(recorded.Warning);
        Assert.False(recorded.IsSimulation);
        Assert.Equal(recorded, new LocalModelExportLocationStore(StoreRoot).Read());
        Assert.Equal(output, store.Resolve(recorded.Id!));
        Assert.Empty(Directory.EnumerateFiles(StoreRoot, "*.tmp"));
    }

    /// <summary>锁内读取最新记录, 旧完成时间的迟到保存不能覆盖较新的成功导出.</summary>
    [Fact]
    public void OlderCompletionCannotReplaceNewerRecordAndStaleIdIsRejected()
    {
        var first = new LocalModelExportLocationStore(StoreRoot);
        var old = first.Remember(Output("old"), Completed);
        var second = new LocalModelExportLocationStore(StoreRoot);
        var current = second.Remember(Output("new"), Completed.AddMinutes(2));
        Assert.NotEqual(old.Id, current.Id);
        Assert.Equal(current, first.Remember(Output("late-old"), Completed.AddMinutes(1)));
        Assert.Equal(current, first.Read());
        Assert.Equal("EXPORT_LOCATION_CHANGED", Assert.Throws<AppException>(() => first.Resolve(old.Id!)).Code);
        Assert.Equal(current.DisplayPath, first.Resolve(current.Id!));
    }

    /// <summary>目录被删除仍保留展示记录, 不创建目录或以父目录替代实际目标.</summary>
    [Fact]
    public void MissingDirectoryRemainsVisibleButCannotBeOpened()
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        var directory = Output("deleted");
        var location = store.Remember(directory, Completed);
        Directory.Delete(directory);
        var missing = store.Read();
        Assert.Equal(location.Id, missing.Id);
        Assert.Equal(directory, missing.DisplayPath);
        Assert.False(missing.Available);
        Assert.NotNull(missing.Warning);
        Assert.Equal("EXPORT_LOCATION_UNAVAILABLE", Assert.Throws<AppException>(() => store.Resolve(location.Id!)).Code);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>持久文件被占用时只保留会话回退, 新实例更晚的记录仍可取代旧回退.</summary>
    [Fact]
    public void FailedPersistenceRetainsSessionBindingWithoutMaskingNewerExternalRecord()
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        var original = store.Remember(Output("original"), Completed);
        var previousBytes = File.ReadAllBytes(RecordPath);
        ModelExportLocation fallback;
        using (UserDataStore.Lock(LockPath))
        {
            fallback = store.Remember(Output("session-only"), Completed.AddMinutes(1));
            Assert.True(fallback.Available);
            Assert.NotNull(fallback.Warning);
            Assert.Equal(fallback, store.Read());
            Assert.Equal(fallback.DisplayPath, store.Resolve(fallback.Id!));
            Assert.Equal(original, new LocalModelExportLocationStore(StoreRoot).Read());
            Assert.Equal(previousBytes, File.ReadAllBytes(RecordPath));
        }
        var newer = new LocalModelExportLocationStore(StoreRoot).Remember(Output("external-newer"), Completed.AddMinutes(2));
        Assert.Equal(newer, store.Read());
        Assert.Null(store.Read().Warning);
        Assert.Equal("EXPORT_LOCATION_CHANGED", Assert.Throws<AppException>(() => store.Resolve(fallback.Id!)).Code);
    }

    /// <summary>会话内较晚的未持久结果也不能被旧完成时间的后到调用替换.</summary>
    [Fact]
    public void OlderRememberCannotDiscardNewerSessionFallback()
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        store.Remember(Output("disk-old"), Completed);
        ModelExportLocation newer;
        using (UserDataStore.Lock(LockPath)) newer = store.Remember(Output("session-new"), Completed.AddMinutes(2));
        var bytes = File.ReadAllBytes(RecordPath);
        Assert.Equal(newer, store.Remember(Output("late-old"), Completed.AddMinutes(1)));
        Assert.Equal(newer, store.Read());
        Assert.Equal(bytes, File.ReadAllBytes(RecordPath));
    }

    /// <summary>非法目录文本不能记录成权限, 无效保存也不破坏已经记录的成功目录.</summary>
    /// <param name="directory">不能作为宿主目录的相对、设备或控制字符文本.</param>
    [Theory]
    [InlineData("relative/path")]
    [InlineData(@"\\?\C:\device")]
    [InlineData(@"\\.\C:\device")]
    [InlineData("C:\\bad\0path")]
    public void InvalidDirectoryCannotReplaceSuccessfulLocation(string directory)
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        var original = store.Remember(Output("valid"), Completed);
        var bytes = File.ReadAllBytes(RecordPath);
        var rejected = store.Remember(directory, Completed.AddMinutes(1));
        Assert.Equal(original.Id, rejected.Id);
        Assert.Equal(original.DisplayPath, rejected.DisplayPath);
        Assert.NotNull(rejected.Warning);
        Assert.Equal(original, store.Read());
        Assert.Equal(bytes, File.ReadAllBytes(RecordPath));
    }

    /// <summary>文件替换受阻不抛出原始IO异常, 原文件和可用会话目录保持.</summary>
    [Fact]
    public void FailedAtomicReplacementDoesNotTurnSuccessfulExportIntoFailure()
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        store.Remember(Output("first"), Completed);
        var bytes = File.ReadAllBytes(RecordPath);
        using (var held = new FileStream(RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = store.Remember(Output("second"), Completed.AddMinutes(1));
            Assert.True(result.Available);
            Assert.NotNull(result.Warning);
            Assert.Equal(result.DisplayPath, store.Resolve(result.Id!));
        }
        Assert.Equal(bytes, File.ReadAllBytes(RecordPath));
        Assert.Empty(Directory.EnumerateFiles(StoreRoot, "*.tmp"));
    }

    /// <summary>损坏/重复/未知字段与版本及超长记录均保留原文, 新成功只建立会话回退.</summary>
    /// <param name="fault">需要破坏的持久字段或大小.</param>
    [Theory]
    [InlineData("json")]
    [InlineData("version")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("id")]
    [InlineData("relative")]
    [InlineData("time")]
    [InlineData("missing")]
    [InlineData("oversized")]
    public void DamagedRecordIsPreservedAndDoesNotAuthorizeItsPath(string fault)
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        store.Remember(Output("previous"), Completed);
        var document = JsonNode.Parse(File.ReadAllText(RecordPath))!.AsObject();
        switch (fault)
        {
            case "version": document["schemaVersion"] = 99; break;
            case "unknown": document["other"] = "unsupported"; break;
            case "id": document["id"] = "not-an-id"; break;
            case "relative": document["directory"] = "relative/path"; break;
            case "time": document["completedAtUtc"] = "not-a-time"; break;
            case "missing": document.Remove("directory"); break;
        }
        var contents = fault == "json" ? "{broken" : fault == "duplicate" ? document.ToJsonString().Replace("{", "{\"schemaVersion\":1,", StringComparison.Ordinal)
            : fault == "oversized" ? new string(' ', 17000) : document.ToJsonString();
        File.WriteAllText(RecordPath, contents);
        var read = store.Read();
        Assert.Null(read.Id);
        Assert.False(read.Available);
        Assert.NotNull(read.Warning);
        var next = store.Remember(Output("new-success"), Completed.AddMinutes(1));
        Assert.True(next.Available);
        Assert.NotNull(next.Warning);
        Assert.Equal(next.DisplayPath, store.Resolve(next.Id!));
        Assert.Equal(contents, File.ReadAllText(RecordPath));
    }

    /// <summary>接口只接受随机记录身份, 目录文本和畸形身份不能作为打开权限.</summary>
    [Fact]
    public void ResolveRejectsPathsAndUnknownIds()
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        var location = store.Remember(Output("bound"), Completed);
        Assert.Equal("EXPORT_LOCATION_CHANGED", Assert.Throws<AppException>(() => store.Resolve(location.DisplayPath!)).Code);
        Assert.Equal("EXPORT_LOCATION_CHANGED", Assert.Throws<AppException>(() => store.Resolve(Guid.NewGuid().ToString("N"))).Code);
        Assert.Equal("EXPORT_LOCATION_CHANGED", Assert.Throws<AppException>(() => store.Resolve("")).Code);
    }

    /// <summary>输出本身或祖先变成目录联接时不跟随目标, 用户存储联接也不能被读取/覆盖.</summary>
    /// <param name="replacement">替换输出、祖先或用户记录目录.</param>
    [Theory]
    [InlineData("output")]
    [InlineData("ancestor")]
    [InlineData("store")]
    public void ReparseReplacementCannotAuthorizeOrModifyLinkedDirectory(string replacement)
    {
        var store = new LocalModelExportLocationStore(StoreRoot);
        var output = Output("parent/output");
        var location = store.Remember(output, Completed);
        var originalBytes = File.ReadAllBytes(RecordPath);
        var link = replacement == "store" ? StoreRoot : replacement == "ancestor" ? Path.GetDirectoryName(output)! : output;
        var target = Path.Combine(root, "moved-target");
        Directory.Move(link, target);
        CreateJunction(link, target);
        try
        {
            var read = store.Read();
            Assert.False(read.Available);
            Assert.NotNull(read.Warning);
            var error = Assert.Throws<AppException>(() => store.Resolve(location.Id!));
            Assert.Equal(replacement == "store" ? "EXPORT_LOCATION_CHANGED" : "EXPORT_LOCATION_UNAVAILABLE", error.Code);
            if (replacement == "store")
            {
                var next = store.Remember(output, Completed.AddMinutes(1));
                Assert.True(next.Available);
                Assert.NotNull(next.Warning);
                Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(target, "model-export-location.json")));
            }
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>只在本轮隔离根下创建测试目录, 返回规范化绝对路径.</summary>
    private string Output(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>对已核验测试路径创建目录联接, 自有工具必须按时退出且不启动可见窗口.</summary>
    private void CreateJunction(string link, string target)
    {
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); Assert.Fail("测试目录联接创建超时."); }
        Assert.Equal(0, process.ExitCode);
        Assert.NotEqual((FileAttributes)0, File.GetAttributes(link) & FileAttributes.ReparsePoint);
    }

    /// <summary>只清理已核验无链接的本次临时工作区, 不删除用户记录或导出目录.</summary>
    public void Dispose()
    {
        UserDataStore.Check(root);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
