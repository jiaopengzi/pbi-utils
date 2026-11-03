// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureConfigGenerationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证模板 Config 的限定列查值、SVG URI 保护及新建和已有模型实际 TMDL 落盘格式.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Code.Dax;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只操作自有临时工程及本地 TOM 对象, 不将排版和序列化断言视为引擎求值验收.</summary>
public sealed class TemplateMeasureConfigGenerationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-config-dax-" + Guid.NewGuid().ToString("N"));

    /// <summary>名称引号按 DAX 规则转义, value 使用限定列以免模型同名度量改变拼接含义.</summary>
    [Fact]
    public void ConfigEscapesNamesAndQualifiesValueColumn()
    {
        using var database = new TOM.Database { Name = "config", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var business = new TOM.Table { Name = "Business" };
        business.Measures.Add(new TOM.Measure { Name = "value", Expression = "999" });
        database.Model.Tables.Add(business);

        ScaffoldTemplateMeasures.Apply(database.Model, [new("测试\"键", "源文本", "WebUrl", true, "说明", "模板")], "config", "O'Brien 配置");

        var measure = database.Model.Tables["Measure"].Measures["测试\"键"];
        Assert.Contains("'O''Brien 配置'[name] = \"测试\"\"键\"", measure.Expression);
        DaxAssert.Contains("CONCATENATEX(ConfigTable, 'O''Brien 配置'[value])", measure.Expression);
        Assert.EndsWith("\nRETURN\n    Result", measure.Expression, StringComparison.Ordinal);
        Assert.Equal(DaxFormatter.FormatMeasure(measure.Expression, DaxFormattingStyle.Long), measure.Expression);
        Assert.DoesNotContain("源文本", measure.Expression);
        Assert.Equal("WebUrl", measure.DataCategory);
        Assert.Equal("999", business.Measures["value"].Expression);
    }

    /// <summary>已有表名大小写无关地占用 DAX 变量名, 重复生成须稳定避让并同步所有变量引用.</summary>
    [Theory]
    [InlineData("Uncategorized")]
    [InlineData("ImageUrl")]
    public void ConfigVariablesAvoidExistingTableNamesAndRemainStable(string category)
    {
        using var database = new TOM.Database { Name = "collisions", CompatibilityLevel = 1606, Model = new TOM.Model() };
        foreach (var name in new[] { "CONFIGTABLE", "ConfigTable1", "CONFIGVALUE", "SVGSTARTIMAGE", "SVGMATCHPOSITION", "RESULT" })
            database.Model.Tables.Add(new TOM.Table { Name = name });
        TemplateMeasureDefinition[] definitions = [new("template", "<svg><path/></svg>", category, true, "", "")];

        ScaffoldTemplateMeasures.Apply(database.Model, definitions, "config");

        var expression = database.Model.Tables["Measure"].Measures["template"].Expression;
        DaxAssert.Contains("VAR ConfigTable2 = FILTER(", expression);
        DaxAssert.Contains("VAR ConfigValue1 = CONCATENATEX(ConfigTable2, 'PbiUtilsTemplateMeasures'[value])", expression);
        Assert.DoesNotMatch("(?im)^VAR (CONFIGTABLE|ConfigTable1|CONFIGVALUE|SVGSTARTIMAGE|SVGMATCHPOSITION|RESULT) =", expression);
        if (category == "ImageUrl")
        {
            DaxAssert.Contains("VAR SvgStartImage1 = \"data:image/svg+xml;utf8,\"", expression);
            DaxAssert.Contains("VAR SvgMatchPosition1 = COALESCE(SEARCH(\"<svg*</svg>\", ConfigValue1, 1, BLANK()), SEARCH(\"<svg*/>\", ConfigValue1, 1, BLANK()))", expression);
            DaxAssert.Contains("VAR Result1 = IF(", expression);
            DaxAssert.Contains("ISBLANK(SvgMatchPosition1)", expression);
            DaxAssert.Contains("LEFT(ConfigValue1, 5)", expression);
            DaxAssert.Contains("SvgStartImage1 & ConfigValue1", expression);
            Assert.EndsWith("\nRETURN\n    Result1", expression, StringComparison.Ordinal);
        }
        else Assert.EndsWith("\nRETURN\n    Result1", expression, StringComparison.Ordinal);

        ScaffoldTemplateMeasures.Apply(database.Model, definitions, "config");

        Assert.Equal(expression, database.Model.Tables["Measure"].Measures["template"].Expression);
    }

    /// <summary>角色指定的参数表晚于表达式创建, 首次生成也须预留其名称并在后续调用保持变量名稳定.</summary>
    [Fact]
    public void ConfigVariablesReserveMissingParameterRoleTableName()
    {
        using var database = new TOM.Database { Name = "role", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var roles = new Dictionary<string, string>(StringComparer.Ordinal) { ["ConfigTable"] = "measure-config" };
        TemplateMeasureDefinition[] definitions = [new("author", "Jane", "Uncategorized", true, "", "")];
        Assert.False(database.Model.Tables.ContainsName("ConfigTable"));

        ScaffoldTemplateMeasureMode.Apply(database.Model, definitions, "config", roles, configuration: true);

        var expression = database.Model.Tables["Measure"].Measures["author"].Expression;
        Assert.True(database.Model.Tables.ContainsName("ConfigTable"));
        DaxAssert.Contains("VAR ConfigTable1 = FILTER('ConfigTable', 'ConfigTable'[name] = \"author\")", expression);
        DaxAssert.Contains("CONCATENATEX(ConfigTable1, 'ConfigTable'[value])", expression);
        Assert.DoesNotContain("VAR ConfigTable =", expression);

        ScaffoldTemplateMeasureMode.Apply(database.Model, definitions, "config", roles, configuration: true);

        Assert.Equal(expression, database.Model.Tables["Measure"].Measures["author"].Expression);
    }

    /// <summary>Config 图像保留完整 SVG 搜索并在拼接前保留 data URI, 用户值始终来自参数表.</summary>
    [Theory]
    [InlineData("<svg><path/></svg>")]
    [InlineData("data:image/svg+xml;utf8,<svg><path/></svg>")]
    [InlineData("https://example.com/logo.svg")]
    [InlineData("ordinary image text")]
    public void ConfigImageUsesSvgSearchAndDataUriGuard(string value)
    {
        using var database = new TOM.Database { Name = "image", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var definition = new TemplateMeasureDefinition("logo", value, "ImageUrl", true, "", "");

        ScaffoldTemplateMeasures.Apply(database.Model, [definition], "config");

        var expression = database.Model.Tables["Measure"].Measures["logo"].Expression;
        Assert.StartsWith("\nVAR SvgStartImage = \"data:image/svg+xml;utf8,\"\n", expression, StringComparison.Ordinal);
        DaxAssert.Contains("SEARCH(\"<svg*</svg>\", ConfigValue, 1, BLANK())", expression);
        DaxAssert.Contains("LEFT(ConfigValue, 5) = \"data:\"", expression);
        DaxAssert.Contains("ConfigValue, SvgStartImage & ConfigValue)", expression);
        Assert.EndsWith("\nRETURN\n    Result", expression, StringComparison.Ordinal);
        Assert.Equal(DaxFormatter.FormatMeasure(expression, DaxFormattingStyle.Long), expression);
        Assert.Equal(value, definition.Value);
    }

    /// <summary>Config 同时识别成对结束和自闭合 SVG, 裸自闭合根及带属性根均可补齐图像前缀.</summary>
    [Theory]
    [InlineData("<svg/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1' />")]
    public void ConfigImageRecognizesSelfClosingSvgAlongsidePairedSvg(string value)
    {
        using var database = new TOM.Database { Name = "self-closing-image", CompatibilityLevel = 1606, Model = new TOM.Model() };

        ScaffoldTemplateMeasures.Apply(database.Model, [new("logo", value, "ImageUrl", true, "", "")], "config");

        var expression = database.Model.Tables["Measure"].Measures["logo"].Expression;
        DaxAssert.Contains("VAR SvgMatchPosition = COALESCE(SEARCH(\"<svg*</svg>\", ConfigValue, 1, BLANK()), SEARCH(\"<svg*/>\", ConfigValue, 1, BLANK()))", expression);
        DaxAssert.Contains("ISBLANK(SvgMatchPosition)", expression);
        DaxAssert.Contains("ConfigValue, SvgStartImage & ConfigValue)", expression);
    }

    /// <summary>图像表达式不声明被真实 Desktop 引擎拒绝的 Status 变量, 仍通过完整 SVG 搜索决定前缀.</summary>
    [Fact]
    public void ConfigImageDoesNotDeclareReservedStatusVariable()
    {
        using var database = new TOM.Database { Name = "safe-image-variable", CompatibilityLevel = 1606, Model = new TOM.Model() };

        ScaffoldTemplateMeasures.Apply(database.Model, [new("logo", "<svg></svg>", "ImageUrl", true, "", "")], "config");

        var expression = database.Model.Tables["Measure"].Measures["logo"].Expression;
        Assert.DoesNotMatch("(?im)^\\s*VAR Status\\s*=", expression);
        DaxAssert.Contains("VAR SvgMatchPosition = COALESCE(SEARCH(\"<svg*</svg>\", ConfigValue, 1, BLANK()), SEARCH(\"<svg*/>\", ConfigValue, 1, BLANK()))", expression);
        DaxAssert.Contains("ISBLANK(SvgMatchPosition)", expression);
    }

    /// <summary>HTTP(S) 图像地址即使含 SVG 字样也必须返回完整原地址, 不进入 SVG data URI 拼接分支.</summary>
    [Fact]
    public void ConfigImageGuardsHttpAndHttpsValuesFromSvgPrefix()
    {
        using var database = new TOM.Database { Name = "image-url", CompatibilityLevel = 1606, Model = new TOM.Model() };

        ScaffoldTemplateMeasures.Apply(database.Model, [new("logo", "https://example.com/image?svg=<svg></svg>", "ImageUrl", true, "", "")], "config");

        var expression = database.Model.Tables["Measure"].Measures["logo"].Expression;
        DaxAssert.Contains("|| LEFT(ConfigValue, 7) = \"http://\"", expression);
        DaxAssert.Contains("|| LEFT(ConfigValue, 8) = \"https://\"", expression);
        DaxAssert.Contains("ConfigValue, SvgStartImage & ConfigValue)", expression);
    }

    /// <summary>字符串模式允许完整 XML 声明和注释前导的裸 SVG, 保留已有 URI、无效前导和普通文本.</summary>
    [Theory]
    [InlineData("<svg><path/></svg>", "data:image/svg+xml;utf8,<svg><path/></svg>")]
    [InlineData("<!-- logo -->\n<svg><path/></svg>", "data:image/svg+xml;utf8,<!-- logo -->\n<svg><path/></svg>")]
    [InlineData("<?xml version='1.0'?>\n<svg></svg>", "data:image/svg+xml;utf8,<?xml version='1.0'?>\n<svg></svg>")]
    [InlineData("<?xml version='1.0'?>\n<!-- logo -->\n<svg/>", "data:image/svg+xml;utf8,<?xml version='1.0'?>\n<!-- logo -->\n<svg/>")]
    [InlineData("data:image/svg+xml;utf8,<svg><path/></svg>", "data:image/svg+xml;utf8,<svg><path/></svg>")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", "data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("http://example.com/logo.png", "http://example.com/logo.png")]
    [InlineData("https://example.com/logo.svg", "https://example.com/logo.svg")]
    [InlineData("https://example.com/image?svg=<svg></svg>", "https://example.com/image?svg=<svg></svg>")]
    [InlineData("<!-- unclosed <svg></svg>", "<!-- unclosed <svg></svg>")]
    [InlineData("<?xml missing terminator <svg></svg>", "<?xml missing terminator <svg></svg>")]
    [InlineData("<!-- <svg></svg> -->", "<!-- <svg></svg> -->")]
    [InlineData("<!-- logo --><div><svg></svg></div>", "<!-- logo --><div><svg></svg></div>")]
    [InlineData("plain <svg></svg>", "plain <svg></svg>")]
    [InlineData("ordinary image text", "ordinary image text")]
    public void TmdlImageRetainsUriAndPrefixesOnlyRawSvg(string value, string expected)
    {
        using var database = new TOM.Database { Name = "image", CompatibilityLevel = 1606, Model = new TOM.Model() };

        ScaffoldTemplateMeasures.Apply(database.Model, [new("logo", value, "ImageUrl", true, "", "")]);

        Assert.Equal("\n\"" + expected + "\"", database.Model.Tables["Measure"].Measures["logo"].Expression);
    }

    /// <summary>模板值允许 32768 字符, Config 格式化不把用户值送入 30000 字符限制的编辑器格式化入口.</summary>
    [Theory]
    [InlineData("config")]
    [InlineData("tmdl")]
    public void MaximumTemplateValueRemainsSupported(string mode)
    {
        using var database = new TOM.Database { Name = "maximum", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var value = new string('a', 32768);

        ScaffoldTemplateMeasures.Apply(database.Model, [new("maximum", value, "Uncategorized", true, "", "")], mode);

        var expression = database.Model.Tables["Measure"].Measures["maximum"].Expression;
        if (mode == "tmdl") Assert.Equal("\n\"" + value + "\"", expression);
        else
        {
            DaxAssert.Contains("CONCATENATEX", expression);
            Assert.DoesNotContain(value, expression);
        }
    }

    /// <summary>Config 的度量正文实际保存前导 LF 和顶格正文, TMDL 重开后定义头与 VAR 仍位于不同行.</summary>
    [Fact]
    public void ConfigMeasureDefinitionHeadStaysSeparateAfterTmdlRoundTrip()
    {
        using var database = new TOM.Database { Name = "definition", CompatibilityLevel = 1606, Model = new TOM.Model() };
        ScaffoldTemplateMeasures.Apply(database.Model, [new("version", "1.0", "Uncategorized", true, "", "config")], "config");
        var expected = database.Model.Tables["Measure"].Measures["version"].Expression;
        Assert.StartsWith("\nVAR ConfigTable =\n", expected, StringComparison.Ordinal);
        var folder = Path.Combine(root, "definition-head");
        TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);

        using var reopened = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        var expression = reopened.Model.Tables["Measure"].Measures["version"].Expression;
        Assert.Equal(expected, expression);
        Assert.StartsWith("version =\nVAR ConfigTable =\n", "version =" + expression, StringComparison.Ordinal);
        Assert.Equal(DaxFormatter.FormatMeasure(expression, DaxFormattingStyle.Long), expression);
    }

    /// <summary>32768 字符常量仍可生成并序列化重开, 字符串内部的 LF 和引号不因代码缩进改变.</summary>
    [Fact]
    public void MaximumLiteralWithEmbeddedLineFeedKeepsValueAfterTmdlRoundTrip()
    {
        using var database = new TOM.Database { Name = "literal", CompatibilityLevel = 1606, Model = new TOM.Model() };
        var value = new string('a', 32766) + "\n\"";
        ScaffoldTemplateMeasures.Apply(database.Model, [new("large", value, "Uncategorized", true, "", "")]);
        var expected = "\n" + ScaffoldTemplateMeasures.Literal(value);
        Assert.Equal(expected, database.Model.Tables["Measure"].Measures["large"].Expression);
        var folder = Path.Combine(root, "large-literal");
        TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);

        using var reopened = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        Assert.Equal(expected, reopened.Model.Tables["Measure"].Measures["large"].Expression);
        Assert.Contains("\n\"\"\"", reopened.Model.Tables["Measure"].Measures["large"].Expression);
    }

    /// <summary>当前空白模板工程的新建结果实际写出多行 Config DAX, TOM 回读保持已格式化定义及用户元数据.</summary>
    [Theory]
    [InlineData("blank")]
    public async Task CreationPersistsFormattedConfigDaxInTmdl(string kind)
    {
        var (entry, folder) = await CreateProject(kind, "config");
        Assert.True(File.Exists(entry));

        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);

        AssertFormattedFile(database.Model, folder);
    }

    /// <summary>已有 TMDL 工程走共享配置确认和离线文件事务, 新建及覆盖的普通文本和 SVG 度量均保存为本地排版结果.</summary>
    [Fact]
    public async Task ModelConfigurationSavePersistsFormattedConfigDaxInTmdl()
    {
        var (entry, folder) = await CreateProject("blank", "tmdl");
        var reader = new ProjectReader();
        var snapshot = await reader.ReadAsync(entry, default);
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var edits = new ModelEditService(reader, workspace);
        using var compiler = new ModelConfigurationCompiler();
        edits.Select(snapshot);
        var source = await compiler.ReadAssetContextAsync(snapshot, default);
        var definitions = Definitions();
        definitions = [.. definitions, new("new value", "new source", "Uncategorized", true, "", "")];
        var prepared = await compiler.PrepareAsync(new(Measures: definitions, MeasuresMode: "config", MeasureConfigurationRevision: source.ConfigurationRevision), snapshot, default);
        try
        {

            snapshot = await edits.EditConfigurationAndSaveAsync(prepared, default, beforeCommit: token => compiler.VerifyAsync(prepared, token));

            using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
            AssertFormattedFile(database.Model, folder);
            var added = database.Model.Tables["Measure"].Measures["new value"].Expression;
            Assert.Equal(DaxFormatter.FormatMeasure(added, DaxFormattingStyle.Long), added);
            DaxAssert.Contains("CONCATENATEX", added);
            Assert.Equal(added, snapshot.Model.Measures.Single(measure => measure.Name == "new value").Expression);
        }
        finally { compiler.Release(prepared); }
    }

    /// <summary>冻结和实际变更共用同批将新增日期表的完整名称集合, 不因先生成度量而写出与日期表同名的 VAR.</summary>
    [Fact]
    public async Task ConfigurationReservesCalendarTableCreatedLaterInSameBatch()
    {
        var (entry, folder) = await CreateProject("blank", "tmdl");
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        using var compiler = new ModelConfigurationCompiler();
        var source = await compiler.ReadAssetContextAsync(snapshot, default);
        var request = new ModelConfigurationRequest(Measures: Definitions(), MeasuresMode: "config",
            Calendar: new("simple", 2024, 2024, "ConfigTable"), MeasureConfigurationRevision: source.ConfigurationRevision);
        var prepared = await compiler.PrepareAsync(request, snapshot, default);
        try
        {
            using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
            Assert.False(database.Model.Tables.ContainsName("ConfigTable"));

            ModelMutation.Apply(database, new(entry, "memory", prepared.Operation, prepared.ObjectName, prepared.Property, prepared.Value, Configuration: prepared.Configuration));

            Assert.True(database.Model.Tables.ContainsName("ConfigTable"));
            foreach (var definition in Definitions())
            {
                var expression = database.Model.Tables["Measure"].Measures[definition.Name].Expression;
                DaxAssert.Contains("VAR ConfigTable1 = FILTER(", expression);
                DaxAssert.Contains("CONCATENATEX(ConfigTable1,", expression);
                Assert.DoesNotContain("VAR ConfigTable =", expression);
                Assert.Equal(DaxFormatter.FormatMeasure(expression, DaxFormattingStyle.Long), expression);
            }
        }
        finally { compiler.Release(prepared); }
    }

    /// <summary>在随机目录组合真实内置模板, 返回其 PBIP 入口和 TOM 定义目录; 组合失败直接传播.</summary>
    private async Task<(string Entry, string Folder)> CreateProject(string kind, string mode)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "Formatted", "zh-cn", Definitions(), null, null, null, null, MeasuresMode: mode),
            project, project, null, default);
        return (Path.Combine(project, composition.EntryFile), Path.Combine(project, "pbip", "Formatted.SemanticModel", "definition"));
    }

    /// <summary>提供需转义的普通模板键与裸 SVG, 覆盖两种生成分支并保留六字段业务配置.</summary>
    private static TemplateMeasureDefinition[] Definitions() =>
        [new("a\"b", "raw \"value\"", "Uncategorized", true, "说明", "folder"), new("logo", "<svg><path/></svg>", "ImageUrl", true, "图像", "images")];

    /// <summary>直接核实磁盘 TMDL 的多行变量和 TOM 回读表达式, 不把序列化器成功当作已格式化.</summary>
    private static void AssertFormattedFile(TOM.Model model, string folder)
    {
        var text = File.ReadAllText(Path.Combine(folder, "tables", "Measure.tmdl"));
        Assert.Matches("(?m)^\\s+VAR ConfigTable =\\r?\\n\\s+FILTER \\(", text);
        Assert.Matches("(?m)^\\s+VAR ConfigValue =\\r?\\n\\s+CONCATENATEX \\(", text);
        Assert.Matches("(?m)^\\s+RETURN\\s*$", text);
        foreach (var definition in Definitions())
        {
            var measure = model.Tables["Measure"].Measures[definition.Name];
            Assert.StartsWith("\nVAR ", measure.Expression, StringComparison.Ordinal);
            Assert.Equal(DaxFormatter.FormatMeasure(measure.Expression, DaxFormattingStyle.Long), measure.Expression);
            Assert.Contains("\nVAR ConfigValue =\n", measure.Expression);
            Assert.DoesNotContain("\n\nVAR", measure.Expression);
            Assert.DoesNotContain("LOOKUPVALUE", measure.Expression);
            Assert.Equal(definition.Description, measure.Description);
            Assert.Equal(definition.DisplayFolder, measure.DisplayFolder);
        }
    }

    /// <summary>只清理本类自建的随机夹具目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
