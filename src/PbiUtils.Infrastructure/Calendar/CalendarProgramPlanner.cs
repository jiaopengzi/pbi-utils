// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\CalendarProgramPlanner.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在完整受信模型上规划日期函数与真实源列映射, 不执行模型或资产写入.

using System.Text;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>预览与实际写入共享列规划和函数名称空间规则.</summary>
public static class CalendarProgramPlanner
{
    /// <summary>在原模型上只读规划精确列映射; 内置模板使用纯字段投影, 用户目标须通过完整归属.</summary>
    public static ScaffoldCalendarProgram Preview(TOM.Database database, ScaffoldCalendarOptions options, string locale,
        CancellationToken token, bool freshBuiltin = false, bool trustedTemplateRole = false)
    {
        token.ThrowIfCancellationRequested();
        var model = database.Model;
        RequireTableName(model, options.TableName);
        var table = model.Tables.Find(options.TableName);
        if (table is not null)
        {
            if (!(freshBuiltin && table.Name == "Calendar") && !trustedTemplateRole)
            {
                ModelConfigurationOwnership.Require(table, table.Annotations);
                RequireCurrentStorage(table);
            }
        }
        var prior = table is null ? [] : freshBuiltin && table.Name == "Calendar" ? BuiltinColumns(table, options)
            : ScaffoldCalendarWriter.CaptureOutput(table, new HashSet<string>(StringComparer.Ordinal)).Columns;
        var mapping = ScaffoldCalendarWriter.PlanColumns(options, prior,
            requiredFields: table?.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null ? CalendarColumnProtection.RequiredFields(table) : null);
        if (table?.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null)
            _ = CalendarColumnRename.Prepare(table, options, prior, mapping, token);
        var binding = table is null ? null : ScaffoldCalendarWriter.FunctionBinding(table);
        var program = ScaffoldCalendarM.Compile(options, mapping.Select(value => new ScaffoldCalendarColumn(value.SourceColumn, value.Field)).ToArray(),
            locale, cancellationToken: token);
        RequireName(model, program.FunctionName, table, binding);
        RequireSingleBinding(model, table, binding, token);
        return program;
    }

    /// <summary>只读推导内置模板首次裁剪后的输出, 保持原列、排序、镜像和业务元数据完整不变.</summary>
    /// <param name="table">调用者已核验身份的内置 Calendar 表.</param>
    /// <param name="options">本轮字段及编号选择.</param>
    /// <returns>与正式模板形状生成器一致的源列映射, 不改写任何 SDK 对象.</returns>
    private static ModelConfigurationCalendarColumn[] BuiltinColumns(TOM.Table table, ScaffoldCalendarOptions options)
    {
        var fields = ScaffoldCalendarFields.Resolve(options).Select(value => value.Field).ToHashSet(StringComparer.Ordinal);
        return [.. table.Columns.OfType<TOM.DataColumn>().Select(column => ScaffoldCalendarWriter.Semantic(column.Name))
            .Where(fields.Contains).Select(field =>
            {
                var name = ScaffoldCalendarFields.ColumnName(field, options.NumberedColumns ?? true);
                return new ModelConfigurationCalendarColumn(name, name, field, true);
            })];
    }

    /// <summary>日期表自身也属于M查询公共名称空间, 不覆盖同名查询或大小写不同的目标.</summary>
    internal static void RequireTableName(TOM.Model model, string name)
    {
        if (model.Expressions.Any(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            || model.Tables.Any(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && value.Name != name))
            throw Conflict();
    }

    /// <summary>只接受完整归属有效的当前日期表与函数, 不读取历史 CSV 或 inline 资产.</summary>
    internal static void RequireCurrentStorage(TOM.Table table)
    {
        _ = ScaffoldCalendarWriter.IsSelfContained(table);
    }

    /// <summary>模型表与M查询共用大小写不敏感名称空间; 未知同名对象不自动改名或覆盖.</summary>
    internal static void RequireName(TOM.Model model, string name, TOM.Table? table, ModelConfigurationCalendarFunction? binding)
    {
        if (model.Tables.Any(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            || model.Tables.SelectMany(value => value.Partitions).Any(value => value.Source is TOM.MPartitionSource && value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw Conflict();
        var existing = model.Expressions.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            if (binding is not null && name == binding.FunctionName) throw Conflict();
            return;
        }
        if (binding is null || table is null || existing.Name != binding.FunctionName) throw Conflict();
        ModelConfigurationOwnership.Require(existing, existing.Annotations);
    }

    /// <summary>固定函数只允许一张独立日期表绑定, 不覆盖第二表或非当前函数名称.</summary>
    internal static void RequireSingleBinding(TOM.Model model, TOM.Table? table, ModelConfigurationCalendarFunction? binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (model.Tables.Any(value => value != table && value.Annotations.Find(ScaffoldCalendarWriter.OptionsAnnotation) is not null))
            throw new AppException("CONFIGURATION_CALENDAR_FUNCTION_CONFLICT", "固定日期函数只能绑定一张日期表, 请配置已有日期表", 3);
        if (binding is not null && binding.FunctionName != ScaffoldCalendarM.FunctionName) throw Conflict();
    }

    /// <summary>对最终规范M文本作跨TOM换行一致的指纹, 保留其余全部字符.</summary>
    internal static string CodeHash(string text) => ModelConfigurationOwnership.Hash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));

    /// <summary>精确代码事实供冻结摘要和候选回核共用.</summary>
    internal static ModelConfigurationCalendarFunction Capture(ScaffoldCalendarProgram program) => new(program.FunctionName,
        CodeHash(program.FunctionExpression), CodeHash(program.InvocationExpression), program.GeneratorVersion, program.ResourceVersion, program.Locale);

    private static AppException Conflict() => new("CONFIGURATION_CALENDAR_FUNCTION_CONFLICT", "日期函数缺失、名称冲突或绑定无效, 请重新读取目标", 3);
}
