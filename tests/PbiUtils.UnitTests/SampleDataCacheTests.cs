// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\SampleDataCacheTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 示例结果缓存的重启恢复、快速命中、外部改写、版本保留与提交后故障边界回归.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.SampleData;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>所有缓存和产物只写入本测试的独立临时目录, 不修改真实用户数据.</summary>
public sealed class SampleDataCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-sample-cache-" + Guid.NewGuid().ToString("N"));
    private static readonly SampleDataOptions Options = new("zh-cn", 2, 2, 14, "2024-01-01", "2024-03-01", 42);
    private string Output => Path.Combine(root, "output");
    private string CacheRoot => Path.Combine(root, "user-data");
    private string CachePath => Path.Combine(CacheRoot, "sample-data-result.json");

    /// <summary>新的服务和桥接会话恢复宿主目录与完整统计, 命中不再调用逐行统计.</summary>
    [Fact]
    public async Task RestartRestoresBoundDirectoryAndAvoidsCsvScan()
    {
        WriteProducts(2);
        var initial = await new SampleDataService(new SampleDataAdapter(), cache: new LocalSampleDataCache(CacheRoot)).GetAsync(Output);
        var adapter = new CountingAdapter { FailInspection = true };
        var service = new SampleDataService(adapter, cache: new LocalSampleDataCache(CacheRoot));
        Assert.Equal(Output, service.RestoreOutputPath());
        var actual = await service.GetAsync(Output);
        Assert.Equal(initial.Tables, actual.Tables);
        Assert.Equal(1, adapter.Selections);
        Assert.Equal(0, adapter.Inspections);
        ConcurrentQueue<JsonElement> messages = new();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test", samples: service);
        await session.ReceiveAsync(OriginPolicy.Production, """{"protocolVersion":1,"id":"status","method":"sample.status","params":{}}""");
        var result = messages.Last().GetProperty("result");
        Assert.Equal(Output, result.GetProperty("outputPath").GetString());
        Assert.Equal(2, result.GetProperty("tables")[0].GetProperty("rows").GetInt64());
        Assert.False(result.TryGetProperty("fileStamps", out _));
        Assert.Equal(0, adapter.Inspections);
    }

    /// <summary>显式宿主目录优先于缓存, 网页仍不得传入任意目录覆盖宿主绑定.</summary>
    [Fact]
    public async Task ExplicitHostDirectoryOverridesPersistedSelection()
    {
        WriteProducts(1);
        await new SampleDataService(new SampleDataAdapter(), cache: new LocalSampleDataCache(CacheRoot)).GetAsync(Output);
        var explicitOutput = Path.Combine(root, "explicit");
        ConcurrentQueue<JsonElement> messages = new();
        using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()),
            message => messages.Enqueue(JsonSerializer.Deserialize<JsonElement>(message)), "test", samples: new(new SampleDataAdapter(), cache: new LocalSampleDataCache(CacheRoot)), sampleDirectory: explicitOutput);
        await session.ReceiveAsync(OriginPolicy.Production, """{"protocolVersion":1,"id":"status","method":"sample.status","params":{}}""");
        Assert.Equal(explicitOutput, messages.Last().GetProperty("result").GetProperty("outputPath").GetString());
        await session.ReceiveAsync(OriginPolicy.Production, """{"protocolVersion":1,"id":"injected","method":"sample.status","params":{"outputPath":"untrusted"}}""");
        Assert.Equal("VALIDATION_ERROR", messages.Last().GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>启动恢复仅读缓存文本, 已被替换为文件的旧目标由后续真实操作安全拒绝.</summary>
    [Fact]
    public async Task StartupRestoreDoesNotProbeFormerOutputDirectory()
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var moved = Path.Combine(root, "moved-output");
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(Output), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(moved), StringComparison.OrdinalIgnoreCase);
        Directory.Move(Output, moved);
        File.WriteAllText(Output, "external replacement");
        Assert.Equal(Output, cache.RestoreOutputPath());
        Assert.Equal(Output, service.RestoreOutputPath());
        Assert.Equal("SAMPLE_INVALID_OUTPUT", Assert.Throws<AppException>(() => cache.Read(Output)).Code);
        Assert.Equal("SAMPLE_INVALID_OUTPUT", (await Assert.ThrowsAsync<AppException>(() => service.GetAsync(Output))).Code);
    }

    /// <summary>切换到另一宿主目录时不应探测已失效的旧缓存目标或记录其路径失败.</summary>
    [Fact]
    public async Task SwitchingDirectoryDoesNotProbeFormerCachedTarget()
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        await new SampleDataService(new SampleDataAdapter(), cache: cache).GetAsync(Output);
        var moved = Path.Combine(root, "prior-output");
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(Output), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(moved), StringComparison.OrdinalIgnoreCase);
        Directory.Move(Output, moved);
        File.WriteAllText(Output, "old target is no longer a directory");
        var selected = Path.Combine(root, "selected-output");
        Assert.Null(cache.Read(selected));
        var logger = new CapturingLogger();
        var current = await new SampleDataService(new SampleDataAdapter(), logger, cache).GetAsync(selected);
        Assert.Equal(selected, current.OutputPath);
        Assert.False(current.HasData);
        Assert.DoesNotContain(logger.Events, item => item.Operation == "SampleData.CacheRead" && item.Outcome == "Failed");
        Assert.Equal(selected, cache.RestoreOutputPath());
    }

    /// <summary>文件新增、删除、同大小时间变化和 PBIP 存在状态变化都会使缓存失效并真实回读.</summary>
    [Theory]
    [InlineData("append")]
    [InlineData("delete")]
    [InlineData("time")]
    [InlineData("pbip")]
    public async Task ChangedMetadataTriggersAuthoritativeInspection(string mutation)
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        await new SampleDataService(new SampleDataAdapter(), cache: cache).GetAsync(Output);
        var product = Path.Combine(Output, "data", SampleTables.FileProduct);
        switch (mutation)
        {
            case "append": WriteProducts(2); break;
            case "delete": File.Delete(product); break;
            case "time": File.SetLastWriteTimeUtc(product, File.GetLastWriteTimeUtc(product).AddSeconds(1)); break;
            case "pbip": Directory.CreateDirectory(Path.Combine(Output, "pbip")); break;
        }
        Assert.False(cache.Read(Output)!.IsCurrent);
        var adapter = new CountingAdapter();
        var actual = await new SampleDataService(adapter, cache: cache).GetAsync(Output);
        Assert.Equal(1, adapter.Inspections);
        Assert.True(cache.Read(Output)!.IsCurrent);
        if (mutation == "append") Assert.Equal(2, Assert.Single(actual.Tables).Rows);
        if (mutation == "delete") { Assert.False(actual.HasData); Assert.Empty(actual.Tables); }
    }

    /// <summary>外部把已缓存 CSV 改坏时保留真实校验错误, 轻量选择仍可识别需覆盖的旧产物.</summary>
    [Fact]
    public async Task CorruptChangedCsvIsNotReturnedAsCachedSuccess()
    {
        WriteProducts(1);
        var service = new SampleDataService(new SampleDataAdapter(), cache: new LocalSampleDataCache(CacheRoot));
        await service.GetAsync(Output);
        File.WriteAllText(Path.Combine(Output, "data", SampleTables.FileProduct), "broken");
        Assert.True((await service.GetSelectionAsync(Output)).HasData);
        Assert.Equal("SAMPLE_INVALID_CSV", (await Assert.ThrowsAsync<AppException>(() => service.GetAsync(Output))).Code);
    }

    /// <summary>全量与增量发布后缓存真实统计, 清空后原子替换为空结果, 新会话不复活旧统计.</summary>
    [Fact]
    public async Task GenerationIncrementAndClearKeepPersistedStateCurrent()
    {
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        var full = await service.GenerateAsync(Output, Options);
        Assert.True(cache.Read(Output)!.IsCurrent);
        Assert.Equal(full.State.Tables, cache.Read(Output)!.State.Tables);
        var incremental = await service.IncrementAsync(Output, Options with { StartDate = "2024-03-02", EndDate = "2024-05-01" });
        Assert.True(cache.Read(Output)!.IsCurrent);
        Assert.Equal(incremental.State.Tables, cache.Read(Output)!.State.Tables);
        var cleared = await service.ClearAsync(Output);
        Assert.False(cleared.HasData);
        var restored = cache.Read(Output)!;
        Assert.True(restored.IsCurrent);
        Assert.False(restored.State.HasData);
        Assert.Empty(restored.State.Tables);
        Assert.Equal(Output, new SampleDataService(new SampleDataAdapter(), cache: cache).RestoreOutputPath());
        Assert.Empty(Directory.GetFiles(CacheRoot, "*.tmp"));
    }

    /// <summary>损坏、未知版本、重复字段和非法统计既不用于恢复也不被后续正常统计静默覆盖.</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("negative")]
    [InlineData("path")]
    [InlineData("table")]
    [InlineData("oversize")]
    [InlineData("malformed")]
    public async Task UnrecognizedCacheIsPreservedAndActualFilesStillLoad(string mutation)
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var document = JsonNode.Parse(File.ReadAllText(CachePath))!;
        switch (mutation)
        {
            case "version": document["schemaVersion"] = 2; break;
            case "unknown": document["unexpected"] = true; break;
            case "missing": document["state"]!.AsObject().Remove("hasData"); break;
            case "negative": document["state"]!["tables"]![0]!["rows"] = -1; break;
            case "path": document["state"]!["outputPath"] = "relative"; break;
            case "table": document["state"]!["tables"]![0]!["name"] = "unexpected"; break;
        }
        var text = mutation switch
        {
            "duplicate" => "{\"schemaVersion\":1," + document.ToJsonString()[1..],
            "oversize" => new string(' ', 16385),
            "malformed" => "{",
            _ => document.ToJsonString(),
        };
        File.WriteAllText(CachePath, text);
        Assert.Equal("INVALID_SAMPLE_CACHE", Assert.Throws<AppException>(() => cache.Read(Output)).Code);
        Assert.Null(service.RestoreOutputPath());
        Assert.Equal(1, Assert.Single((await service.GetAsync(Output)).Tables).Rows);
        Assert.Equal(text, File.ReadAllText(CachePath));
    }

    /// <summary>缓存被另一进程锁定时生成结果保持成功, 原缓存字节不被覆盖.</summary>
    [Fact]
    public async Task BusyCacheDoesNotTurnPublishedGenerationIntoFailure()
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var before = File.ReadAllBytes(CachePath);
        using (UserDataStore.Lock(Path.Combine(CacheRoot, "sample-data-result.lock")))
        {
            var result = await service.GenerateAsync(Output, Options, overwrite: true);
            Assert.Equal(11, result.State.Tables.Count);
            Assert.Equal(before, File.ReadAllBytes(CachePath));
        }
        Assert.False(cache.Read(Output)!.IsCurrent);
        Assert.Equal(11, (await service.GetAsync(Output)).Tables.Count);
        Assert.True(cache.Read(Output)!.IsCurrent);
    }

    /// <summary>统计后同大小改写不允许把旧行数与新文件时间戳组合成有效缓存.</summary>
    [Fact]
    public async Task SavingOldObservationAfterExternalReplacementIsSkipped()
    {
        WriteProducts(1);
        var state = await new SampleDataAdapter().InspectAsync(Output, CancellationToken.None);
        var file = Path.Combine(Output, "data", SampleTables.FileProduct);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(1));
        new LocalSampleDataCache(CacheRoot).Save(state);
        Assert.False(File.Exists(CachePath));
        Assert.False(Directory.Exists(CacheRoot));
    }

    /// <summary>结果目录被外部删除后回读及清空均保存真实空状态, 不重建数据根或复活旧统计.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedDataRootBecomesPersistedEmptyState(bool clear)
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var moved = Path.Combine(root, "removed-output");
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(Output), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(moved), StringComparison.OrdinalIgnoreCase);
        Directory.Move(Output, moved);
        Assert.False(cache.Read(Output)!.IsCurrent);
        var actual = clear ? await service.ClearAsync(Output) : await service.GetAsync(Output);
        Assert.False(actual.HasData);
        Assert.Empty(actual.Tables);
        Assert.Null(actual.LastFactDate);
        Assert.False(Directory.Exists(Output));
        Assert.True(cache.Read(Output)!.IsCurrent);
        Assert.False(cache.Read(Output)!.State.HasData);
    }

    /// <summary>缓存目标目录、根类型冲突、读取占用及只读文件故障不覆盖已提交的生成或清空事实.</summary>
    [Theory]
    [InlineData("root-file")]
    [InlineData("cache-directory")]
    [InlineData("locked-file")]
    [InlineData("readonly-file")]
    public async Task CacheStorageFailurePreservesCommittedFileOperations(string failure)
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var bytes = File.ReadAllBytes(CachePath);
        var backup = Path.Combine(root, "cache-backup");
        FileStream? lease = null;
        try
        {
            switch (failure)
            {
                case "root-file":
                    Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(CacheRoot), StringComparison.OrdinalIgnoreCase);
                    Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(backup), StringComparison.OrdinalIgnoreCase);
                    Directory.Move(CacheRoot, backup);
                    File.WriteAllText(CacheRoot, "cache root replaced by a file");
                    break;
                case "cache-directory": File.Move(CachePath, backup); Directory.CreateDirectory(CachePath); break;
                case "locked-file": lease = new FileStream(CachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break;
                case "readonly-file": File.SetAttributes(CachePath, FileAttributes.ReadOnly); break;
            }
            var generated = await service.GenerateAsync(Output, Options, overwrite: true);
            Assert.Equal(11, generated.State.Tables.Count);
            Assert.True(File.Exists(Path.Combine(Output, "pbip", "demo.pbip")));
            var cleared = await service.ClearAsync(Output);
            Assert.False(cleared.HasData);
            Assert.Empty(cleared.Tables);
            Assert.False(Directory.Exists(Path.Combine(Output, "pbip")));
        }
        finally
        {
            lease?.Dispose();
            if (failure == "readonly-file") File.SetAttributes(CachePath, FileAttributes.Normal);
        }
        if (failure == "root-file") Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(backup, "sample-data-result.json")));
        else if (failure == "cache-directory") Assert.Equal(bytes, File.ReadAllBytes(backup));
        else Assert.Equal(bytes, File.ReadAllBytes(CachePath));
        if (Directory.Exists(CacheRoot)) Assert.Empty(Directory.GetFiles(CacheRoot, "*.tmp"));
    }

    /// <summary>输出根、数据子树和用户缓存根出现目录联接时安全拒绝跟随且保留联接目标字节.</summary>
    [Theory]
    [InlineData("output")]
    [InlineData("data")]
    [InlineData("cache")]
    public async Task ReparseReplacementNeverAuthorizesOrModifiesLinkedTarget(string replaced)
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var link = replaced == "output" ? Output : replaced == "data" ? Path.Combine(Output, "data") : CacheRoot;
        var target = Path.Combine(root, "junction-target");
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        Directory.Move(link, target);
        var protectedFile = replaced == "cache" ? Path.Combine(target, "sample-data-result.json")
            : Path.Combine(target, replaced == "output" ? "data" : "", SampleTables.FileProduct);
        var before = File.ReadAllBytes(protectedFile);
        CreateJunction(link, target);
        try
        {
            if (replaced == "cache")
            {
                Assert.Equal("UNSAFE_USER_DATA_PATH", Assert.Throws<AppException>(() => cache.Read(Output)).Code);
                Assert.Null(service.RestoreOutputPath());
                Assert.Equal(1, Assert.Single((await service.GetAsync(Output)).Tables).Rows);
            }
            else
            {
                Assert.Equal("SAMPLE_INVALID_OUTPUT", Assert.Throws<AppException>(() => cache.Read(Output)).Code);
                Assert.Equal("SAMPLE_INVALID_OUTPUT", (await Assert.ThrowsAsync<AppException>(() => service.GetAsync(Output))).Code);
                Assert.Equal("SAMPLE_INVALID_OUTPUT", (await Assert.ThrowsAsync<AppException>(() => service.ClearAsync(Output))).Code);
            }
            Assert.Equal(before, File.ReadAllBytes(protectedFile));
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>发布后的实际文件被外部修改时旧统计不得附加新标记持久化, 已发布结果仍成功返回.</summary>
    [Fact]
    public async Task ExternalChangeImmediatelyAfterPublishCannotBecomeCurrentCache()
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var before = File.ReadAllBytes(CachePath);
        var progress = new CallbackProgress(value =>
        {
            if (value.Stage != "stageDone") return;
            using var append = new SampleCsvStorage(Path.Combine(Output, "data")).Append(SampleTables.FileProduct);
            append.Write(["3", "P3", "A类", "外部追加", "10", "5"]);
        });
        var result = await service.GenerateAsync(Output, Options, overwrite: true, progress: progress);
        Assert.Equal(2, result.State.Tables.Single(table => table.Name == Path.GetFileNameWithoutExtension(SampleTables.FileProduct)).Rows);
        Assert.Equal(before, File.ReadAllBytes(CachePath));
        Assert.False(cache.Read(Output)!.IsCurrent);
        var current = await service.GetAsync(Output);
        Assert.Equal(3, current.Tables.Single(table => table.Name == Path.GetFileNameWithoutExtension(SampleTables.FileProduct)).Rows);
        Assert.True(cache.Read(Output)!.IsCurrent);
    }

    /// <summary>缓存保存已开始时发生业务取消不丢失提交成功或持久化结果, 保存结束前不提前返回.</summary>
    [Fact]
    public async Task CancellationWhileCacheSaveRunsStillDrainsCommittedResult()
    {
        using var cancellation = new CancellationTokenSource();
        using var cache = new BlockingCache(new LocalSampleDataCache(CacheRoot));
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        var generation = service.GenerateAsync(Output, Options, cancellationToken: cancellation.Token);
        try
        {
            await cache.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(generation.IsCompleted);
        }
        finally { cache.Release.Set(); }
        var result = await generation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.State.HasData);
        Assert.True(new LocalSampleDataCache(CacheRoot).Read(Output)!.IsCurrent);
    }

    /// <summary>仅选择目录不改写先前结果, 完整回读新目录后才持久化新的宿主结果.</summary>
    [Fact]
    public async Task SelectionAloneRetainsPreviousResultUntilActualRead()
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var before = File.ReadAllBytes(CachePath);
        var chosen = Path.Combine(root, "new-selection");
        Assert.False((await service.GetSelectionAsync(chosen)).HasData);
        Assert.Equal(before, File.ReadAllBytes(CachePath));
        Assert.Equal(Output, cache.RestoreOutputPath());
        await service.GetAsync(chosen);
        Assert.Equal(chosen, cache.RestoreOutputPath());
        Assert.True(cache.Read(chosen)!.IsCurrent);
        Assert.Null(cache.Read(Output));
    }

    /// <summary>未注入缓存的 CLI 共享服务始终真实统计, 不读取或覆盖旁边已经存在的桌面缓存.</summary>
    [Fact]
    public async Task UnconfiguredServiceDoesNotConsumeOrRefreshDesktopCache()
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        await new SampleDataService(new SampleDataAdapter(), cache: cache).GetAsync(Output);
        var before = File.ReadAllBytes(CachePath);
        WriteProducts(2);
        var uncached = new SampleDataService(new SampleDataAdapter());
        Assert.Null(uncached.RestoreOutputPath());
        Assert.Equal(2, Assert.Single((await uncached.GetAsync(Output)).Tables).Rows);
        Assert.Equal(before, File.ReadAllBytes(CachePath));
        Assert.False(cache.Read(Output)!.IsCurrent);
    }

    /// <summary>提交前取消保留已有缓存, 未配置缓存的 CLI 共享服务不隐式创建用户文件.</summary>
    [Fact]
    public async Task CancellationAndDisabledCacheDoNotWrite()
    {
        WriteProducts(1);
        var cache = new LocalSampleDataCache(CacheRoot);
        var service = new SampleDataService(new SampleDataAdapter(), cache: cache);
        await service.GetAsync(Output);
        var before = File.ReadAllBytes(CachePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(Output, Options, overwrite: true, cancellationToken: new(true)));
        Assert.Equal(before, File.ReadAllBytes(CachePath));
        using var afterCommit = new CancellationTokenSource();
        var generated = await service.GenerateAsync(Output, Options, overwrite: true, beginCommit: afterCommit.Cancel, cancellationToken: afterCommit.Token);
        Assert.True(generated.State.HasData);
        Assert.True(cache.Read(Output)!.IsCurrent);
        var uncached = Path.Combine(root, "uncached");
        await new SampleDataService(new SampleDataAdapter()).GetAsync(uncached);
        Assert.False(Directory.Exists(uncached));
    }

    /// <summary>写入固定结构的少量真实 CSV 行, 统计仍经过产品解析器.</summary>
    /// <param name="count">产品数据行数.</param>
    private void WriteProducts(int count)
    {
        using var writer = new SampleCsvStorage(Path.Combine(Output, "data")).Create(SampleTables.FileProduct, SampleTables.HeaderProduct);
        for (var index = 0; index < count; index++) writer.Write([(index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "P1", "A类", "产品", "10", "5"]);
    }

    /// <summary>在已核实属于测试工作区的两个目录之间创建系统目录联接, 等待自有工具退出.</summary>
    /// <param name="link">测试工作区内的联接节点.</param>
    /// <param name="target">测试工作区内的实际目标目录.</param>
    private void CreateJunction(string link, string target)
    {
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); Assert.Fail("测试目录联接创建超时."); }
        Assert.Equal(0, process.ExitCode);
        Assert.NotEqual((FileAttributes)0, File.GetAttributes(link) & FileAttributes.ReparsePoint);
    }

    /// <summary>把同步真实进度转交测试回调, 精确定位发布后的外部变更时机.</summary>
    private sealed class CallbackProgress(Action<SampleDataProgress> report) : IProgress<SampleDataProgress>
    {
        /// <summary>立即执行固定阶段回调, 不排队到 UI 上下文.</summary>
        public void Report(SampleDataProgress value) => report(value);
    }

    /// <summary>仅阻塞可选缓存保存以构造取消竞争, 实际写盘仍使用产品缓存.</summary>
    private sealed class BlockingCache(LocalSampleDataCache inner) : ISampleDataCache, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        /// <inheritdoc/>
        public string? RestoreOutputPath() => inner.RestoreOutputPath();

        /// <inheritdoc/>
        public SampleDataCacheEntry? Read(string expectedOutputPath) => inner.Read(expectedOutputPath);

        /// <summary>在实际保存之前暴露门闩, 超时显式失败而不泄漏等待线程.</summary>
        public void Save(SampleDataState state)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("测试缓存门闩未释放.");
            inner.Save(state);
        }

        /// <summary>释放本测试创建的同步门闩.</summary>
        public void Dispose() => Release.Dispose();
    }

    /// <summary>包装真实适配器并统计逐行读取次数, 快速命中时可以禁止解析 CSV.</summary>
    private sealed class CountingAdapter : ISampleDataAdapter
    {
        private readonly SampleDataAdapter adapter = new();
        public int Selections { get; private set; }
        public int Inspections { get; private set; }
        public bool FailInspection { get; init; }

        /// <summary>实际复核目录及产物存在状态.</summary>
        public Task<SampleDataState> SelectAsync(string outputPath, CancellationToken cancellationToken)
        {
            Selections++;
            return adapter.SelectAsync(outputPath, cancellationToken);
        }

        /// <summary>记录实际解析次数, 禁止读取时以确定性异常识别误走完整统计.</summary>
        public Task<SampleDataState> InspectAsync(string outputPath, CancellationToken cancellationToken)
        {
            Inspections++;
            if (FailInspection) throw new InvalidOperationException("Unexpected CSV scan.");
            return adapter.InspectAsync(outputPath, cancellationToken);
        }

        /// <inheritdoc/>
        public Task<SampleDataResponse> GenerateAsync(string outputPath, SampleDataOptions options, bool incremental, bool overwrite,
            IProgress<SampleDataProgress>? progress, Action? beginCommit, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null) =>
            adapter.GenerateAsync(outputPath, options, incremental, overwrite, progress, beginCommit, cancellationToken, beforeCommit);

        /// <inheritdoc/>
        public Task<SampleDataState> ClearAsync(string outputPath, Action? beginCommit, CancellationToken cancellationToken) =>
            adapter.ClearAsync(outputPath, beginCommit, cancellationToken);
    }

    /// <summary>捕获真实业务事件字段, 用于验证不相关旧目录不会触发缓存读取失败.</summary>
    private sealed class CapturingLogger : ILogger<SampleDataService>
    {
        public ConcurrentQueue<(string? Operation, string? Outcome)> Events { get; } = new();

        /// <summary>本测试只检查事件字段, 不持有作用域资源.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>保留所有级别, 不隐藏误探测旧目标产生的故障事件.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <summary>只读取固定业务关联字段, 不复制路径、异常正文或用户数据.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            Events.Enqueue((values.GetValueOrDefault("Operation") as string, values.GetValueOrDefault("Outcome") as string));
        }
    }

    /// <summary>仅清理本测试创建且已核实位于临时目录中的工作区.</summary>
    public void Dispose()
    {
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
