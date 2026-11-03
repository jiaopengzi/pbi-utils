// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BlankTemplateFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以当前空白模板及最小自有 TOM 模型构造用户模板边界夹具.

using System.Text;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.SampleData;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.UnitTests;

/// <summary>复用当前报表绑定, 只为真实安全边界添加调用方明确拥有的模型定义.</summary>
internal static class BlankTemplateFixture
{
    /// <summary>将调用方模型序列化为独立用户模板快照, 不模拟已移除的内置来源.</summary>
    /// <param name="root">测试独占目录, 序列化材料仅位于其 fixture-source 下.</param>
    /// <param name="database">调用方拥有并负责释放的最小模型.</param>
    /// <param name="calendarRole">是否为 Calendar 表显式声明完整列映射和日期角色.</param>
    /// <returns>保留当前 blank 报表结构的冻结用户模板; TOM 序列化错误直接传播.</returns>
    public static async Task<ResolvedPbipTemplate> WithModelAsync(string root, TOM.Database database, bool calendarRole = false)
    {
        var blank = await new ScaffoldTemplateCatalog(root).GetAsync("builtin:blank", default);
        var folder = Path.Combine(root, "fixture-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        TOM.TmdlSerializer.SerializeDatabaseToFolder(database, folder);
        var files = blank.Files.Where(file => !file.RelativePath.StartsWith("blank.SemanticModel/definition/", StringComparison.Ordinal)
            && file.RelativePath != "pbi-utils.template.json").ToList();
        foreach (var path in Directory.EnumerateFiles(folder, "*.tmdl", SearchOption.AllDirectories))
            files.Add(new("blank.SemanticModel/definition/" + Path.GetRelativePath(folder, path).Replace('\\', '/'), await File.ReadAllBytesAsync(path)));
        if (calendarRole)
        {
            var table = database.Model.Tables["Calendar"];
            var descriptor = new
            {
                schemaVersion = 1,
                tables = new[]
                {
                    new
                    {
                        name = table.Name,
                        role = "calendar",
                        columns = table.Columns.OfType<TOM.DataColumn>().Select(column => new
                        {
                            name = column.Name,
                            dataType = column.DataType.ToString(),
                            sourceColumn = column.SourceColumn
                        })
                    }
                }
            };
            files.Add(new("pbi-utils.template.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(descriptor))));
        }
        return blank with { Info = blank.Info with { Id = "user:minimal-model", Name = "minimal-model", Source = "user" }, Files = files };
    }

    /// <summary>通过当前创建流程生成已有日历和样例的用户模板, 以真实资产归属验证后续范围扩展.</summary>
    /// <param name="root">测试独占的临时根.</param>
    /// <param name="options">本次已知范围及字段的日期选项.</param>
    /// <returns>目录重新核验的用户模板, 日期和已有 CSV 均具有实际创建产生的完整归属.</returns>
    public static async Task<ResolvedPbipTemplate> GeneratedCalendarAsync(string root, ScaffoldCalendarOptions options)
    {
        var catalog = new ScaffoldTemplateCatalog(root);
        var blank = await catalog.GetAsync("builtin:blank", default);
        var source = Path.Combine(root, "templates", "pbip", "calendar-source");
        Directory.CreateDirectory(source);
        var sample = new SampleDataOptions("zh-cn", 1, 1, 7, FormattableString.Invariant($"{options.StartYear:0000}-01-01"),
            FormattableString.Invariant($"{options.StartYear:0000}-04-01"), 42);
        var request = new ScaffoldRequest(1, blank.Info.Id, blank.Info.Baseline, "CalendarSource", "zh-cn", null, sample, options, null, null);
        await new ScaffoldModelWriter().ComposeAsync(blank, request, source, source, null, default);
        var entry = (await catalog.ListAsync(default)).Single(info => info.Source == "user" && info.Name == "calendar-source");
        return await catalog.GetAsync(entry.Id, default);
    }

    /// <summary>构造单个日期列和用户 M 查询的最小表, 不带产品生成或覆盖归属.</summary>
    /// <returns>拥有 Calendar 表及独立 UserFunction 表达式的数据库, 由调用方释放.</returns>
    public static TOM.Database CalendarModel()
    {
        var database = new TOM.Database { Name = "calendar-fixture", CompatibilityLevel = 1606, Model = new TOM.Model { Culture = "zh-CN" } };
        var table = new TOM.Table { Name = "Calendar" };
        table.Columns.Add(new TOM.DataColumn { Name = "Dates", SourceColumn = "Dates", DataType = TOM.DataType.DateTime });
        table.Partitions.Add(new TOM.Partition
        {
            Name = table.Name,
            Mode = TOM.ModeType.Import,
            Source = new TOM.MPartitionSource { Expression = "#table(type table [Dates = date], {{#date(2024, 1, 1)}})" }
        });
        database.Model.Tables.Add(table);
        database.Model.Expressions.Add(new TOM.NamedExpression { Name = "UserFunction", Kind = TOM.ExpressionKind.M, Expression = "() => \"用户原始查询\"" });
        return database;
    }
}
