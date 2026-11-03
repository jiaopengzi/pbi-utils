// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ProjectTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以临时独立工程验证内容识别, 官方模型读取与路径安全.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>独立夹具不会读取或改写用户工程.</summary>
public sealed class ProjectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-tests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service = new(new ProjectReader());

    /// <summary>建立最小内容契约夹具, 不声称为 Desktop 黄金模板.</summary>
    public ProjectTests()
    {
        Write("示例.pbip", """{"version":"1.0","artifacts":[{"report":{"path":"report"}}]}""");
        Write("report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../model"}}}""");
        Write("report/definition/report.json", "{}");
        Write("report/definition/pages/pages.json", """{"pageOrder":["p1"]}""");
        Write("report/definition/pages/p1/page.json", """{"name":"p1","displayName":"中文页","width":1280,"height":720}""");
        Write("model/definition.pbism", """{"version":"1.0"}""");
        Write("model/model.bim", """{"name":"测试","compatibilityLevel":1600,"model":{"culture":"zh-CN","tables":[{"name":"销售","columns":[{"name":"金额","dataType":"int64","sourceColumn":"金额"}],"measures":[{"name":"销售额","expression":"SUM('销售'[金额])","formatString":"0.00","description":"说明"}]}]}}""");
    }
    /// <summary>只在本测试唯一目录写入已知夹具.</summary>
    private void Write(string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    /// <summary>验证真实解析结果、原文保留、重复读取基线一致和源字节不变.</summary>
    [Fact]
    public async Task ReadsBoundModelAndReportWithoutChangingSource()
    {
        var before = File.ReadAllBytes(Path.Combine(root, "model/model.bim"));
        var result = await service.InspectAsync(root, default);
        Assert.Equal("TMSL", result.ModelFormat);
        Assert.Equal("PBIR", result.ReportFormat);
        Assert.Equal("销售额", Assert.Single(result.Model.Measures).Name);
        Assert.Equal("SUM('销售'[金额])", result.Model.Measures[0].Expression);
        Assert.Equal("中文页", Assert.Single(result.Pages).DisplayName);
        Assert.Equal(result.BaselineId, (await service.InspectAsync(root, default)).BaselineId);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "model/model.bim")));
    }
    /// <summary>坏绑定、未知版本、多个入口和畸形 JSON 均为明确失败.</summary>
    [Theory]
    [InlineData("report/definition.pbir", "{\"version\":\"4.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../../outside\"}}}", "UNSAFE_PROJECT_PATH")]
    [InlineData("model/definition.pbism", "{\"version\":\"99.0\"}", "UNSUPPORTED_VERSION")]
    [InlineData("model/definition.pbism", "{\"version\":\"1.0\",\"version\":\"4.0\"}", "INVALID_PROJECT")]
    [InlineData("report/definition.pbir", "{\"version\":\"4.0\",\"datasetReference\":{\"byConnection\":{}}}", "REMOTE_MODEL_UNSUPPORTED")]
    [InlineData("model/model.bim", "not json", "MODEL_PARSE_FAILED")]
    [InlineData("report/definition/pages/p1/page.json", "{\"name\":\"other\"}", "INVALID_REPORT_REFERENCE")]
    public async Task RejectsInvalidContent(string file, string content, string code)
    {
        Write(file, content);
        Assert.Equal(code, (await Assert.ThrowsAsync<AppException>(() => service.InspectAsync(root, default))).Code);
    }
    /// <summary>不任意选择第一个工程, 并在取消后不继续读取.</summary>
    [Fact]
    public async Task AmbiguityAndCancellation()
    {
        Write("第二.pbip", "{}");
        Assert.Equal("AMBIGUOUS_PROJECT", (await Assert.ThrowsAsync<AppException>(() => service.InspectAsync(root, default))).Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InspectAsync(root, new(true)));
    }
    /// <summary>副本修改必须精确批准; 拒绝重放, 原文不变且最终副本可回读.</summary>
    [Fact]
    public async Task ChangeCopyRequiresExactApprovalAndPreservesSource()
    {
        var output = root + "-copy";
        using var changes = new ChangeService(new ChangeWorkspace(), TimeProvider.System);
        var request = new ChangeRequest(Path.Combine(root, "示例.pbip"), output, "measure.set", "销售额", "description", "新说明", "销售");
        var plan = await changes.PrepareAsync(request, default);
        Assert.Single(plan.Changes);
        Assert.Contains("新说明", plan.Changes[0].After);
        Assert.False(Directory.Exists(output));
        Assert.Equal("APPROVAL_MISMATCH", (await Assert.ThrowsAsync<AppException>(() => changes.ApplyAsync(plan.Id, "forged", default))).Code);
        var result = await changes.ApplyAsync(plan.Id, plan.Digest, default);
        Assert.Equal("files_created", result.Stage);
        Assert.Equal("新说明", (await service.InspectAsync(result.ProjectPath, default)).Model.Measures[0].Description);
        Assert.Equal("说明", (await service.InspectAsync(root, default)).Model.Measures[0].Description);
        Assert.Equal("PLAN_NOT_FOUND", (await Assert.ThrowsAsync<AppException>(() => changes.ApplyAsync(plan.Id, plan.Digest, default))).Code);
        Directory.Delete(output, true);
    }
    /// <summary>源新增文件和修改都会使批准前基线失效, 不发布半成品.</summary>
    [Fact]
    public async Task StalePlanAndUdfGate()
    {
        using var changes = new ChangeService(new ChangeWorkspace(), TimeProvider.System);
        var plan = await changes.PrepareAsync(new(Path.Combine(root, "示例.pbip"), root + "-copy", "table.set", "销售", "description", "test"), default);
        Write("external.txt", "并发新增");
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => changes.ApplyAsync(plan.Id, plan.Digest, default))).Code);
        Assert.False(Directory.Exists(root + "-copy"));
        Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", (await Assert.ThrowsAsync<AppException>(() => changes.PrepareAsync(new(Path.Combine(root, "示例.pbip"), root + "-udf", "udf.set", "Test.Double", "expression", "(x : NUMERIC) => x * 2"), default))).Code);
    }
    /// <summary>清理本测试拥有的唯一临时根, 不操作其他目录.</summary>
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>日期表及期间模板在官方序列化后可读取, 重复生成幂等, 人工修改不被覆盖.</summary>
    [Fact]
    public async Task CalendarAndPeriodCopiesAreIdempotentAndRejectManualEdits()
    {
        using var changes = new ChangeService(new ChangeWorkspace(), TimeProvider.System);
        var copies = new[] { root + "-calendar", root + "-period" };
        try
        {
            var calendar = new ChangeRequest(Path.Combine(root, "示例.pbip"), copies[0], "calendar.create", "Calendar", "config", """{"startYear":2024,"endYear":2025}""");
            var plan = await changes.PrepareAsync(calendar, default);
            var result = await changes.ApplyAsync(plan.Id, plan.Digest, default);
            var snapshot = await service.InspectAsync(result.ProjectPath, default);
            Assert.Equal(7, snapshot.Model.Tables.Single(t => t.Name == "Calendar").Columns.Count);
            var repeated = await changes.PrepareAsync(calendar with { ProjectPath = result.ProjectPath, OutputPath = root + "-unused" }, default);
            Assert.Empty(repeated.Changes);
            changes.Discard(repeated.Id);
            var period = new ChangeRequest(result.ProjectPath, copies[1], "period.generate", "销售额", "config", """{"dateTable":"Calendar","dateColumn":"Date","measureTable":"销售","templates":["YTD","YOY","PY"]}""");
            plan = await changes.PrepareAsync(period, default);
            result = await changes.ApplyAsync(plan.Id, plan.Digest, default);
            snapshot = await service.InspectAsync(result.ProjectPath, default);
            Assert.Equal(4, snapshot.Model.Measures.Count);
            Assert.Equal("0.00%", snapshot.Model.Measures.Single(m => m.Name == "销售额 同比率").FormatString);
            repeated = await changes.PrepareAsync(period with { ProjectPath = result.ProjectPath, OutputPath = root + "-unused" }, default);
            Assert.Empty(repeated.Changes);
            changes.Discard(repeated.Id);
            var bim = Path.Combine(copies[1], "model/model.bim");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(bim))!;
            json["model"]!["tables"]![0]!["measures"]![1]!["expression"] = "123";
            File.WriteAllText(bim, json.ToJsonString());
            Assert.Equal("GENERATED_OBJECT_CONFLICT", (await Assert.ThrowsAsync<AppException>(() => changes.PrepareAsync(period with { ProjectPath = result.ProjectPath, OutputPath = root + "-unused" }, default))).Code);
        }
        finally
        {
            foreach (var path in copies)
                if (Path.GetFullPath(path).StartsWith(root + "-", StringComparison.Ordinal) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
