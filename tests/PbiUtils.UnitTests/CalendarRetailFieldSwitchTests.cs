// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarRetailFieldSwitchTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证当前空白模板生成工程切换日期字段时的冻结映射与归属.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用真实内置模板及 PBIP 读取链路, 不执行 Power BI 引擎或刷新业务数据.</summary>
public sealed class CalendarRetailFieldSwitchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-retail-calendar-" + Guid.NewGuid().ToString("N"));

    /// <summary>生成工程在文件与连接模型路径连续缩减和扩展日期字段, 预览/冻结/候选一致且归属有效.</summary>
    [Theory]
    [InlineData("blank", true)]
    [InlineData("blank", false)]
    public async Task GeneratedCalendarCanSwitchSelections(string kind, bool fileBased)
    {
        var template = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:" + kind, default);
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var full = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: [.. ScaffoldCalendarFields.All.Select(field => field.Id)]);
        var composition = await new ScaffoldModelWriter().ComposeAsync(template,
            new(1, template.Info.Id, template.Info.Baseline, "project", "zh-cn", null, null, full, null, null), project, project, null, default);
        var folder = Path.Combine(Directory.GetDirectories(project, "*.SemanticModel", SearchOption.AllDirectories).Single(), "definition");
        using var database = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        var table = database.Model.Tables[full.TableName];
        using var compiler = new ModelConfigurationCompiler();
        foreach (var selected in new[] { ScaffoldCalendarFields.Simple, ScaffoldCalendarFields.Recommended, ScaffoldCalendarFields.All.Select(field => field.Id).ToArray() })
        {
            TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);
            var snapshot = await new ProjectReader().ReadAsync(Path.Combine(project, composition.EntryFile), default);
            if (!fileBased) snapshot = snapshot with { ProjectPath = "model://calendar", Connection = new("desktop", "fixture") };
            var capability = snapshot.Model.Tables.Single(value => value.Name == table.Name).CalendarCapability!;
            Assert.Equal(["Dates"], capability.LockedFields.Select(field => field.Field));
            var expected = selected.Concat(capability.LockedFields.Select(field => field.Field)).ToHashSet(StringComparer.Ordinal);
            var options = full with { Fields = [.. selected], NumberedColumns = false };
            var preview = CalendarProgramPlanner.Preview(database, options, "zh-cn", default);
            var prepared = await compiler.PrepareAsync(new(Calendar: options), snapshot, default);
            var frozen = Assert.Single(prepared.Configuration!.CalendarOutputs);
            ModelMutation.Apply(database, new(snapshot.ProjectPath, "memory", prepared.Operation, prepared.ObjectName,
                prepared.Property, prepared.Value, Configuration: prepared.Configuration));
            var actual = ScaffoldCalendarWriter.CaptureOutput(table, new HashSet<string>(StringComparer.Ordinal)).Columns;
            Assert.Equal(expected.Order(), actual.Select(column => column.Field).Order());
            Assert.Equal(CalendarProgramPlanner.CodeHash(preview.FunctionExpression), frozen.Function!.DefinitionHash);
            Assert.Equal(CalendarProgramPlanner.CodeHash(preview.InvocationExpression), frozen.Function.InvocationHash);
            Assert.True(ModelConfigurationOwnership.IsOwned(table, table.Annotations));
            compiler.Release(prepared);
        }
    }

    /// <summary>只删除本类创建的临时模型与生成资产, 不访问用户工程.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
