// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableBulkExportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证多表导出完整工作簿或全新CSV目录, 保留逐表身份和整轮失败保护.

using PbiUtils.Infrastructure.Models.Export;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelTableBulkExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-bulk-export-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>只创建本组测试自己的临时父目录.</summary>
    public ModelTableBulkExportTests() => Directory.CreateDirectory(root);

    /// <summary>选择多表时仅发布一个全新目录, 每表CSV保留实际标题和值, 不添加ZIP或覆盖父目录文件.</summary>
    [Fact]
    public async Task MultipleCsvTablesPublishOneNewDirectory()
    {
        var source = new Source(["First", "Second"]);
        var destination = Path.Combine(root, "published");
        var result = await Export(source, Request(["First", "Second"], "csv"), destination);
        Assert.Equal(4, result.RowCount);
        Assert.Equal("published", result.FileName);
        Assert.Equal([new ModelTableExportItem("First", 2), new ModelTableExportItem("Second", 2)], result.Tables);
        Assert.Equal(["First", "Second"], source.ReadTables);
        Assert.Equal(["First.csv", "Second.csv"], Directory.GetFiles(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("Value\r\nFirst\r\nFirst\r\n", await File.ReadAllTextAsync(Path.Combine(destination, "First.csv")));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    /// <summary>全部表位于同一工作簿, 包关系引用每张实际表, 可选摘要在末页记录所有表及行数.</summary>
    [Fact]
    public async Task WorkbookContainsOneSheetPerSelectedTableAndSummary()
    {
        var source = new Source(["First", "Second"]);
        var destination = Path.Combine(root, "all.xlsx");
        var result = await Export(source, Request(["Second", "First"], "xlsx", true), destination);
        Assert.Equal(4, result.RowCount);
        Assert.Equal([new ModelTableExportItem("Second", 2), new ModelTableExportItem("First", 2)], result.Tables);
        Assert.Equal(["Second", "First"], source.ReadTables);
        using var zip = ZipFile.OpenRead(destination);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var stream = zip.GetEntry("xl/workbook.xml")!.Open();
        var workbook = XDocument.Load(stream);
        Assert.Equal(["Second", "First", "导出日志"], workbook.Descendants(ns + "sheet").Select(sheet => sheet.Attribute("name")!.Value));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
        Assert.Single(Directory.GetFiles(root));
    }

    /// <summary>第二表读取失败、取消或可见基线变化都拒绝整轮, 不留下部分工作簿或CSV目标目录.</summary>
    /// <param name="format">工作簿文件或多CSV目录格式.</param>
    /// <param name="failure">第二表发生的受控失败类型.</param>
    [Theory]
    [InlineData("csv", "read")]
    [InlineData("xlsx", "read")]
    [InlineData("csv", "cancel")]
    [InlineData("xlsx", "cancel")]
    [InlineData("csv", "baseline")]
    [InlineData("xlsx", "baseline")]
    public async Task LaterTableFailureNeverPublishesPartialSelection(string format, string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source(["First", "Second"])
        {
            BeforeTable = (source, table) =>
            {
                if (table != "Second") return;
                if (failure == "cancel") cancellation.Cancel();
                else if (failure == "baseline") source.Snapshot = source.Snapshot with { BaselineId = "changed" };
                else throw new AppException("EXPORT_QUERY_FAILED", "query failed", 4);
            }
        };
        var destination = Path.Combine(root, format == "csv" ? "published" : "all.xlsx");
        await Assert.ThrowsAnyAsync<Exception>(() => Export(source, Request(["First", "Second"], format), destination, token: cancellation.Token));
        Assert.Equal(["First", "Second"], source.ReadTables);
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    /// <summary>第二表超出Excel单元格上限时, 第一表也不得提前覆盖已有工作簿.</summary>
    [Fact]
    public async Task ExcelLaterSheetLimitPreservesOriginalWorkbook()
    {
        var source = new Source(["First", "Second"]) { Value = table => table == "Second" ? new string('x', 32768) : table };
        var destination = Path.Combine(root, "all.xlsx");
        await File.WriteAllTextAsync(destination, "original");
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First", "Second"], "xlsx"), destination));
        Assert.Equal("EXPORT_EXCEL_LIMIT", error.Code);
        Assert.Equal(["First", "Second"], source.ReadTables);
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    /// <summary>批量CSV目标若预先存在或发布前被抢占, 不改动该目标的任何内容.</summary>
    /// <param name="concurrent">是否在最终提交裁决窗口注入新目标.</param>
    /// <param name="occupiedByFile">竞争目标是文件还是目录.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CsvDirectoryNeverOverwritesExistingOrConcurrentDestination(bool concurrent, bool occupiedByFile)
    {
        var source = new Source(["First", "Second"]);
        var destination = Path.Combine(root, "published");
        void Occupy()
        {
            if (occupiedByFile) File.WriteAllText(destination, "external");
            else { Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "external.txt"), "external"); }
        }
        if (!concurrent) Occupy();
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First", "Second"], "csv"), destination, commit: concurrent ? Occupy : null));
        Assert.Equal("EXPORT_DESTINATION_CHANGED", error.Code);
        Assert.Equal("external", await File.ReadAllTextAsync(occupiedByFile ? destination : Path.Combine(destination, "external.txt")));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    /// <summary>空选择、重复选择或超过预算的选择在读取数据和建立目标之前整批拒绝.</summary>
    /// <param name="invalid">被拒绝的精确选择类型.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("many")]
    public async Task InvalidSelectionDoesNotCreateAnyOutput(string invalid)
    {
        var source = new Source(["First", "Second"]);
        var tables = invalid == "empty" ? Array.Empty<string>() : invalid == "duplicate" ? ["First", "First"] : [.. Enumerable.Range(0, 2001).Select(index => "T" + index)];
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(tables, "csv"), Path.Combine(root, "published")));
        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.Empty(source.ReadTables);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    /// <summary>先校验所有已选表, 不先把有效表写出后才发现后续选择已经无效.</summary>
    [Fact]
    public async Task MissingLaterTableIsRejectedBeforeFirstQuery()
    {
        var source = new Source(["First"]);
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First", "Missing"], "csv"), Path.Combine(root, "published")));
        Assert.Equal("EXPORT_TABLE_UNAVAILABLE", error.Code);
        Assert.Empty(source.ReadTables);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    /// <summary>外部程序若向私有暂存插入未知项, 不发布也不递归删除该项, 并保留安全清理失败事实.</summary>
    [Fact]
    public async Task UnexpectedStagingEntryIsNotDeletedOrPublished()
    {
        string? external = null;
        var source = new Source(["First", "Second"])
        {
            BeforeTable = (_, table) =>
            {
                if (table != "Second") return;
                var temporary = Directory.GetDirectories(root).Single();
                external = Path.Combine(temporary, "external.txt");
                File.WriteAllText(external, "external");
            }
        };
        var destination = Path.Combine(root, "published");
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First", "Second"], "csv"), destination));
        Assert.Equal("EXPORT_FILE_FAILED", error.Code);
        Assert.Contains("清理失败", ModelTableExportService.DecorateCleanupFailure(error, "safe"));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("external", await File.ReadAllTextAsync(external!));
    }

    /// <summary>路径分隔、Windows设备名及大小写碰撞不会逃逸CSV目录, Excel工作表名称同样合法且唯一.</summary>
    /// <param name="format">需要验证输出名称的容器格式.</param>
    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task OutputNamesAreSafeAndCaseInsensitivelyUnique(string format)
    {
        string[] tables = ["../CON", "CON", "a/b", "a\\b", "A_B", "导出日志", "'[]:*?/\\'", new string('界', 40), new string('x', 30) + "'tail", string.Concat(Enumerable.Repeat("🙂", 20))];
        var source = new Source(tables);
        var destination = Path.Combine(root, format == "csv" ? "published" : "all.xlsx");
        await Export(source, Request(tables, format, format == "xlsx"), destination);
        if (format == "csv")
        {
            var files = Directory.GetFiles(destination);
            Assert.Equal(tables.Length, files.Length);
            Assert.Equal(tables.Length, files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(files, path => { Assert.Equal(destination, Path.GetDirectoryName(path)); Assert.EndsWith(".csv", path); Assert.DoesNotContain(':', Path.GetFileName(path)); });
            Assert.DoesNotContain(files, path => Path.GetFileName(path).Equals("CON.csv", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            using var zip = ZipFile.OpenRead(destination);
            using var stream = zip.GetEntry("xl/workbook.xml")!.Open();
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var names = XDocument.Load(stream).Descendants(ns + "sheet").Select(sheet => sheet.Attribute("name")!.Value).ToArray();
            Assert.Equal(tables.Length + 1, names.Length);
            Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(names, name => { Assert.InRange(name.Length, 1, 31); Assert.DoesNotContain(name, character => "[]:*?/\\".Contains(character)); Assert.False(name.StartsWith('\'') || name.EndsWith('\'')); });
        }
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    /// <summary>从真实JSON请求形状建立选择, 该辅助器不授予模型或文件权限.</summary>
    private static ModelTableExportRequest Request(string[] tables, string format, bool includeLog = false) => JsonSerializer.Deserialize<ModelTableExportRequest>(
        JsonSerializer.Serialize(new { projectId = "project", baselineId = "baseline", tables, format, includeLog, locale = "zh-cn" }), Json)!;
    /// <summary>数据仍由生产共享服务消费, 输出始终通过真实文件适配写入.</summary>
    private static Task<ModelTableExportResult> Export(Source source, ModelTableExportRequest request, string destination, Action? commit = null, CancellationToken token = default)
        => new ModelTableExportService(source, source, new ModelTableFileWriter()).ExportAsync(source.Snapshot.ProjectPath, request, destination, token, commit);

    /// <summary>只替代外部只读引擎, 按请求表名产生两条重复记录并保留逐表读取顺序.</summary>
    private sealed class Source(string[] tables) : IProjectReader, IModelTableDataReader
    {
        public ProjectSnapshot Snapshot { get; set; } = new("model://bulk", "project", "baseline", "TOM", "", "1", null,
            new("model", 1606, "en-US", tables.Select(name => new TableInfo(name, null, false, [new("Value", "Data", "String", false, null)], [])).ToArray(), [], [], 0, 0), [], [], [], new("desktop", "Desktop"));
        public List<string> ReadTables { get; } = [];
        public Action<Source, string>? BeforeTable { get; init; }
        public Func<string, object?>? Value { get; init; }

        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot); }
        public Task<long> ReadAsync(ProjectSnapshot snapshot, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
        {
            ReadTables.Add(table); BeforeTable?.Invoke(this, table); token.ThrowIfCancellationRequested();
            return Task.FromResult(consume(new Rows(Value?.Invoke(table) ?? table), token));
        }
    }
    /// <summary>确定性重复行游标, 用于证明批量写入没有去重或全表合并.</summary>
    private sealed class Rows(object? value) : IModelTableRows
    {
        private int index;
        public IReadOnlyList<string> Columns => ["Value"];
        public bool Read() => ++index <= 2;
        public object? GetValue(int ordinal) => value;
    }
    /// <summary>仅清理测试创建的唯一临时目录及其测试产物.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
