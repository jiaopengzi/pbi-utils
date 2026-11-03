// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableSamplingExportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模型侧抽样意图、严格行数上限、重复/空表及摘要和失败保护.

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

public sealed class ModelTableSamplingExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-sampling-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly XNamespace XmlNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>只创建本测试独立的输出父目录.</summary>
    public ModelTableSamplingExportTests() => Directory.CreateDirectory(root);

    /// <summary>非法范围在任何文件或数据读取前拒绝, 不交给DAX隐式截断或转为全量.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidLimitNeverCreatesOutputOrQueriesRows(int limit)
    {
        var source = new Source([("First", 2)]);
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First"], "csv", limit), "invalid.csv"));
        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.Equal(0, source.ReadCalls);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    /// <summary>请求范围与实际行数分别保留, N大于表时保留全部重复行, 空表只保留标题.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 2)]
    [InlineData(int.MaxValue, 2)]
    [InlineData(100, 0)]
    [InlineData(null, 2)]
    public async Task ActualCountsRemainIndependentFromRequestedLimit(int? limit, int actual)
    {
        var source = new Source([("First", actual)]);
        var result = await Export(source, Request(["First"], "csv", limit), "sample.csv");
        Assert.Equal(actual, result.RowCount);
        Assert.Equal(actual, Assert.Single(result.Tables).RowCount);
        Assert.Equal([limit], source.RequestedLimits);
        var json = JsonSerializer.SerializeToElement(result, Json);
        Assert.True(json.TryGetProperty("rowLimit", out var reported));
        Assert.Equal(limit, reported.ValueKind == JsonValueKind.Null ? null : (int?)reported.GetInt32());
        Assert.Equal("Value\r\n" + string.Concat(Enumerable.Repeat("duplicate\r\n", actual)), await File.ReadAllTextAsync(Path.Combine(root, "sample.csv")));
    }

    /// <summary>源返回N+1属于查询边界异常, 最多多读一行即失败, 不裁剪成成功或覆盖原文件.</summary>
    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task UnexpectedExtraRowFailsAndPreservesExistingFile(string format)
    {
        var source = new Source([("First", 100)]);
        var destination = "retained." + format;
        await File.WriteAllTextAsync(Path.Combine(root, destination), "retained original");
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First"], format, 2), destination));
        Assert.Equal("EXPORT_ROW_LIMIT_EXCEEDED", error.Code);
        Assert.Equal(3, source.ReadCalls);
        Assert.Equal("retained original", await File.ReadAllTextAsync(Path.Combine(root, destination)));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    /// <summary>第二表异常超量使整轮失败, 第一表不能提前发布工作簿或CSV子目录.</summary>
    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task LaterTableExceedingLimitNeverPublishesPartialBatch(string format)
    {
        var source = new Source([("First", 2), ("Second", 3)]);
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First", "Second"], format, 2), format == "csv" ? "batch" : "batch.xlsx"));
        Assert.Equal("EXPORT_ROW_LIMIT_EXCEEDED", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    /// <summary>多表共享同一每表上限, 选中的重复行原样写入, 不把总批次数量误作单表上限.</summary>
    [Fact]
    public async Task EachTableReceivesItsOwnSameLimitAndPreservesDuplicates()
    {
        var source = new Source([("First", 2), ("Second", 2)]);
        var result = await Export(source, Request(["First", "Second"], "csv", 2), "batch");
        Assert.Equal([2, 2], source.RequestedLimits);
        Assert.Equal(4, result.RowCount);
        Assert.All(Directory.GetFiles(Path.Combine(root, "batch")), file => Assert.Equal("Value\r\nduplicate\r\nduplicate\r\n", File.ReadAllText(file)));
    }

    /// <summary>源同步Read期间发生取消时保留取消首因, 不改报N+1边界错误或发布部分文件.</summary>
    [Fact]
    public async Task CancellationDuringExtraReadRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source([("First", 3)]) { AfterRead = number => { if (number == 3) cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Export(source, Request(["First"], "csv", 2), "cancel.csv", cancellation.Token));
        Assert.Equal(3, source.ReadCalls);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    /// <summary>抽样查询与真实 API long 输出一致, 先抽原表再投影, 不排序/去重且保留特殊名称.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void SamplingQueriesLimitAtSourceBeforeProjection(int limit)
    {
        var table = new TableInfo("a'b", null, false, [new("x]y\"", "Data", "String", false, null), new("Row", "RowNumber", "Int64", true, null)], []);
        var number = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("EVALUATE\nSELECTCOLUMNS ( SAMPLE ( " + number + ", 'a''b', 0 ), \"x]y\"\"\", 'a''b'[x]]y\"] )", ModelTableExportQuery.Create(table, limit));
        Assert.Equal("EVALUATE\nSAMPLE ( " + number + ", 'a''b', 0 )", ModelTableExportQuery.Create(table.Name, limit));
    }

    /// <summary>非法查询上限拒绝于SDK执行前, 不依靠DAX对0/负数的空表语义掩盖参数错误.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QueryBuildersRejectInvalidLimits(int limit)
        => Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => ModelTableExportQuery.Create("First", limit)).Code);

    /// <summary>抽样不改变Excel单元格限制, 即使N很大也不能覆盖原文件或截断文本.</summary>
    [Fact]
    public async Task SampledLongTextStillFailsWithoutReplacingTarget()
    {
        var source = new Source([("First", 1)]) { Value = new string('x', 32768) };
        await File.WriteAllTextAsync(Path.Combine(root, "retained.xlsx"), "retained original");
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, Request(["First"], "xlsx", int.MaxValue), "retained.xlsx"));
        Assert.Equal("EXPORT_EXCEL_LIMIT", error.Code);
        Assert.Equal("retained original", await File.ReadAllTextAsync(Path.Combine(root, "retained.xlsx")));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    /// <summary>摘要分别显示实际行数、请求模式与每表上限, 全量上限留空而非冒充0行.</summary>
    [Theory]
    [InlineData("zh-cn", 100)]
    [InlineData("en-us", 100)]
    [InlineData("zh-cn", null)]
    [InlineData("en-us", null)]
    public async Task SummaryDistinguishesAllRowsAndLimitedSelection(string locale, int? limit)
    {
        var source = new Source([("First", 2)]);
        await Export(source, Request(["First"], "xlsx", limit, true, locale), "summary.xlsx");
        using var zip = ZipFile.OpenRead(Path.Combine(root, "summary.xlsx"));
        using var stream = zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();
        var rows = XDocument.Load(stream).Descendants(XmlNamespace + "row").ToArray();
        var headers = rows[0].Elements(XmlNamespace + "c").Select(Text).ToArray();
        Assert.Equal(locale == "en-us" ? ["Table", "Worksheet", "Exported rows", "Columns", "Export mode", "Row limit per table"]
            : ["表", "工作表", "导出行数", "列数", "导出范围", "每表行数上限"], headers);
        var data = rows[1].Elements(XmlNamespace + "c").ToArray();
        Assert.Equal("2", data[2].Element(XmlNamespace + "v")!.Value);
        Assert.Equal(locale == "en-us" ? limit.HasValue ? "Sample" : "All rows" : limit.HasValue ? "抽样" : "全部行", Text(data[4]));
        Assert.Equal(limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", Text(data[5]));
        Assert.Empty(rows.SelectMany(row => row.Descendants(XmlNamespace + "f")));
    }

    /// <summary>读取摘要文字或原始数值, 不用产品解析器核验导出格式.</summary>
    private static string Text(XElement cell) => cell.Element(XmlNamespace + "v")?.Value ?? string.Concat(cell.Descendants(XmlNamespace + "t").Select(value => value.Value));

    /// <summary>真实JSON形状进入当前请求契约, 红灯阶段可证明旧实现忽略rowLimit.</summary>
    private static ModelTableExportRequest Request(string[] tables, string format, int? rowLimit, bool includeLog = false, string locale = "zh-cn")
        => JsonSerializer.Deserialize<ModelTableExportRequest>(JsonSerializer.Serialize(new { projectId = "project", baselineId = "baseline", tables, format, includeLog, locale, rowLimit }), Json)!;

    /// <summary>使用真实共享编排及文件writer, 只替代外部引擎.</summary>
    private Task<ModelTableExportResult> Export(Source source, ModelTableExportRequest request, string name, CancellationToken token = default)
        => new ModelTableExportService(source, source, new ModelTableFileWriter()).ExportAsync(source.Snapshot.ProjectPath, request, Path.Combine(root, name), token);

    /// <summary>外部引擎返回受控数量的重复行, 不在夹具中做全量截断以掩盖产品守卫缺失.</summary>
    private sealed class Source((string Name, int Rows)[] tables) : IProjectReader, IModelTableDataReader
    {
        public int ReadCalls { get; private set; }
        public List<int?> RequestedLimits { get; } = [];
        public Action<int>? AfterRead { get; init; }
        public string Value { get; init; } = "duplicate";
        public ProjectSnapshot Snapshot { get; } = new("model://sampling", "project", "baseline", "TOM", "", "", null,
            new("model", 1606, "en-US", tables.Select(table => new TableInfo(table.Name, null, false, [new("Value", "Data", "String", false, null)], [])).ToArray(), [], [], 0, 0), [], [], [], new("desktop", "Desktop"));
        /// <summary>返回当前固定结构, 不访问文件或真实模型.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) => Task.FromResult(Snapshot);
        /// <summary>仅暴露模拟引擎本来返回的原始行流, 每次Read记数以检测额外全量消费.</summary>
        public Task<long> ReadAsync(ProjectSnapshot snapshot, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
        {
            RequestedLimits.Add(rowLimit);
            return Task.FromResult(consume(new Rows(tables.Single(item => item.Name == table).Rows, Value, () => { ReadCalls++; AfterRead?.Invoke(ReadCalls); }), token));
        }
    }

    /// <summary>重复标量游标保留实际出现次数, 不去重或按请求行数裁剪.</summary>
    private sealed class Rows(int count, string value, Action read) : IModelTableRows
    {
        private int index;
        public IReadOnlyList<string> Columns => ["Value"];
        public bool Read() { read(); return ++index <= count; }
        public object? GetValue(int ordinal) => value;
    }

    /// <summary>仅删除本测试自行创建的唯一目录和产物.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
