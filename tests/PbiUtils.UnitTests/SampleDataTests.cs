// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\SampleDataTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 示例数据完整模板, 生成管理, 真实统计与安全发布边界回归.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.SampleData;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>从共享服务实际写入临时文件, 不将浏览器模拟视为数据生成验证.</summary>
public sealed class SampleDataTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "pbi-utils-sample-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SampleDataService service = new(new SampleDataAdapter());
    private static readonly SampleDataOptions Options = new("zh-cn", 8, 9, 14, "2021-01-01", "2025-06-01", 42);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly string[] expected = ["stagePbip", "stageSummary", "stageDone"];

    /// <summary>提交钩子的异步回执完成前不发布 CSV/PBIP, 完成后保留原提交后排空规则.</summary>
    [Fact]
    public async Task AwaitsBeforeCommitHookBeforePublishing()
    {
        var root = Root("异步回执");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var committed = false;
        var generating = service.GenerateAsync(root, Options,
            beginCommit: () => { committed = true; cancellation.Cancel(); }, cancellationToken: cancellation.Token,
            beforeCommit: async token => { entered.SetResult(); await release.Task.WaitAsync(token); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(generating.IsCompleted);
        Assert.False(committed);
        Assert.False(Directory.Exists(Path.Combine(root, "data")));
        Assert.False(Directory.Exists(Path.Combine(root, "pbip")));
        release.SetResult();
        Assert.True((await generating).State.HasData);
        Assert.True(committed);
        Assert.True(cancellation.IsCancellationRequested);
        AssertNoStage();
    }

    /// <summary>回执失败、取消或等待期间外部变更不能发布, 外部文件完整保留且 staging 清理.</summary>
    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("baseline")]
    public async Task BeforeCommitHookFailureCancellationOrChangedBaselineDoesNotPublish(string scenario)
    {
        var root = Root("回执阻止发布");
        using var cancellation = new CancellationTokenSource();
        var committed = false;
        var error = await Record.ExceptionAsync(() => service.GenerateAsync(root, Options,
            beginCommit: () => committed = true, cancellationToken: cancellation.Token,
            beforeCommit: async token =>
            {
                Assert.Equal(cancellation.Token, token);
                await Task.Yield();
                if (scenario == "failure") throw new AppException("RECEIPT_FAILED", "回执失败");
                if (scenario == "cancel") cancellation.Cancel();
                else
                {
                    Directory.CreateDirectory(Path.Combine(root, "data"));
                    await File.WriteAllTextAsync(Path.Combine(root, "data", "external.txt"), "保留外部修改", token);
                }
            }));
        if (scenario == "cancel") Assert.IsType<OperationCanceledException>(error, exactMatch: false);
        else Assert.Equal(scenario == "failure" ? "RECEIPT_FAILED" : "SAMPLE_BASELINE_CHANGED", Assert.IsType<AppException>(error).Code);
        Assert.False(committed);
        Assert.False(Directory.Exists(Path.Combine(root, "pbip")));
        if (scenario == "baseline") Assert.Equal("保留外部修改", await File.ReadAllTextAsync(Path.Combine(root, "data", "external.txt")));
        else Assert.False(Directory.Exists(Path.Combine(root, "data")));
        AssertNoStage();
    }

    /// <summary>清单与实际内嵌资源逐字节对齐, 必要隐藏文件全部保留而本机状态排除.</summary>
    [Fact]
    public void ResourceManifestMatchesEveryEmbeddedByteAndIncludesHiddenFiles()
    {
        using var document = JsonDocument.Parse(SampleResources.Read("manifest.json"));
        Assert.Equal("197aaf4b6c3fac28990814851640c21796d49263", document.RootElement.GetProperty("commit").GetString());
        var entries = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(56, entries.Length);
        var names = entries.Select(entry => entry.GetProperty("path").GetString()!).Order(StringComparer.Ordinal).ToArray();
        var actual = typeof(SampleDataAdapter).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("PbiUtils.Sample/", StringComparison.Ordinal))
            .Select(name => name["PbiUtils.Sample/".Length..].Replace('\\', '/')).Where(name => name != "manifest.json").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(names, actual);
        foreach (var entry in entries)
        {
            var bytes = SampleResources.Read(entry.GetProperty("path").GetString()!);
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), bytes.LongLength);
            Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        Assert.Contains("pbip/demo.Report/.platform", names);
        Assert.Contains("pbip/demo.SemanticModel/.pbi/editorSettings.json", names);
        Assert.DoesNotContain(names, name => name.Contains("localSettings.json", StringComparison.Ordinal) || name.Contains("cache.abf", StringComparison.Ordinal));
        var dataset = SampleResources.Load();
        Assert.Equal(6, dataset.Regions.Count);
        Assert.Equal(34, dataset.Provinces.Count);
        Assert.True(dataset.Cities.Count > 300);
        Assert.True(dataset.Districts.Count > 2500);
    }

    /// <summary>全量生成十一 BOM CSV 和全部 PBIP 资源, 动态路径与日期正确且进度不早报完成.</summary>
    [Fact]
    public async Task FullGenerationMatchesLayoutStatisticsAndFinishStages()
    {
        var root = Root("完整示例");
        List<SampleDataProgress> progress = [];
        var response = await service.GenerateAsync(root, Options, progress: new InlineProgress(progress.Add));
        Assert.True(response.State.HasData);
        Assert.NotNull(response.State.LastFactDate);
        Assert.Equal(11, response.State.Tables.Count);
        Assert.Equal(Options.ProductCount, response.Result.Products);
        Assert.Equal(Options.StoreCount, response.Result.Stores);
        Assert.True(response.Result.Orders > 0);
        foreach (var table in response.State.Tables)
        {
            var path = Path.Combine(root, "data", table.Name + ".csv");
            Assert.Equal(new FileInfo(path).Length, table.Size);
            Assert.Equal(Utf8Bom, File.ReadAllBytes(path)[..3]);
            Assert.Equal(new SampleCsvStorage(Path.Combine(root, "data")).Read(table.Name + ".csv").LongCount(), table.Rows);
        }
        var expression = File.ReadAllText(Expression(root));
        Assert.Contains("expression Path = \"" + Path.Combine(root, "data") + "\"", expression);
        Assert.Contains("meta [IsParameterQuery=true", expression);
        Assert.Contains("\r\n", expression);
        Assert.Contains("date_start=#date(2021, 1, 1)", File.ReadAllText(Calendar(root)));
        Assert.Contains("date_end=#date(2025, 12, 31)", File.ReadAllText(Calendar(root)));
        Assert.Equal(51, Directory.EnumerateFiles(Path.Combine(root, "pbip"), "*", SearchOption.AllDirectories).Count());
        var project = await new ProjectService(new ProjectReader()).InspectAsync(Path.Combine(root, "pbip", "demo.pbip"), CancellationToken.None);
        Assert.Equal("TMDL", project.ModelFormat);
        Assert.Equal("PBIR", project.ReportFormat);
        Assert.NotEmpty(project.Model.Tables);
        Assert.NotEmpty(project.Model.Measures);
        Assert.True(project.Model.RelationshipCount > 0);
        Assert.Contains(project.Model.Tables, table => table.Name == "T00_产品表");
        Assert.Contains(project.Model.Tables, table => table.Name == "T04_订单主表");
        Assert.NotEmpty(project.Pages);
        Assert.True(project.Pages.Sum(page => page.Visuals.Count) > 0);
        Assert.Equal(30, progress.Single(value => value.Stage == "stageDimensions").Percent);
        Assert.Equal(55, progress.Single(value => value.Stage == "stageCustomers").Percent);
        Assert.All(progress.Take(progress.Count - 1), value => Assert.InRange(value.Percent, 0, 99));
        Assert.Equal(expected, progress.TakeLast(3).Select(value => value.Stage));
        Assert.Equal(99, progress.Single(value => value.Stage == "stagePbip").Percent);
        Assert.Equal(99, progress.Single(value => value.Stage == "stageSummary").Percent);
        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(progress.Select(value => value.Percent).Order(), progress.Select(value => value.Percent));
        AssertNoStage();
    }

    /// <summary>完成进度只在产物可读后发布, 观察者失败不能覆盖已经提交的业务结果.</summary>
    [Fact]
    public async Task CompletionProgressReflectsPublishedFilesAndObserverFailureKeepsSuccess()
    {
        var root = Root("单段进度");
        var committed = false;
        var completed = false;
        var result = await service.GenerateAsync(root, Options, beginCommit: () => committed = true,
            progress: new InlineProgress(value =>
            {
                if (value.Stage != "stageDone")
                {
                    Assert.False(committed);
                    Assert.True(value.Percent < 100);
                    return;
                }
                Assert.True(committed);
                Assert.Equal(100, value.Percent);
                Assert.True(File.Exists(Path.Combine(root, "pbip", "demo.pbip")));
                completed = true;
                throw new IOException("progress listener failed");
            }));
        Assert.True(completed);
        Assert.True(result.State.HasData);
        AssertNoStage();
    }

    /// <summary>上游 Locale 目前只改变界面, 两语言相同随机源的 CSV 内容保持一致.</summary>
    [Fact]
    public async Task LocaleKeepsCsvBusinessContentAndOverwritePreservesUnknownEntries()
    {
        var zh = Root("中文");
        var en = Root("English");
        await service.GenerateAsync(zh, Options);
        await service.GenerateAsync(en, Options with { Locale = "en-us" });
        foreach (var name in SampleTables.AllFiles) Assert.Equal(File.ReadAllBytes(Path.Combine(zh, "data", name)), File.ReadAllBytes(Path.Combine(en, "data", name)));
        File.WriteAllText(Path.Combine(zh, "data", "用户备注.txt"), "保留");
        File.WriteAllText(Path.Combine(zh, "pbip", "自定义资源.txt"), "保留");
        File.WriteAllText(Path.Combine(zh, "根文件.txt"), "保留");
        var error = await Assert.ThrowsAsync<AppException>(() => service.GenerateAsync(zh, Options));
        Assert.Equal("SAMPLE_OVERWRITE_REQUIRED", error.Code);
        await service.GenerateAsync(zh, Options, overwrite: true);
        Assert.Equal("保留", File.ReadAllText(Path.Combine(zh, "data", "用户备注.txt")));
        Assert.Equal("保留", File.ReadAllText(Path.Combine(zh, "pbip", "自定义资源.txt")));
        Assert.Equal("保留", File.ReadAllText(Path.Combine(zh, "根文件.txt")));
    }

    /// <summary>增量保留所有维度与非日历模板字节, 事实追加且序号唯一, 目标按全部数据重建.</summary>
    [Fact]
    public async Task IncrementPreservesDimensionsExtendsFactsAndExpandsCalendar()
    {
        var root = Root("增量");
        var original = await service.GenerateAsync(root, Options);
        var dimensions = SampleTables.AllFiles.Where(name => name is not (SampleTables.FileInventory or SampleTables.FileOrder or SampleTables.FileOrderItem or SampleTables.FileSaleTarget))
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, "data", name)));
        var expression = File.ReadAllBytes(Expression(root));
        var targets = new SampleCsvStorage(Path.Combine(root, "data")).Read(SampleTables.FileSaleTarget).ToDictionary(row => row[1] + ":" + row[3], row => row[4]);
        var start = DateOnly.ParseExact(original.State.LastFactDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1);
        var progress = new List<SampleDataProgress>();
        var response = await service.IncrementAsync(root, Options with { StartDate = Date(start), EndDate = Date(start.AddDays(400)) }, new InlineProgress(progress.Add));
        Assert.All(progress.Take(progress.Count - 1), value => Assert.InRange(value.Percent, 0, 99));
        Assert.Equal(100, Assert.Single(progress, value => value.Stage == "stageDone").Percent);
        Assert.Equal(expected, progress.TakeLast(3).Select(value => value.Stage));
        Assert.Equal(99, progress.Single(value => value.Stage == "stagePbip").Percent);
        Assert.Equal(99, progress.Single(value => value.Stage == "stageSummary").Percent);
        Assert.Equal(progress.Select(value => value.Percent).Order(), progress.Select(value => value.Percent));
        Assert.True(response.Result.Orders > 0);
        foreach (var dimension in dimensions) Assert.Equal(dimension.Value, File.ReadAllBytes(Path.Combine(root, "data", dimension.Key)));
        Assert.Equal(expression, File.ReadAllBytes(Expression(root)));
        foreach (var name in new[] { SampleTables.FileOrder, SampleTables.FileOrderItem, SampleTables.FileInventory })
        {
            var rows = new SampleCsvStorage(Path.Combine(root, "data")).Read(name).ToArray();
            Assert.Equal(rows.Length, rows.Select(row => row[0]).Distinct().Count());
        }
        var newTargets = new SampleCsvStorage(Path.Combine(root, "data")).Read(SampleTables.FileSaleTarget).ToDictionary(row => row[1] + ":" + row[3], row => row[4]);
        var unchangedBefore = new DateOnly(start.Year, start.Month, 1);
        foreach (var pair in targets.Where(pair => DateOnly.ParseExact(pair.Key[(pair.Key.IndexOf(':') + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture) < unchangedBefore))
            Assert.Equal(pair.Value, newTargets[pair.Key]);
        Assert.Contains("date_start=#date(2021, 1, 1)", File.ReadAllText(Calendar(root)));
        Assert.Contains("date_end=#date(" + start.AddDays(400).Year + ", 12, 31)", File.ReadAllText(Calendar(root)));
        Assert.True(response.State.Tables.Single(table => table.Name == "T04_订单主表").Rows > original.Result.Orders);
    }

    /// <summary>缺失目标表与 PBIP 的用户选择保持缺失, 重叠日期不产生任何修改.</summary>
    [Fact]
    public async Task IncrementSkipsRemovedTargetsAndTemplateAndRejectsDateConflicts()
    {
        var root = Root("缺失资源");
        var original = await service.GenerateAsync(root, Options);
        var before = File.ReadAllBytes(Path.Combine(root, "data", SampleTables.FileOrder));
        var conflict = await Assert.ThrowsAsync<AppException>(() => service.IncrementAsync(root, Options));
        Assert.Equal("SAMPLE_DATE_CONFLICT", conflict.Code);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "data", SampleTables.FileOrder)));
        File.Delete(Path.Combine(root, "data", SampleTables.FileSaleTarget));
        Directory.Delete(Path.Combine(root, "pbip"), true);
        var start = DateOnly.ParseExact(original.State.LastFactDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1);
        await service.IncrementAsync(root, Options with { StartDate = Date(start), EndDate = Date(start.AddDays(90)) });
        Assert.False(File.Exists(Path.Combine(root, "data", SampleTables.FileSaleTarget)));
        Assert.False(Directory.Exists(Path.Combine(root, "pbip")));
        AssertNoStage();
    }

    /// <summary>取消在准备阶段不产生最终数据, 开始提交后的取消必须返回实际成功.</summary>
    [Fact]
    public async Task CancellationCleansPreparationAndCannotUndoCommittedSuccess()
    {
        var root = Root("取消");
        using var cancellation = new CancellationTokenSource();
        var progress = new List<SampleDataProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(root, Options,
            progress: new InlineProgress(value => { progress.Add(value); if (value.Stage == "stageSummary") cancellation.Cancel(); }), cancellationToken: cancellation.Token));
        Assert.DoesNotContain(progress, value => value.Percent == 100 || value.Stage == "stageDone");
        Assert.False(Directory.Exists(root));
        AssertNoStage();
        using var afterCommit = new CancellationTokenSource();
        var result = await service.GenerateAsync(root, Options, cancellationToken: afterCommit.Token, beginCommit: afterCommit.Cancel);
        Assert.True(result.State.HasData);
        Assert.True(File.Exists(Path.Combine(root, "pbip", "demo.pbip")));
        AssertNoStage();
    }

    /// <summary>生成期间的用户额外写入造成基线冲突, 隔离结果不能覆盖外部内容.</summary>
    [Fact]
    public async Task ExternalChangeDuringPreparationRejectsPublication()
    {
        var root = Root("外部修改");
        await service.GenerateAsync(root, Options);
        var before = File.ReadAllBytes(Path.Combine(root, "data", SampleTables.FileOrder));
        var progress = new List<SampleDataProgress>();
        var error = await Assert.ThrowsAsync<AppException>(() => service.GenerateAsync(root, Options, overwrite: true,
            progress: new InlineProgress(value => { progress.Add(value); if (value.Stage == "stageSummary") File.WriteAllText(Path.Combine(root, "data", "外部文件.txt"), "外部内容"); })));
        Assert.DoesNotContain(progress, value => value.Percent == 100 || value.Stage == "stageDone");
        Assert.Equal("SAMPLE_BASELINE_CHANGED", error.Code);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "data", SampleTables.FileOrder)));
        Assert.Equal("外部内容", File.ReadAllText(Path.Combine(root, "data", "外部文件.txt")));
        AssertNoStage();
    }

    /// <summary>清空删除固定十一表及整个已确认 PBIP, 保留根目录与未知 data 条目.</summary>
    [Fact]
    public async Task ClearRetainsRootAndUnknownDataAndHandlesMissingDirectory()
    {
        var root = Root("清空");
        await service.GenerateAsync(root, Options);
        File.WriteAllText(Path.Combine(root, "data", "附加.csv"), "用户数据");
        File.WriteAllText(Path.Combine(root, "根文件.txt"), "根数据");
        var state = await service.ClearAsync(root);
        Assert.False(state.HasData);
        Assert.Empty(state.Tables);
        Assert.True(Directory.Exists(root));
        Assert.False(Directory.Exists(Path.Combine(root, "pbip")));
        Assert.Equal("用户数据", File.ReadAllText(Path.Combine(root, "data", "附加.csv")));
        Assert.Equal("根数据", File.ReadAllText(Path.Combine(root, "根文件.txt")));
        var missing = await service.ClearAsync(Root("不存在"));
        Assert.False(missing.HasData);
        Assert.False(Directory.Exists(missing.OutputPath));
        AssertNoStage();
    }

    /// <summary>无基础数据增量明确失败, 损坏统计不能空成功而全量可在确认后修复.</summary>
    [Fact]
    public async Task CorruptCsvSelectionRemainsRecoverableAndMissingBaseFails()
    {
        var root = Root("损坏");
        var missing = await Assert.ThrowsAsync<AppException>(() => service.IncrementAsync(root, Options));
        Assert.Equal("SAMPLE_NO_BASE_DATA", missing.Code);
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "data", SampleTables.FileProduct), "错误表头\n乱码");
        Assert.True((await service.GetSelectionAsync(root)).HasData);
        var error = await Assert.ThrowsAsync<AppException>(() => service.GetAsync(root));
        Assert.Equal("SAMPLE_INVALID_CSV", error.Code);
        var repaired = await service.GenerateAsync(root, Options, overwrite: true);
        Assert.True(repaired.State.HasData);
    }

    /// <summary>仅 PBIP 或仅一张 CSV 也必须可管理和确认覆盖, 增量仍要求四张基础表.</summary>
    [Fact]
    public async Task PartialCsvAndPbipOnlyAreVisibleForClearAndOverwriteConfirmation()
    {
        var root = Root("仅PBIP");
        Directory.CreateDirectory(Path.Combine(root, "pbip"));
        File.WriteAllText(Path.Combine(root, "pbip", "用户模板.txt"), "模板");
        Assert.True((await service.GetAsync(root)).HasData);
        Assert.True((await service.GetSelectionAsync(root)).HasData);
        var overwrite = await Assert.ThrowsAsync<AppException>(() => service.GenerateAsync(root, Options));
        Assert.Equal("SAMPLE_OVERWRITE_REQUIRED", overwrite.Code);
        var noBase = await Assert.ThrowsAsync<AppException>(() => service.IncrementAsync(root, Options));
        Assert.Equal("SAMPLE_NO_BASE_DATA", noBase.Code);
        Assert.False((await service.ClearAsync(root)).HasData);
        using (var writer = new SampleCsvStorage(Path.Combine(root, "data")).Create(SampleTables.FileProduct, SampleTables.HeaderProduct))
            writer.Write(["1", "SKU_1", "分类", "产品", "10", "5"]);
        Assert.True((await service.GetAsync(root)).HasData);
        Assert.False((await service.ClearAsync(root)).HasData);
        Assert.False(Directory.Exists(Path.Combine(root, "data")));
    }

    /// <summary>第二目录被占用时恢复已经替换的第一目录, 不残留半份生成结果.</summary>
    [Fact]
    public async Task PublishFailureRestoresPreviouslyMovedDataDirectory()
    {
        var root = Root("发布恢复");
        await service.GenerateAsync(root, Options);
        var before = File.ReadAllBytes(Path.Combine(root, "data", SampleTables.FileProduct));
        FileStream? locked = null;
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => service.GenerateAsync(root, Options with { ProductCount = 9 }, overwrite: true,
                beginCommit: () => locked = new FileStream(Expression(root), FileMode.Open, FileAccess.Read, FileShare.None)));
            Assert.Equal("SAMPLE_IO_FAILED", error.Code);
        }
        finally { locked?.Dispose(); }
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "data", SampleTables.FileProduct)));
        Assert.True((await service.GetAsync(root)).HasData);
        AssertNoStage();
    }

    /// <summary>非法 UTF-8, 不闭合引号与超长记录明确失败而不返回零行成功.</summary>
    [Theory]
    [InlineData("encoding")]
    [InlineData("quote")]
    [InlineData("length")]
    public async Task MalformedCsvFailsWithStableSafeError(string mode)
    {
        var root = Root("畸形-" + mode);
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var path = Path.Combine(root, "data", SampleTables.FileProduct);
        if (mode == "encoding") File.WriteAllBytes(path, [0xFF, 0xFE, 0x00]);
        else File.WriteAllText(path, string.Join(',', SampleTables.HeaderProduct) + "\n" +
            (mode == "quote" ? "1,\"未闭合" : "1,SKU_1,分类," + new string('A', 70 * 1024) + ",10,5\n"), new UTF8Encoding(true));
        var error = await Assert.ThrowsAsync<AppException>(() => service.GetAsync(root));
        Assert.Equal("SAMPLE_INVALID_CSV", error.Code);
    }

    /// <summary>根目录, 相对路径, 设备路径和保留设备段不能成为产物目标.</summary>
    [Theory]
    [InlineData("relative/path")]
    [InlineData("C:\\")]
    [InlineData("C:\\Temp\\CON.txt")]
    [InlineData("\\\\?\\C:\\Temp\\sample")]
    public async Task InvalidOutputIsRejectedBeforeMutation(string path)
    {
        var error = await Assert.ThrowsAsync<AppException>(() => service.GenerateAsync(path, Options));
        Assert.Equal("SAMPLE_INVALID_OUTPUT", error.Code);
    }

    /// <summary>CSV 专业解析支持引号, 逗号与跨行值, 追加不重复写 BOM/表头.</summary>
    [Fact]
    public void CsvEscapingRoundTripsAndAppendPreservesCurrentFormat()
    {
        var data = Root("CSV");
        var storage = new SampleCsvStorage(data);
        string[] row = ["1", "SKU_1", "逗号,分类", "含\"引号\"\n产品", "100", "80"];
        using (var writer = storage.Create(SampleTables.FileProduct, SampleTables.HeaderProduct)) writer.Write(row);
        Assert.Equal(row, Assert.Single(storage.Read(SampleTables.FileProduct)));
        using (var writer = storage.Append(SampleTables.FileProduct)) writer.Write(row);
        Assert.Equal(2, storage.Read(SampleTables.FileProduct).Count());
        using (var writer = storage.Create(SampleTables.FileOrder, SampleTables.HeaderOrder)) writer.Write(["1", "OC_0000001", "MD_1", "2024-01-01", "2024-01-10", "KH_1", "线上"]);
        using (var writer = storage.Append(SampleTables.FileOrder)) writer.Write(["1", "OC_0000002", "MD_1", "2024-02-01", "2024-02-10", "KH_1", "线上"]);
        Assert.Equal(2, storage.Read(SampleTables.FileOrder).Count());
        Assert.All(storage.Read(SampleTables.FileOrder), value => Assert.Equal(SampleTables.HeaderOrder.Count, value.Length));
    }

    /// <summary>三张事实表缺自动编号的表头不能追加, 当前表头下缺列或多列的行也不得读取.</summary>
    [Theory]
    [InlineData(SampleTables.FileInventory)]
    [InlineData(SampleTables.FileOrder)]
    [InlineData(SampleTables.FileOrderItem)]
    public void CsvRequiresCompleteCurrentFactHeaderAndRowWidth(string file)
    {
        var data = Root("事实格式"); Directory.CreateDirectory(data);
        var storage = new SampleCsvStorage(data);
        var path = Path.Combine(data, file);
        var header = SampleTables.Header(file);
        var oldBytes = new UTF8Encoding(true).GetBytes(string.Join(',', header.Skip(1)) + "\n");
        File.WriteAllBytes(path, oldBytes);
        Assert.Equal("SAMPLE_INVALID_CSV", Assert.Throws<AppException>(() => storage.Read(file).ToArray()).Code);
        Assert.Equal("SAMPLE_INVALID_CSV", Assert.Throws<AppException>(() => storage.Append(file)).Code);
        Assert.Equal(oldBytes, File.ReadAllBytes(path));
        foreach (var count in new[] { header.Count - 1, header.Count + 1 })
        {
            File.WriteAllText(path, string.Join(',', header) + "\n" + string.Join(',', Enumerable.Repeat("1", count)) + "\n", new UTF8Encoding(true));
            Assert.Equal("SAMPLE_INVALID_CSV", Assert.Throws<AppException>(() => storage.Read(file).ToArray()).Code);
        }
    }

    /// <summary>日历只扩张不回缩, 缺失资源为跳过, 带 # 的目录按 M 字面字符串正确释放.</summary>
    [Fact]
    public void TemplateCalendarOnlyExpandsAndEscapesMPath()
    {
        var root = Root("模板#(路径)");
        SampleResources.Install(root, Path.Combine(root, "data"), Options, CancellationToken.None);
        var calendar = Path.Combine(root, SampleResources.CalendarPath.Replace('/', Path.DirectorySeparatorChar));
        SampleResources.ExpandCalendar(root, Options with { StartDate = "2019-01-01", EndDate = "2027-12-31" });
        var content = File.ReadAllBytes(calendar);
        Assert.Contains("date_start=#date(2019, 1, 1)", Encoding.UTF8.GetString(content));
        Assert.Contains("date_end=#date(2027, 12, 31)", Encoding.UTF8.GetString(content));
        SampleResources.ExpandCalendar(root, Options);
        Assert.Equal(content, File.ReadAllBytes(calendar));
        var expression = File.ReadAllText(Path.Combine(root, "demo.SemanticModel", "definition", "expressions.tmdl"));
        Assert.Contains("模板#(#)(路径)", expression);
    }

    /// <summary>缺少当前日历时保持缺失, 不按其他文件名猜测或修改未知模型文件.</summary>
    [Fact]
    public void MissingCurrentCalendarDoesNotModifyOtherModelFiles()
    {
        var root = Root("旧名日历");
        SampleResources.Install(root, Path.Combine(root, "data"), Options, CancellationToken.None);
        var current = Path.Combine(root, SampleResources.CalendarPath.Replace('/', Path.DirectorySeparatorChar));
        var legacy = Path.Combine(Path.GetDirectoryName(current)!, "01_Calendar.tmdl");
        File.Move(current, legacy);
        var original = File.ReadAllBytes(legacy);
        SampleResources.ExpandCalendar(root, Options with { StartDate = "2019-01-01", EndDate = "2027-12-31" });
        Assert.Equal(original, File.ReadAllBytes(legacy));
        Assert.False(File.Exists(current));
    }

    /// <summary>创建各测试独立根路径, 产物仅位于自己的临时工作区.</summary>
    private string Root(string name)
    {
        Directory.CreateDirectory(workspace);
        return Path.Combine(workspace, name);
    }

    /// <summary>按固定文化返回上游日期文本.</summary>
    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    /// <summary>返回生成目录内固定日历路径.</summary>
    private static string Calendar(string root) => Path.Combine(root, "pbip", SampleResources.CalendarPath.Replace('/', Path.DirectorySeparatorChar));
    /// <summary>返回生成目录内固定参数路径.</summary>
    private static string Expression(string root) => Path.Combine(root, "pbip", "demo.SemanticModel", "definition", "expressions.tmdl");
    /// <summary>断言成功或失败都没有残留隔离发布目录.</summary>
    private void AssertNoStage() => Assert.Empty(Directory.EnumerateDirectories(workspace, ".pbi-utils-sample-*"));
    /// <summary>同步进度回调避免异步派发掩盖取消与发布竞争.</summary>
    private sealed class InlineProgress(Action<SampleDataProgress> report) : IProgress<SampleDataProgress>
    {
        /// <summary>在当前后台调用线程上立即派发阶段.</summary>
        public void Report(SampleDataProgress value) => report(value);
    }

    /// <summary>只清理本测试创建的随机临时工作区.</summary>
    public void Dispose()
    {
        if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
    }
}
