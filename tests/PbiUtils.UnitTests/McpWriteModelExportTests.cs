// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteModelExportTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过 MCP 精确请求与回执验证真实 CSV/XLSX 发布和成功导出目录打开边界.

using PbiUtils.Infrastructure.Models.Export;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Execution;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Mcp;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>外部模型与操作系统启动为显式替身, 文件格式、发布、目录记录及 MCP 生命周期均真实执行.</summary>
public sealed class McpWriteModelExportTests
{
    private static readonly string[] SalesTables = ["Sales"];

    /// <summary>指定精确表及格式后发布真实文件, 回读内容及目录记录, 再直接打开宿主保存的同一目录.</summary>
    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ExportTablesPublishesContentAndOpensRecordedDirectory(string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-mcp-write-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new ExportSource();
            var destination = Path.Combine(root, "Sales." + format);
            var storage = Path.Combine(root, "user");
            var locations = new LocalModelExportLocationStore(storage);
            var journal = new ExecutionJournal(storage);
            ConcurrentQueue<JsonElement> prompts = new();
            using var dialogs = new UiDialogBroker(text =>
            {
                var message = JsonSerializer.Deserialize<JsonElement>(text);
                if (message.TryGetProperty("uiDialog", out _)) prompts.Enqueue(message);
            });
            using var edits = new ModelEditService(source, source);
            List<string> opened = [];
            var selections = 0;
            using var session = new BridgeSession(new(false, null), new(new RuntimeEnvironment()), _ => { }, "test",
                projects: new(edits), edits: edits, selectProject: _ => Task.FromResult<string?>("fixture.pbip"),
                connections: new TestLocalFileConnection(source).Service(), dialogs: dialogs, mcpJournal: journal,
                tableExports: new(source, source, new ModelTableFileWriter()), exportLocations: locations,
                chooseTableExportFile: (request, token) =>
                {
                    token.ThrowIfCancellationRequested(); selections++;
                    Assert.Equal(format, request.Format); Assert.Equal(["Sales"], request.Tables);
                    return Task.FromResult<string?>(destination);
                }, openExportDirectory: (path, token) =>
                {
                    Assert.False(token.CanBeCanceled); Assert.True(Directory.Exists(path)); opened.Add(path);
                    return Task.CompletedTask;
                });
            await session.ReceiveAsync(OriginPolicy.Production, JsonSerializer.Serialize(new { protocolVersion = 1, id = "connect", method = "project.open", @params = new { } }));
            var connectionReceipt = AssertReceipt(storage, "project.open");
            Assert.Equal(connectionReceipt, Assert.Single(journal.List()));
            long revision = 0;
            var selected = Assert.IsType<ProjectSnapshot>(await session.InvokeMcpAsync("project.inspect", Json(new { }), default, observeConnectionRevision: observed => revision = observed));
            Assert.Equal(1, revision);
            var parameters = Json(new { selected.ProjectId, selected.BaselineId, tables = SalesTables, format, includeLog = false, locale = "zh-cn", rowLimit = (int?)null });
            Assert.False(File.Exists(destination));
            Assert.Equal(0, selections);
            var pending = session.InvokeMcpAsync("model.exportTables", parameters, default, revision);
            var result = Assert.IsType<ModelTableExportResult>(await pending);
            Assert.Equal(1, selections);
            Assert.Equal(1, source.ReadCalls);
            Assert.Equal(2, result.RowCount);
            Assert.Equal(new ModelTableExportItem("Sales", 2), Assert.Single(result.Tables));
            Assert.Null(result.LocationWarning);
            Assert.Equal(Path.GetFileName(destination), result.FileName);
            if (format == "csv") Assert.Equal("Text,Amount\r\n\"a,\"\"中\"\"\",12.5\r\n=1+1,0\r\n", await File.ReadAllTextAsync(destination));
            else VerifyWorkbook(destination);
            var exportReceipt = AssertReceipt(storage, "model.exportTables");
            var exportedReceipts = journal.List();
            Assert.Equal(2, exportedReceipts.Count);
            Assert.Equal(connectionReceipt, Assert.Single(exportedReceipts, item => item.Record.ExecutionId == connectionReceipt.Record.ExecutionId));
            Assert.Equal(exportReceipt, Assert.Single(exportedReceipts, item => item.Record.ExecutionId != connectionReceipt.Record.ExecutionId));

            var location = new LocalModelExportLocationStore(storage).Read();
            Assert.True(location.Available);
            var openParameters = Json(new { id = location.Id });
            Assert.Empty(opened);
            var opening = session.InvokeMcpAsync("model.openExportDirectory", openParameters, default, revision);
            var openedResult = Json(await opening ?? throw new InvalidOperationException("Missing directory result"));
            Assert.True(openedResult.GetProperty("opened").GetBoolean());
            Assert.Equal(Path.GetFullPath(root), Assert.Single(opened));
            var directoryReceipt = AssertReceipt(storage, "model.openExportDirectory");
            var finalReceipts = journal.List();
            Assert.Equal(exportedReceipts.Count + 1, finalReceipts.Count);
            foreach (var previous in exportedReceipts)
                Assert.Equal(previous, Assert.Single(finalReceipts, item => item.Record.ExecutionId == previous.Record.ExecutionId));
            Assert.Equal(directoryReceipt, Assert.Single(finalReceipts, item => exportedReceipts.All(previous => previous.Record.ExecutionId != item.Record.ExecutionId)));
            Assert.Empty(prompts);
            Assert.Equal(0, session.PendingCount);
            session.Dispose(); await session.ShutdownAsync();
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>重新读取 XLSX 标准 ZIP/XML, 验证两行业务值及公式文本没有变成可执行公式.</summary>
    private static void VerifyWorkbook(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = (zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidOperationException("Missing worksheet")).Open();
        var sheet = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = sheet.Descendants(ns + "c").ToDictionary(cell => cell.Attribute("r")!.Value);
        Assert.Equal("Text", cells["A1"].Descendants(ns + "t").Single().Value);
        Assert.Equal("Amount", cells["B1"].Descendants(ns + "t").Single().Value);
        Assert.Equal("a,\"中\"", cells["A2"].Descendants(ns + "t").Single().Value);
        Assert.Equal("12.5", cells["B2"].Element(ns + "v")!.Value);
        Assert.Equal("inlineStr", cells["A3"].Attribute("t")!.Value);
        Assert.Equal("=1+1", cells["A3"].Descendants(ns + "t").Single().Value);
        Assert.Equal("0", cells["B3"].Element(ns + "v")!.Value);
        Assert.Empty(sheet.Descendants(ns + "f"));
        Assert.Equal(3, sheet.Descendants(ns + "row").Count());
    }

    /// <summary>重新打开真实日志核对指定方法的完成与提交事实, 返回含执行身份与原文摘要的记录供后续稳定性核验.</summary>
    private static ExecutionView AssertReceipt(string root, string method)
    {
        var receipt = Assert.Single(new ExecutionJournal(root).List(), item => item.Record.Method == method);
        Assert.Equal("completed", receipt.Record.State); Assert.True(receipt.Record.CommitStarted); Assert.Null(receipt.Record.ErrorCode);
        return receipt;
    }

    /// <summary>按产品 Web 契约构造参数, 不包含任意路径或查询.</summary>
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>仅替代外部在线模型读取与数据游标, 文件写入由真实生产 writer 完成.</summary>
    private sealed class ExportSource : IProjectReader, IModelEditWorkspace, IModelTableDataReader
    {
        private readonly ProjectSnapshot snapshot = new("model://mcp-export-fixture", "fixture", "baseline", "TOM", "", "", null,
            new("Fixture", 1606, "en-US", [new("Sales", null, false,
                [new("Text", "Data", "String", false, null), new("Amount", "Data", "Double", false, null)], [])], [], [], 0, 0), [], [], [], new("desktop", "Fixture"));
        internal int ReadCalls { get; private set; }
        /// <summary>固定的测试权威结构, 不使用用户端点或凭据.</summary>
        public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(snapshot); }
        /// <summary>导出不得意外创建写草稿.</summary>
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => throw new InvalidOperationException("Export must remain model read-only");
        /// <summary>提供有类型的两行外部数据替身, 真实 writer 同步消费, 不伪造发布结果.</summary>
        public Task<long> ReadAsync(ProjectSnapshot target, string table, int? rowLimit, Func<IModelTableRows, CancellationToken, long> consume, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal(snapshot, target); Assert.Equal("Sales", table); Assert.Null(rowLimit);
            ReadCalls++; return Task.FromResult(consume(new Rows(), token));
        }

        /// <summary>只包含本测试固定值的同步数据游标.</summary>
        private sealed class Rows : IModelTableRows
        {
            private int index = -1;
            private readonly object[][] values = [["a,\"中\"", 12.5m], ["=1+1", 0m]];
            public IReadOnlyList<string> Columns => ["Text", "Amount"];
            /// <summary>移动到下一行, 到末尾返回 false.</summary>
            public bool Read() => ++index < values.Length;
            /// <summary>返回当前行指定列的原始值, 非法索引自然失败.</summary>
            public object? GetValue(int ordinal) => values[index][ordinal];
        }
    }
}
