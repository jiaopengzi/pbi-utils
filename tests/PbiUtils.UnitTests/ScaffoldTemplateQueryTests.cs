// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldTemplateQueryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证可选固定 M 查询的真实组合、命名冲突和严格输入边界.

using PbiUtils.Infrastructure.Scaffolding.Templates;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>执行真实 staging 组合及 TOM 回读, 不把元数据解析视为 M 计算.</summary>
public sealed class ScaffoldTemplateQueryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-template-query-" + Guid.NewGuid().ToString("N"));

    /// <summary>当前空白来源支持四项选择, 仅新增显式辅助表/查询并保持报表字节.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task SelectedQueriesRoundTripThroughEveryBuiltin(string kind)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        using var original = ReadSource(template);
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        var request = Request(template, "[\"Hour24\",\"MQHY\",\"RefreshTime\",\"TypeDetails\"]");
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
        foreach (var name in new[] { "Hour24", "MQHY", "RefreshTime" })
        {
            Assert.Null(database.Model.Expressions.Find(name));
            var table = database.Model.Tables[name];
            var partition = Assert.Single(table.Partitions);
            Assert.Equal(TOM.ModeType.Import, partition.Mode);
            Assert.Equal("model", partition.QueryGroup?.Folder);
            Assert.Equal("Table", table.Annotations.Find("PBI_ResultType")?.Value);
            var code = Assert.IsType<TOM.MPartitionSource>(partition.Source).Expression;
            Assert.DoesNotContain("File.Contents", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Web.Contents", code, StringComparison.Ordinal);
        }
        var expression = database.Model.Expressions["TypeDetails"];
        Assert.Equal(TOM.ExpressionKind.M, expression.Kind);
        Assert.Equal("config", expression.QueryGroup?.Folder);
        Assert.Equal("Table", expression.Annotations.Find("PBI_ResultType")?.Value);
        Assert.Contains("{1 .. 24}", Assert.IsType<TOM.MPartitionSource>(database.Model.Tables["Hour24"].Partitions[0].Source).Expression, StringComparison.Ordinal);
        Assert.Contains("{1 .. 19}", Assert.IsType<TOM.MPartitionSource>(database.Model.Tables["MQHY"].Partitions[0].Source).Expression, StringComparison.Ordinal);
        Assert.Contains("{11, \"二进制\", type binary}", database.Model.Expressions["TypeDetails"].Expression, StringComparison.Ordinal);
        Assert.Empty(original.Model.Expressions);
        Assert.Equal(["Hour24", "MQHY", "RefreshTime"], database.Model.Tables.Select(table => table.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["PbiUtilsProjectRoot", "TypeDetails"], database.Model.Expressions.Select(expression => expression.Name).Order(StringComparer.Ordinal));
        foreach (var file in template.Files.Where(file => file.RelativePath.Contains(".Report/", StringComparison.Ordinal)
            && !file.RelativePath.EndsWith("/definition.pbir", StringComparison.Ordinal)))
            Assert.Equal(file.Content, await File.ReadAllBytesAsync(Path.Combine(stage, "pbip", "project.Report",
                file.RelativePath[(file.RelativePath.IndexOf(".Report/", StringComparison.Ordinal) + ".Report/".Length)..])));
    }

    /// <summary>空白未选择查询时只保留既有工程根参数, 不隐式增加辅助查询或表.</summary>
    [Fact]
    public async Task EmptySelectionDoesNotAddQueries()
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, Request(template, "[]"), stage, Path.Combine(root, "output"), null, default);
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
        Assert.Equal(["PbiUtilsProjectRoot"], database.Model.Expressions.Select(expression => expression.Name));
        Assert.Empty(database.Model.Tables);
    }

    /// <summary>任意用户同名表、表达式或其他表内的 M 分区均拒绝, 不以同名证明覆盖权.</summary>
    [Theory]
    [InlineData("expression", "Hour24")]
    [InlineData("table", "Hour24")]
    [InlineData("partition", "Hour24")]
    [InlineData("expression", "RefreshTime")]
    [InlineData("table", "RefreshTime")]
    [InlineData("partition", "RefreshTime")]
    public async Task UserQueryOrTableConflictFailsBeforePublishing(string kind, string query)
    {
        using var database = new TOM.Database { ID = "fixture", Name = "fixture", CompatibilityLevel = 1606, Model = new TOM.Model { Name = "fixture" } };
        if (kind == "table") database.Model.Tables.Add(new TOM.Table { Name = query.ToLowerInvariant() });
        else if (kind == "partition")
        {
            var other = new TOM.Table { Name = "Other" };
            other.Columns.Add(new TOM.DataColumn { Name = "Value", SourceColumn = "Value", DataType = TOM.DataType.String });
            other.Partitions.Add(new TOM.Partition { Name = query.ToLowerInvariant(), Mode = TOM.ModeType.Import, Source = new TOM.MPartitionSource { Expression = "#table(type table [Value = text], {})" } });
            database.Model.Tables.Add(other);
        }
        else database.Model.Expressions.Add(new TOM.NamedExpression { Name = query.ToLowerInvariant(), Kind = TOM.ExpressionKind.M, Expression = "#table({}, {})" });
        var template = UserTemplate(database);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var error = await Assert.ThrowsAsync<AppException>(() => new ScaffoldModelWriter().ComposeAsync(template, Request(template, JsonSerializer.Serialize(new[] { query })), stage, Path.Combine(root, "output"), null, default));
        Assert.Equal("SCAFFOLD_OBJECT_CONFLICT", error.Code);
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
    }

    /// <summary>白名单段拒绝未知、大小写替身、重复和超限, 不接受任意 M 或路径.</summary>
    [Theory]
    [InlineData("[\"Unknown\"]")]
    [InlineData("[\"hour24\"]")]
    [InlineData("[\"Hour24\",\"Hour24\"]")]
    [InlineData("[null]")]
    [InlineData("[\"Hour24\",\"MQHY\",\"TypeDetails\",\"Hour24\"]")]
    public void InvalidSelectionIsRejected(string selection)
    {
        var template = UserTemplate(new TOM.Database { ID = "fixture", Name = "fixture", CompatibilityLevel = 1606, Model = new TOM.Model { Name = "fixture" } });
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request(template, selection))).Code);
    }

    /// <summary>省略映射、明确 null 和空映射均保持旧协议的 model/config 默认分组.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task GroupingDefaultsRemainCompatible(string groupsJson)
    {
        using var database = await ComposeGroupsAsync(JsonSerializer.Deserialize<Dictionary<string, string>>(groupsJson));
        Assert.Equal("model", database.Model.Tables["Hour24"].Partitions[0].QueryGroup?.Folder);
        Assert.Equal("model", database.Model.Tables["MQHY"].Partitions[0].QueryGroup?.Folder);
        Assert.Equal("config", database.Model.Expressions["TypeDetails"].QueryGroup?.Folder);
    }

    /// <summary>自定义中文、多项同组和明确空组通过真实 TMDL 回读, 缺键仍使用目录默认值.</summary>
    [Theory]
    [InlineData("{\"Hour24\":\"小时维度\"}", "小时维度", "model", "config")]
    [InlineData("{\"Hour24\":\"共享维度\",\"MQHY\":\"共享维度\",\"TypeDetails\":\"共享维度\"}", "共享维度", "共享维度", "共享维度")]
    [InlineData("{\"Hour24\":\"\",\"TypeDetails\":\"\"}", null, "model", null)]
    [InlineData("{\"Hour24\":\" 维度 \"}", " 维度 ", "model", "config")]
    public async Task GroupOverridesRoundTripThroughTmdl(string groupsJson, string? hourGroup, string? periodGroup, string? typeGroup)
    {
        using var database = await ComposeGroupsAsync(JsonSerializer.Deserialize<Dictionary<string, string>>(groupsJson));
        Assert.Equal(hourGroup, database.Model.Tables["Hour24"].Partitions[0].QueryGroup?.Folder);
        Assert.Equal(periodGroup, database.Model.Tables["MQHY"].Partitions[0].QueryGroup?.Folder);
        Assert.Equal(typeGroup, database.Model.Expressions["TypeDetails"].QueryGroup?.Folder);
        Assert.DoesNotContain(database.Model.QueryGroups, group => group.Folder.Length == 0);
        if (hourGroup == "共享维度") Assert.Single(database.Model.QueryGroups, group => group.Folder == "共享维度");
    }

    /// <summary>映射保留未选择的草稿, 已有用户查询不因该映射被改组或改写.</summary>
    [Fact]
    public void GroupMapDoesNotModifyUnselectedQueries()
    {
        using var database = new TOM.Database { Name = "unselected-query", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var group = new TOM.QueryGroup { Folder = "config" }; database.Model.QueryGroups.Add(group);
        const string original = "let Source = \"用户原始值\" in Source";
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "TypeDetails", Kind = TOM.ExpressionKind.M, Expression = original, QueryGroup = group });
        ScaffoldTemplateQueryWriter.Apply(database.Model, ["Hour24"], false, new Dictionary<string, string> { ["TypeDetails"] = "" });
        Assert.Equal(original, database.Model.Expressions["TypeDetails"].Expression);
        Assert.Same(group, database.Model.Expressions["TypeDetails"].QueryGroup);
        Assert.Equal("model", database.Model.Tables["Hour24"].Partitions[0].QueryGroup?.Folder);
    }

    /// <summary>大小写不同的映射复用已有组, 用户查询的定义及成员关系保持.</summary>
    [Fact]
    public void ExistingGroupIsReusedWithoutRenamingOrChangingOtherMembers()
    {
        using var database = new TOM.Database { Name = "existing-query-group", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var group = new TOM.QueryGroup { Folder = "model" }; database.Model.QueryGroups.Add(group);
        const string original = "#table(type table [UserValue = text], {{\"用户原文\"}})";
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "UserQuery", Kind = TOM.ExpressionKind.M, Expression = original, QueryGroup = group });
        ScaffoldTemplateQueryWriter.Apply(database.Model, ["Hour24"], false, new Dictionary<string, string> { ["Hour24"] = "MODEL" });
        Assert.Same(group, database.Model.Tables["Hour24"].Partitions[0].QueryGroup);
        Assert.Single(database.Model.QueryGroups, value => value.Folder.Equals("model", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(original, database.Model.Expressions["UserQuery"].Expression);
        Assert.Same(group, database.Model.Expressions["UserQuery"].QueryGroup);
    }

    /// <summary>组名的字符数上限包含第 200 个中文字符, 不误按 UTF-8 字节数缩短.</summary>
    [Fact]
    public async Task MaximumLengthChineseGroupRoundTrips()
    {
        var group = new string('组', 200);
        using var database = await ComposeGroupsAsync(new() { ["Hour24"] = group }, ["Hour24"]);
        Assert.Equal(group, database.Model.Tables["Hour24"].Partitions[0].QueryGroup?.Folder);
    }

    /// <summary>字典只接受标准键和有效字符串, 不用默认值掩盖无效映射.</summary>
    [Theory]
    [InlineData("{\"Unknown\":\"group\"}")]
    [InlineData("{\"hour24\":\"group\"}")]
    [InlineData("{\"Hour24\":null}")]
    [InlineData("{\"Hour24\":\" \"}")]
    [InlineData("{\"Hour24\":\"　\"}")]
    [InlineData("{\"Hour24\":\"group\\nname\"}")]
    [InlineData("{\"Hour24\":\"group\\tname\"}")]
    [InlineData("{\"Hour24\":\"group\\u0000name\"}")]
    [InlineData("{\"Hour24\":\"one\",\"MQHY\":\"two\",\"TypeDetails\":\"three\",\"Other\":\"four\"}")]
    public void InvalidGroupMapIsRejected(string groupsJson)
    {
        var groups = JsonSerializer.Deserialize<Dictionary<string, string>>(groupsJson);
        var request = new ScaffoldRequest(1, "builtin:blank", new string('a', 64), "project", "zh-cn", null, null, null, null, null)
        { TemplateQueryGroups = groups };
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => ScaffoldValidation.Validate(request)).Code);
    }

    /// <summary>超过上限一字符的组名在任何文件准备前拒绝.</summary>
    [Fact]
    public void OverlongGroupNameIsRejected()
    {
        var request = new ScaffoldRequest(1, "builtin:blank", new string('a', 64), "project", "zh-cn", null, null, null, null, null)
        { TemplateQueryGroups = new() { ["Hour24"] = new string('x', 201) } };
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => ScaffoldValidation.Validate(request)).Code);
    }

    /// <summary>真实生成并回读四个标准辅助表/查询, 只向测试拥有的 staging 写入.</summary>
    /// <param name="groups">可选分组覆盖, null 使用默认分组.</param>
    /// <param name="selected">显式选择, 省略时选择全部四项.</param>
    /// <returns>调用方负责释放的实际输出数据库.</returns>
    private async Task<TOM.Database> ComposeGroupsAsync(Dictionary<string, string>? groups, string[]? selected = null)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var request = Request(template, "[\"Hour24\",\"MQHY\",\"RefreshTime\",\"TypeDetails\"]") with { TemplateQueryGroups = groups };
        if (selected is not null) request = request with { TemplateQueries = selected };
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "output"), null, default);
        return TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Directory.GetDirectories(stage, "*.SemanticModel", SearchOption.AllDirectories).Single() + "/definition");
    }

    /// <summary>通过公开请求提交固定 ID 数组, 非法元素交给真实共享校验器处理.</summary>
    private static ScaffoldRequest Request(ResolvedPbipTemplate template, string selection)
    {
        return new ScaffoldRequest(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn", null, null, null, null, null)
        { TemplateQueries = JsonSerializer.Deserialize<string[]>(selection)! };
    }

    /// <summary>将冻结内置定义字节复制到测试独占目录, 用官方 TOM 读取原查询基线.</summary>
    /// <param name="template">已经目录核验的内置模板快照.</param>
    /// <returns>调用方负责释放的完整源模型; 缺少合法定义时直接使测试失败.</returns>
    private TOM.Database ReadSource(ResolvedPbipTemplate template)
    {
        var definitionFile = template.Files.First(file => file.RelativePath.Contains(".SemanticModel/definition/", StringComparison.Ordinal)).RelativePath;
        var prefix = definitionFile[..(definitionFile.IndexOf("/definition/", StringComparison.Ordinal) + "/definition/".Length)];
        var source = Path.Combine(root, "source");
        foreach (var file in template.Files.Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var path = Path.Combine(source, file.RelativePath[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Content);
        }
        return TOM.TmdlSerializer.DeserializeDatabaseFromFolder(source);
    }

    /// <summary>最小自包含用户模板只包含测试拥有的模型, 不依赖原资源或真实 Desktop.</summary>
    private static ResolvedPbipTemplate UserTemplate(TOM.Database database) => new(
        new("user:fixture", "fixture", "user", new string('a', 64), "", []), "project.pbip",
        [new("project.pbip", Encoding.UTF8.GetBytes("{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"report\"}}]}")),
            new("report/definition.pbir", Encoding.UTF8.GetBytes("{\"version\":\"4.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../model\"}}}")),
            new("report/definition/report.json", Encoding.UTF8.GetBytes("{}")),
            new("report/definition/pages/pages.json", Encoding.UTF8.GetBytes("{\"pageOrder\":[]}")),
            new("model/definition.pbism", Encoding.UTF8.GetBytes("{\"version\":\"1.0\"}")),
            new("model/model.bim", Encoding.UTF8.GetBytes(TOM.JsonSerializer.SerializeDatabase(database)))]);

    /// <summary>仅删除本测试分配的临时目录, 不影响用户模板或项目.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
