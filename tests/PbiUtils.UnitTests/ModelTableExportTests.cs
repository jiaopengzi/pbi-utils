// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelTableExportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证单表导出格式、权威基线、取消和受锁文件发布边界.

using PbiUtils.Infrastructure.Models.Export;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class ModelTableExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-export-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Columns = ["Text", "Number", "Date", "Flag", "Empty"];
    /// <summary>创建本用例独立的系统临时目录, 不触及模型或用户输出.</summary>
    public ModelTableExportTests() => Directory.CreateDirectory(root);

    /// <summary>CSV 流式保留标题、空值、数字及引号换行, 使用 Excel 可识别的 UTF-8 BOM.</summary>
    [Fact]
    public async Task CsvPreservesRowsAndEscapesText()
    {
        var source = new Source([["a,\"b\"\r\n中", 123.45m, new DateTime(2026, 10, 25, 12, 3, 4), true, null]]);
        var result = await Export(source, "csv");
        var bytes = File.ReadAllBytes(Destination("csv"));
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal("Text,Number,Date,Flag,Empty\r\n\"a,\"\"b\"\"\r\n中\",123.45,2026-10-25T12:03:04.0000000,TRUE,\r\n", File.ReadAllText(Destination("csv")));
        Assert.Equal(1, result.RowCount);
        Assert.Equal("table.csv", result.FileName);
        Assert.True(source.Disposed);
    }

    /// <summary>Excel 输出标准 ZIP/XML, 字符串不执行公式, 数值、日期、布尔及空值保留类型.</summary>
    [Fact]
    public async Task WorkbookContainsTypedCellsAndOptionalSummary()
    {
        var source = new Source([["=SUM(A1)", 123.45m, new DateTime(2026, 10, 25), true, null], ["x", 9007199254740993L, null, false, ""]]);
        await Export(source, "xlsx", true);
        using var zip = ZipFile.OpenRead(Destination("xlsx"));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/styles.xml"));
        var sheet = Xml(zip, "xl/worksheets/sheet1.xml");
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = sheet.Descendants(ns + "c").ToDictionary(c => c.Attribute("r")!.Value);
        Assert.Equal("inlineStr", cells["A2"].Attribute("t")!.Value);
        Assert.Equal("=SUM(A1)", cells["A2"].Descendants(ns + "t").Single().Value);
        Assert.Empty(sheet.Descendants(ns + "f"));
        Assert.Equal("123.45", cells["B2"].Element(ns + "v")!.Value);
        Assert.Equal("1", cells["C2"].Attribute("s")!.Value);
        Assert.Equal("b", cells["D2"].Attribute("t")!.Value);
        Assert.Equal("1", cells["D2"].Element(ns + "v")!.Value);
        Assert.Equal("inlineStr", cells["B3"].Attribute("t")!.Value);
        Assert.Equal("9007199254740993", cells["B3"].Descendants(ns + "t").Single().Value);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
        Assert.DoesNotContain(root, Xml(zip, "xl/worksheets/sheet2.xml").ToString());
    }

    /// <summary>空表仍导出完整标题, 不把没有数据误报为失败.</summary>
    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task EmptyTableKeepsHeaders(string format)
    {
        var result = await Export(new Source([]), format);
        Assert.Equal(0, result.RowCount);
        Assert.True(File.Exists(Destination(format)));
        if (format == "csv") Assert.Equal("Text,Number,Date,Flag,Empty\r\n", File.ReadAllText(Destination(format)));
    }

    /// <summary>无效格式、CSV 日志和不支持的受限来源在建立数据查询或文件前拒绝.</summary>
    [Theory]
    [InlineData("pdf", false, "desktop", false, "EXPORT_FORMAT_INVALID")]
    [InlineData("csv", true, "desktop", false, "EXPORT_LOG_UNSUPPORTED")]
    [InlineData("xlsx", false, "pbip", false, "EXPORT_SOURCE_UNSUPPORTED")]
    [InlineData("xlsx", false, "ssas-xmla", true, "EXPORT_SOURCE_UNSUPPORTED")]
    [InlineData("xlsx", false, "desktop", true, "EXPORT_SOURCE_UNSUPPORTED")]
    public async Task InvalidOptionsAndSourcesAreRejected(string format, bool log, string kind, bool limited, string code)
    {
        var source = new Source([]) { Snapshot = Snapshot() with { Connection = new(kind, kind, limited) } };
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, format, log));
        Assert.Equal(code, error.Code);
        Assert.False(source.Opened);
        Assert.Empty(Directory.GetFiles(root));
    }

    /// <summary>Service 的元数据限制不阻止实际游标导出; 即使字段架构未提供, 两种格式也保留真实标题和重复行.</summary>
    /// <param name="format">本轮实际写入的文件格式.</param>
    /// <param name="hasSchemaColumns">是否能从可见架构读取字段, 不作为数据游标字段的替代.</param>
    [Theory]
    [InlineData("csv", true)]
    [InlineData("csv", false)]
    [InlineData("xlsx", true)]
    [InlineData("xlsx", false)]
    public async Task LimitedServiceExportsActualCursorWithoutCompleteMetadata(string format, bool hasSchemaColumns)
    {
        var snapshot = Snapshot() with { Connection = new("powerbi-service", "Service", true) };
        if (!hasSchemaColumns) snapshot = snapshot with { Model = snapshot.Model with { Tables = [snapshot.Model.Tables[0] with { Columns = [] }] } };
        var source = new Source([["same", 9007199254740993L, null, true, null], ["same", 9007199254740993L, null, true, null]]) { Snapshot = snapshot };
        var result = await Export(source, format);
        Assert.Equal(2, result.RowCount);
        Assert.True(source.Opened);
        Assert.True(source.Disposed);
        Assert.True(source.Snapshot.Connection!.IsLimited);
        if (format == "csv")
            Assert.Equal("Text,Number,Date,Flag,Empty\r\nsame,9007199254740993,,TRUE,\r\nsame,9007199254740993,,TRUE,\r\n", File.ReadAllText(Destination(format)));
        else
        {
            using var zip = ZipFile.OpenRead(Destination(format));
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = Xml(zip, "xl/worksheets/sheet1.xml").Descendants(ns + "row").ToArray();
            Assert.Equal(3, rows.Length);
            Assert.Equal(Columns, rows[0].Descendants(ns + "t").Select(value => value.Value));
            Assert.Equal(rows[1].Elements(ns + "c").Select(cell => cell.Value), rows[2].Elements(ns + "c").Select(cell => cell.Value));
        }
    }

    /// <summary>完整架构已确认没有实体列的来源仍拒绝, 不把 Service 特例扩大到完整元数据或其他来源.</summary>
    /// <param name="kind">宿主绑定的在线模型来源.</param>
    [Theory]
    [InlineData("desktop")]
    [InlineData("ssas-xmla")]
    [InlineData("powerbi-service")]
    public void CompleteSourceWithoutDataColumnsRemainsUnavailable(string kind)
    {
        var snapshot = Snapshot() with { Connection = new(kind, kind) };
        snapshot = snapshot with { Model = snapshot.Model with { Tables = [snapshot.Model.Tables[0] with { Columns = [] }] } };
        var error = Assert.Throws<AppException>(() => ModelTableExportService.ValidateSnapshot(snapshot, new("project", "baseline", ["Sales"], "csv")));
        Assert.Equal("EXPORT_TABLE_EMPTY", error.Code);
    }

    /// <summary>允许受限 Service 查询仍保留项目、基线及待核验保护, 任何失败均发生在文件和数据会话之前.</summary>
    /// <param name="changed">本用例改变的身份或保存状态.</param>
    /// <param name="expectedCode">预期的稳定拒绝码.</param>
    [Theory]
    [InlineData("project", "STALE_BASELINE")]
    [InlineData("baseline", "STALE_BASELINE")]
    [InlineData("pending", "UNSAVED_CHANGES")]
    [InlineData("save", "UNSAVED_CHANGES")]
    public async Task LimitedServiceRetainsIdentityAndPendingGuards(string changed, string expectedCode)
    {
        var snapshot = Snapshot() with { Connection = new("powerbi-service", "Service", true) };
        snapshot = changed switch
        {
            "project" => snapshot with { ProjectId = "different" },
            "baseline" => snapshot with { BaselineId = "different" },
            "pending" => snapshot with { HasPendingChanges = true },
            _ => snapshot with { SaveResult = new(false, false, false, "unknown", "SAVE_UNKNOWN") }
        };
        var source = new Source([]) { Snapshot = snapshot };
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, "csv"));
        Assert.Equal(expectedCode, error.Code);
        Assert.False(source.Opened);
        Assert.Empty(Directory.GetFiles(root));
    }

    /// <summary>权威目标或基线在读取前后变化时保留已有目标并清除临时文件.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleBaselinePreservesExistingDestination(bool afterRead)
    {
        Directory.CreateDirectory(root); File.WriteAllText(Destination("csv"), "original");
        var source = new Source([["x", 1, null, true, null]]);
        if (afterRead) source.AfterRead = () => source.Snapshot = source.Snapshot with { BaselineId = "changed" };
        else source.Snapshot = source.Snapshot with { ProjectId = "changed" };
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, "csv"));
        Assert.Equal("STALE_BASELINE", error.Code);
        Assert.Equal("original", File.ReadAllText(Destination("csv")));
        Assert.Single(Directory.GetFiles(root));
    }

    /// <summary>读取、提交前取消和超长单元格失败均不发布部分文件.</summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("read")]
    [InlineData("long")]
    [InlineData("commit")]
    public async Task FailureAndCancellationPreserveDestination(string failure)
    {
        Directory.CreateDirectory(root); File.WriteAllText(Destination("xlsx"), "original");
        using var cancel = new CancellationTokenSource();
        var source = new Source([[failure == "long" ? new string('x', 32768) : "x", 1, null, true, null]])
        {
            BeforeRow = () => { if (failure == "cancel") cancel.Cancel(); if (failure == "read") throw new InvalidOperationException("SDK detail"); }
        };
        if (failure == "commit") source.AfterRead = () => cancel.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => Export(source, "xlsx", token: cancel.Token));
        Assert.Equal("original", File.ReadAllText(Destination("xlsx")));
        Assert.Single(Directory.GetFiles(root));
        Assert.True(source.Disposed);
    }

    /// <summary>目标被另一个进程修改时不得覆盖该修改, 提交回调只在可发布时执行.</summary>
    [Fact]
    public async Task DestinationConflictAndCommitCancellationAreRespected()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Destination("csv"), "original");
        var source = new Source([]) { AfterRead = () => File.WriteAllText(Destination("csv"), "other") };
        var commits = 0;
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, "csv", committing: () => commits++));
        Assert.Equal("EXPORT_DESTINATION_CHANGED", error.Code);
        Assert.Equal(0, commits);
        Assert.Equal("other", File.ReadAllText(Destination("csv")));
        Assert.Single(Directory.GetFiles(root));
    }

    /// <summary>宿主提交回调期间出现的新目标内容必须保留, 不允许在最终发布窗口覆盖.</summary>
    [Fact]
    public async Task DestinationChangedInsideCommitCallbackIsPreserved()
    {
        File.WriteAllText(Destination("csv"), "original");
        var error = await Assert.ThrowsAsync<AppException>(() => Export(new Source([]), "csv", committing: () => File.WriteAllText(Destination("csv"), "concurrent")));
        Assert.Equal("EXPORT_DESTINATION_CHANGED", error.Code);
        Assert.Equal("concurrent", File.ReadAllText(Destination("csv")));
        Assert.Single(Directory.GetFiles(root));
    }

    /// <summary>真实受锁句柄允许发布替换旧目标, 完成后仅留下最终文件且正确记录唯一提交.</summary>
    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ExistingDestinationIsReplacedAndBackupRemoved(string format)
    {
        File.WriteAllText(Destination(format), "original");
        var commits = 0;
        await Export(new Source([]), format, committing: () => commits++);
        Assert.Equal(1, commits);
        Assert.NotEqual("original", File.ReadAllText(Destination(format)));
        Assert.Equal([Destination(format)], Directory.GetFiles(root));
    }

    /// <summary>最终指纹核验范围内原目标同时拒绝其他写入和删除, 保护一直保持到发布.</summary>
    [Fact]
    public void LockedOriginalCannotBeWrittenOrDeletedDuringFinalValidation()
    {
        var destination = Destination("csv"); var temporary = Path.Combine(root, "temporary.tmp");
        File.WriteAllText(destination, "original"); File.WriteAllText(temporary, "complete");
        var published = false;
        ModelTableExportPublication.Publish(temporary, destination, "same", _ =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(destination, "concurrent"));
            Assert.Throws<IOException>(() => File.Delete(destination));
            return "same";
        }, () => published = true);
        Assert.True(published);
        Assert.Equal("complete", File.ReadAllText(destination));
        Assert.Single(Directory.GetFiles(root));
    }

    /// <summary>原先没有目标时, 最后提交窗口中新建的用户文件也不得被覆盖.</summary>
    [Fact]
    public async Task NewlyCreatedDestinationIsNotOverwritten()
    {
        var error = await Assert.ThrowsAsync<AppException>(() => Export(new Source([]), "csv", committing: () => File.WriteAllText(Destination("csv"), "concurrent")));
        Assert.Equal("EXPORT_DESTINATION_CHANGED", error.Code);
        Assert.Equal("concurrent", File.ReadAllText(Destination("csv")));
        Assert.Single(Directory.GetFiles(root));
    }

    /// <summary>读取首因和取消码在清理也失败时保持, 安全提示附加真实清理事实.</summary>
    [Fact]
    public async Task CleanupFailurePreservesOriginalFailureAndCancellation()
    {
        var source = new Source([]);
        foreach (Exception expected in new Exception[] { new AppException("FIRST_FAILURE", "safe", 4), new OperationCanceledException() })
        {
            var writer = new CleanupFailureWriter(expected);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => new ModelTableExportService(source, source, writer).ExportAsync("model://test", new("project", "baseline", ["Sales"], "csv"), Destination("csv"), default));
            Assert.Same(expected, error);
            Assert.Contains("清理失败", ModelTableExportService.DecorateCleanupFailure(error, "safe"));
        }
    }

    /// <summary>Excel 行列硬上限必须整批失败并保留原目标, 不静默截断为成功文件.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExcelRowAndColumnLimitsKeepOriginalFile(bool rows)
    {
        File.WriteAllText(Destination("xlsx"), "original");
        var source = new Source([["x"]])
        {
            ColumnNames = rows ? ["Col"] : Enumerable.Range(0, 16385).Select(i => "C" + i).ToArray(),
            GeneratedRows = rows ? 1048576 : 0
        };
        var error = await Assert.ThrowsAsync<AppException>(() => Export(source, "xlsx"));
        Assert.Equal("EXPORT_EXCEL_LIMIT", error.Code);
        Assert.Equal("original", File.ReadAllText(Destination("xlsx")));
        Assert.Single(Directory.GetFiles(root));
    }

    /// <summary>导出读范围与其他编辑/切换共用门闩, 完成后释放且支持完整远端只读目标.</summary>
    [Fact]
    public async Task ControlledReadScopeSerializesEditsAndReleasesGate()
    {
        var source = new Source([]) { Snapshot = Snapshot() with { Connection = new("ssas-xmla", "SSAS") } };
        using var edits = new ModelEditService(source, new NoWorkspace()); edits.Select(source.Snapshot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = edits.ExecuteReadAsync("project", "baseline", async (snapshot, _) => { entered.SetResult(); await complete.Task; return snapshot.ProjectId; }, default);
        await entered.Task;
        Assert.Equal("BUSY", (await Assert.ThrowsAsync<AppException>(() => edits.ExecuteReadAsync("project", "baseline", (_, _) => Task.FromResult(0), default))).Code);
        Assert.Equal("BUSY", Assert.Throws<AppException>(() => edits.Select(null)).Code);
        complete.SetResult(); Assert.Equal("project", await operation);
        source.Snapshot = source.Snapshot with { BaselineId = "changed" };
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => edits.ExecuteReadAsync("project", "baseline", (_, _) => Task.FromResult(0), default))).Code);
        edits.Select(source.Snapshot);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.ExecuteReadAsync<int>("project", "changed", (_, _) => throw new OperationCanceledException(), default));
        edits.Select(null);
    }

    /// <summary>Excel 1900 闰年兼容规则不应使一月和二月的日期晚一天.</summary>
    [Theory]
    [InlineData(1900, 1, 1, "1")]
    [InlineData(1900, 2, 28, "59")]
    [InlineData(1900, 3, 1, "61")]
    public async Task ExcelEarlyDateSerialsAreCorrect(int year, int month, int day, string expected)
    {
        await Export(new Source([["x", 1, new DateTime(year, month, day), true, null]]), "xlsx");
        using var zip = ZipFile.OpenRead(Destination("xlsx"));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal(expected, Xml(zip, "xl/worksheets/sheet1.xml").Descendants(ns + "c").Single(c => c.Attribute("r")!.Value == "C2").Element(ns + "v")!.Value);
    }

    /// <summary>工作簿保留不同换行和 XML 控制字符, 用户原有 OOXML 转义模式不得被二次解释.</summary>
    [Fact]
    public async Task ExcelTextPreservesLineEndingsAndEscapesControlCharacters()
    {
        await Export(new Source([["a\rb\nc\r\nd\0_x0001_", 1, null, true, null]]), "xlsx");
        using var zip = ZipFile.OpenRead(Destination("xlsx"));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cell = Xml(zip, "xl/worksheets/sheet1.xml").Descendants(ns + "c").Single(c => c.Attribute("r")!.Value == "A2");
        Assert.Equal("a\rb\nc\r\nd_x0000__x005F_x0001_", cell.Descendants(ns + "t").Single().Value);
    }

    /// <summary>固定表查询默认使用 API 核验的 long 布局, 对标识符和别名正确转义并排除内部行号.</summary>
    [Fact]
    public void QueryEscapesNamesAndRetainsDuplicates()
    {
        var table = new TableInfo("a'b", null, false, [new("x]y\"", "Data", "String", false, null), new("Row", "RowNumber", "Int64", true, null)], []);
        Assert.Equal("EVALUATE\nSELECTCOLUMNS ( 'a''b', \"x]y\"\"\", 'a''b'[x]]y\"] )", ModelTableExportQuery.Create(table));
    }

    /// <summary>较长投影按官方 long 布局保留别名/列引用参数对, 抽样源仍先执行且不引入排序或去重.</summary>
    [Fact]
    public void LongProjectionKeepsAliasReferencePairsTogether()
    {
        var table = new TableInfo("Sales", null, false,
            [new("Amount", "Data", "Decimal", false, null), new("Units", "Data", "Int64", false, null), new("Product", "Data", "String", false, null)], []);
        Assert.Equal("""
            EVALUATE
            SELECTCOLUMNS (
                SAMPLE ( 100, 'Sales', 0 ),
                "Amount", 'Sales'[Amount],
                "Units", 'Sales'[Units],
                "Product", 'Sales'[Product]
            )
            """, ModelTableExportQuery.Create(table, 100));
    }

    /// <summary>固定查询布局不截断大型投影, 不把标识符或别名内部的换行误认为布局.</summary>
    [Fact]
    public void QueryLayoutPreservesLargeProjectionAndLiteralLineBreaks()
    {
        var columns = Enumerable.Range(0, 200).Select(index => new ColumnInfo(new string('列', 100) + index, "Data", "String", false, null)).ToArray();
        var table = new TableInfo("首行\n次行", null, false, columns, []);
        var query = ModelTableExportQuery.Create(table, 100);
        Assert.True(query.Length > 30000);
        Assert.Contains("'首行\n次行'[" + columns[^1].Name + "]", query);
        var simple = new TableInfo("Sales", null, false, [new("首行\n次行", "Data", "String", false, null)], []);
        Assert.Contains("\"首行\n次行\",", ModelTableExportQuery.Create(simple));
    }

    /// <summary>生成前取消优先于空投影或非法抽样参数, 两种入口均保留调用方的取消令牌.</summary>
    [Fact]
    public void CancelledQueryGenerationPreservesCancellationBeforeValidation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var table = new TableInfo("Sales", null, false, [], []);
        var projected = Assert.ThrowsAny<OperationCanceledException>(() => ModelTableExportQuery.Create(table, cancellationToken: cancellation.Token));
        var direct = Assert.ThrowsAny<OperationCanceledException>(() => ModelTableExportQuery.Create("Sales", 0, cancellation.Token));
        Assert.Equal(cancellation.Token, projected.CancellationToken);
        Assert.Equal(cancellation.Token, direct.CancellationToken);
    }

    /// <summary>列枚举途中取消须停止后续遍历, 不被全部内部行号的空投影错误或后续枚举异常覆盖.</summary>
    [Fact]
    public void CancellationDuringProjectionEnumerationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var table = new TableInfo("Sales", null, false, new CancellingColumns(cancellation), []);
        var error = Assert.ThrowsAny<OperationCanceledException>(() => ModelTableExportQuery.Create(table, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    /// <summary>在首次枚举时确定性触发取消, 再次推进则失败, 无计时或线程调度依赖.</summary>
    private sealed class CancellingColumns(CancellationTokenSource cancellation) : IReadOnlyList<ColumnInfo>
    {
        public int Count => 1;
        public ColumnInfo this[int index] => index == 0 ? new("Row", "RowNumber", "Int64", true, null) : throw new ArgumentOutOfRangeException(nameof(index));
        /// <summary>返回首个内部列前取消, 后续读取抛错以验证查询生成已停止.</summary>
        public IEnumerator<ColumnInfo> GetEnumerator()
        {
            cancellation.Cancel();
            yield return this[0];
            throw new InvalidOperationException("取消后不应继续读取模型列");
        }
        /// <summary>非泛型枚举复用同一取消与失败行为.</summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>执行被测真实共享服务, 数据源仅代替外部引擎.</summary>
    private Task<ModelTableExportResult> Export(Source source, string format, bool log = false, Action? committing = null, CancellationToken token = default)
        => new ModelTableExportService(source, source, new ModelTableFileWriter()).ExportAsync("model://test", new("project", "baseline", ["Sales"], format, log), Destination(format), token, committing);
    /// <summary>取得本测试内的固定格式输出路径.</summary>
    private string Destination(string format) => Path.Combine(root, "table." + format);
    /// <summary>读取真实工作簿中的指定 XML 条目, 只保留解析结果并释放条目流.</summary>
    private static XDocument Xml(ZipArchive zip, string name) { using var stream = zip.GetEntry(name)!.Open(); return XDocument.Load(stream); }
    /// <summary>构造已连接且完整可查询的只读测试快照.</summary>
    private static ProjectSnapshot Snapshot() => new("model://test", "project", "baseline", "TOM", "", "1", null,
        new("model", 1606, "en-US", [new("Sales", null, false, Columns.Select(c => new ColumnInfo(c, "Data", "String", false, null)).ToArray(), [])], [], [], 0, 0), [], [], [], new("desktop", "Desktop"));

    /// <summary>只替代外部数据读取与游标, 文件和格式处理始终使用产品真实实现.</summary>
    private sealed class Source(object?[][] values) : IProjectReader, IModelTableDataReader, IModelTableRows
    {
        public ProjectSnapshot Snapshot { get; set; } = ModelTableExportTests.Snapshot();
        public Action? AfterRead { get; set; }
        public Action? BeforeRow { get; set; }
        public IReadOnlyList<string> ColumnNames { get; init; } = ModelTableExportTests.Columns;
        public int? GeneratedRows { get; init; }
        public bool Opened { get; private set; }
        public bool Disposed { get; private set; }
        private int row = -1;
        public IReadOnlyList<string> Columns => ColumnNames;
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot); }
        public Task<long> ReadAsync(ProjectSnapshot snapshot, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
        {
            Opened = true;
            try { var count = consume(this, token); AfterRead?.Invoke(); return Task.FromResult(count); }
            finally { Disposed = true; }
        }
        public bool Read() { BeforeRow?.Invoke(); return ++row < (GeneratedRows ?? values.Length); }
        public object? GetValue(int ordinal) => values[GeneratedRows is null ? row : 0][ordinal];
    }

    /// <summary>只代替独立失败的文件资源, 用于验证首因不被 Dispose 覆盖.</summary>
    private sealed class CleanupFailureWriter(Exception failure) : IModelTableFileWriter, IModelTableExportFile
    {
        public string FileName => "table.csv";
        public Task<IModelTableExportFile> CreateAsync(string destination, ModelTableExportRequest request, CancellationToken token) => Task.FromResult<IModelTableExportFile>(this);
        public long Write(string table, IModelTableRows rows, CancellationToken token) => throw failure;
        public void Complete(CancellationToken token) => throw new InvalidOperationException();
        public Task CommitAsync(Action? beginCommit, CancellationToken token) => throw new InvalidOperationException();
        public void Dispose() => throw new AppException("EXPORT_CLEANUP_FAILED", "cleanup", 4);
    }

    /// <summary>只读范围不得尝试创建写入草稿.</summary>
    private sealed class NoWorkspace : IModelEditWorkspace
    {
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => throw new InvalidOperationException("Read scope cannot create drafts");
    }

    /// <summary>仅清理本测试创建的唯一临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
