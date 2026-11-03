// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TemplateMeasureReusedTemplateTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 重用用户模板时显式覆盖模板度量及根参数, 保留未选日期对象与源工程字节.

using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.SampleData.Model;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅在随机隔离工程中组合真实 TMDL, 不接入用户 Desktop 或执行数据查询.</summary>
public sealed class TemplateMeasureReusedTemplateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-reuse-overwrite-" + Guid.NewGuid().ToString("N"));

    /// <summary>手改且失效的模板表和根参数可按本轮请求重建, 未选日期不被改写或重新认领.</summary>
    [Theory]
    [InlineData("config", false, false, false)]
    [InlineData("tmdl", false, false, false)]
    [InlineData("config", true, false, false)]
    [InlineData("config", false, true, false)]
    [InlineData("config", false, false, true)]
    public async Task ExplicitTemplateMeasuresOverrideManualObjectsInReusedTemplate(string mode, bool missingState, bool corruptRoles, bool crossTable)
    {
        var catalog = new ScaffoldTemplateCatalog(root);
        var builtin = await catalog.GetAsync("builtin:blank", default);
        var source = Path.Combine(root, "templates", "pbip", "manual"); Directory.CreateDirectory(source);
        var initial = new ScaffoldRequest(1, builtin.Info.Id, builtin.Info.Baseline, "Original", "zh-cn",
            [Definition("old")], null, new("simple", 2024, 2024), null, null, MeasuresMode: "config");
        await new ScaffoldModelWriter().ComposeAsync(builtin, initial, source, source, null, default);
        var folder = Path.Combine(source, "pbip", "Original.SemanticModel", "definition");
        string calendarBefore;
        string calendarStamp;
        using (var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder))
        {
            database.Model.Tables["Measure"].Description = "manual parent";
            database.Model.Tables["Measure"].Measures["author"].Expression = "123";
            var config = database.Model.Tables[TemplateMeasureConfigurationStorage.DefaultTable];
            config.Description = "manual config";
            config.Columns.Remove("value");
            var parameter = database.Model.Expressions["PbiUtilsProjectRoot"];
            parameter.Expression = "\"manual root\"";
            parameter.Annotations.Remove("PBI_ResultType");
            database.Model.Tables["Calendar"].Description = "manual unrelated calendar";
            if (crossTable)
            {
                var author = database.Model.Tables["Measure"].Measures["author"];
                var moved = new TOM.Measure { Name = author.Name, Expression = author.Expression, LineageTag = author.LineageTag };
                database.Model.Tables["Measure"].Measures.Remove(author);
                database.Model.Tables["Calendar"].Measures.Add(moved);
            }
            calendarBefore = ModelConfigurationOwnership.Fingerprint(database.Model.Tables["Calendar"]);
            calendarStamp = database.Model.Tables["Calendar"].Annotations[ModelConfigurationOwnership.Stamp].Value;
            if (missingState)
            {
                database.Model.Annotations.Remove(ModelConfigurationOwnership.RolesStamp);
                database.Model.Tables["Measure"].Annotations[TemplateMeasureConfigurationStorage.Annotation].Value = "{broken";
            }
            if (corruptRoles) database.Model.Annotations[ModelConfigurationOwnership.RolesStamp].Value = "{broken";
            TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);
        }
        var info = Assert.Single(await catalog.ListAsync(default), item => item.Name == "manual");
        var template = await catalog.GetAsync(info.Id, default);
        var originals = template.Files.ToDictionary(file => file.RelativePath, file => file.Content.ToArray());
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var final = Path.Combine(root, "final");
        var request = new ScaffoldRequest(1, info.Id, info.Baseline, "Copy", "zh-cn", [Definition("next")], null, null, null, null, MeasuresMode: mode);
        await new ScaffoldModelWriter().ComposeAsync(template, request, stage, final, null, default);
        using var saved = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(stage, "pbip", "Copy.SemanticModel", "definition"));
        Assert.Equal(mode, TemplateMeasureConfigurationStorage.Read(saved.Model)!.Mode);
        if (!crossTable) Assert.Equal(calendarBefore, ModelConfigurationOwnership.Fingerprint(saved.Model.Tables["Calendar"]));
        Assert.Equal(calendarStamp, saved.Model.Tables["Calendar"].Annotations[ModelConfigurationOwnership.Stamp].Value);
        Assert.False(ModelConfigurationOwnership.IsOwned(saved.Model.Tables["Calendar"], saved.Model.Tables["Calendar"].Annotations));
        Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", Assert.Throws<AppException>(() => ScaffoldCalendarWriter.IsSelfContained(saved.Model.Tables["Calendar"])).Code);
        Assert.StartsWith(PowerQueryLiterals.Text(final), saved.Model.Expressions["PbiUtilsProjectRoot"].Expression);
        Assert.Equal(TOM.ExpressionKind.M, saved.Model.Expressions["PbiUtilsProjectRoot"].Kind);
        Assert.Equal("Text", saved.Model.Expressions["PbiUtilsProjectRoot"].Annotations["PBI_ResultType"].Value);
        if (mode == "config") Assert.NotNull(saved.Model.Tables[TemplateMeasureConfigurationStorage.DefaultTable].Columns.Find("value"));
        else Assert.Equal("\n\"next\"", saved.Model.Tables[crossTable ? "Calendar" : "Measure"].Measures["author"].Expression);
        foreach (var file in originals) Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(source, file.Key)));
    }

    /// <summary>重用入口的缺失参数表和多个旧参数角色均可自由切换, 最终映射不保留悬空或歧义条目.</summary>
    [Theory]
    [InlineData("config", false)]
    [InlineData("tmdl", false)]
    [InlineData("config", true)]
    [InlineData("tmdl", true)]
    public async Task ReusedTemplateRecoversMissingAndAmbiguousParameterRoles(string mode, bool multiple)
    {
        var catalog = new ScaffoldTemplateCatalog(root);
        var builtin = await catalog.GetAsync("builtin:blank", default);
        var source = Path.Combine(root, "templates", "pbip", "roles"); Directory.CreateDirectory(source);
        var initial = new ScaffoldRequest(1, builtin.Info.Id, builtin.Info.Baseline, "Original", "zh-cn",
            [Definition("old")], null, null, null, null, MeasuresMode: "config");
        await new ScaffoldModelWriter().ComposeAsync(builtin, initial, source, source, null, default);
        var folder = Path.Combine(source, "pbip", "Original.SemanticModel", "definition");
        using (var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder))
        {
            var ownership = ModelConfigurationOwnership.ReadRoles(database.Model);
            if (multiple) ownership.Roles["MissingConfig"] = "measure-config";
            else database.Model.Tables.Remove(TemplateMeasureConfigurationStorage.DefaultTable);
            ModelConfigurationOwnership.SetRoles(database.Model, ownership);
            TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);
        }
        // SDK 文件夹序列化不会删除旧文档, 显式模拟用户已删除参数表后的真实工程定义.
        if (!multiple) File.Delete(Path.Combine(folder, "tables", TemplateMeasureConfigurationStorage.DefaultTable + ".tmdl"));
        var info = Assert.Single(await catalog.ListAsync(default), item => item.Name == "roles");
        var template = await catalog.GetAsync(info.Id, default);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var request = initial with { TemplateId = info.Id, TemplateBaseline = info.Baseline, ProjectName = "Copy", Measures = [Definition("next")], MeasuresMode = mode };
        var composition = await new ScaffoldModelWriter().ComposeAsync(template, request, stage, Path.Combine(root, "final"), null, default);
        using var saved = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(stage, "pbip", "Copy.SemanticModel", "definition"));
        Assert.Equal(mode, TemplateMeasureConfigurationStorage.Read(saved.Model)!.Mode);
        var roles = ModelConfigurationOwnership.ReadRoles(saved.Model).Roles.Where(pair => pair.Value == "measure-config").ToArray();
        Assert.All(roles, pair => Assert.True(saved.Model.Tables.ContainsName(pair.Key)));
        if (mode == "config")
        {
            Assert.Single(roles);
            Assert.Contains("CONCATENATEX", saved.Model.Tables["Measure"].Measures["author"].Expression);
        }
        else
        {
            Assert.Empty(roles);
            Assert.Equal("\n\"next\"", saved.Model.Tables["Measure"].Measures["author"].Expression);
        }
        var context = TemplateMeasureConfigurationStorage.ReadAssetContext(saved, stage, Path.Combine(stage, composition.EntryFile), _ => Task.CompletedTask);
        Assert.Equal(roles.SingleOrDefault().Key, context.ConfigTableName);
    }

    /// <summary>构造本轮明确选择的完整六字段模板项.</summary>
    private static TemplateMeasureDefinition Definition(string value) => new("author", value, "Uncategorized", true, "", "");

    /// <summary>仅删除本测试自有随机目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
