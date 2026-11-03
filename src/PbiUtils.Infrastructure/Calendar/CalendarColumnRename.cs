// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Calendar\CalendarColumnRename.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 预检日期标准列编号覆盖并同步真实模型引用, 保留原对象及生成归属边界.

using PbiUtils.Application.Code.PowerQuery;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Calendar;

/// <summary>已在完整私有模型预检的更名批次, 不对外部 live 模型发起 SDK 操作.</summary>
internal sealed record CalendarColumnRenamePlan(IReadOnlyDictionary<string, string> Renames, Action Apply, Action Complete);

/// <summary>只有标准生成名称随编号选项变化; 业务别名和无法消歧的外部依赖不猜测重写.</summary>
internal static class CalendarColumnRename
{
    /// <summary>将标准语义名/固定编号名切换到本轮编号, 非标准显示或源别名原样返回.</summary>
    internal static string StandardName(string name, string field, bool numbered) => name == field || name == ScaffoldCalendarFields.ColumnName(field, true)
        ? ScaffoldCalendarFields.ColumnName(field, numbered) : name;

    /// <summary>从已核验表和最终映射只读准备更名及引用更新; M、歧义或未知表达式会在任何修改前失败.</summary>
    /// <param name="table">调用方拥有的完整私有候选中的生成日期表.</param>
    /// <param name="options">本轮显式字段及编号配置.</param>
    /// <param name="prior">变更前真实模型名、源列和语义.</param>
    /// <param name="final">同一规划器产生的完整最终映射.</param>
    /// <param name="token">只读预检的取消令牌.</param>
    /// <returns>分为应用与完成的精确批次, 没有更名时为 null.</returns>
    /// <exception cref="AppException">无法证明引用安全时拒绝, 不提供部分变更.</exception>
    internal static CalendarColumnRenamePlan? Prepare(TOM.Table table, ScaffoldCalendarOptions options,
        IReadOnlyList<ModelConfigurationCalendarColumn> prior, IReadOnlyList<ModelConfigurationCalendarColumn> final, CancellationToken token)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceChanged = false;
        foreach (var column in prior)
        {
            var desired = options.NumberedColumns is { } numbered ? StandardName(column.Name, column.Field, numbered) : column.Name;
            var actual = final.SingleOrDefault(value => value.Name == desired && value.Field == column.Field);
            if (actual is null) continue;
            if (desired != column.Name) renames.Add(column.Name, desired);
            sourceChanged |= actual.SourceColumn != column.SourceColumn;
        }
        if (renames.Count == 0 && !sourceChanged) return null;
        ModelConfigurationOwnership.Require(table, table.Annotations);
        var model = table.Model ?? throw Blocked();
        var binding = ScaffoldCalendarWriter.FunctionBinding(table);
        foreach (var expression in model.Expressions.Where(value => value.Name != binding?.FunctionName)) CheckM(expression.Expression);
        foreach (var candidate in model.Tables)
        {
            foreach (var partition in candidate.Partitions.Where(value => candidate != table))
                if (partition.Source is TOM.MPartitionSource source) CheckM(source.Expression);
            if (candidate.RefreshPolicy is TOM.BasicRefreshPolicy policy) { CheckM(policy.SourceExpression); CheckM(policy.PollingExpression); }
            else if (candidate.RefreshPolicy is not null) throw Blocked();
        }
        var references = new CalendarColumnRenameReferences(model, table, renames);
        references.RequireLinguistic(token);
        var changes = new List<(string Before, string After, Action<string> Set)>();
        foreach (var candidate in model.Tables)
        {
            foreach (var column in candidate.Columns.OfType<TOM.CalculatedColumn>()) Add(column.Expression, value => column.Expression = value);
            foreach (var column in candidate.Columns.OfType<TOM.CalculatedTableColumn>()) Add(column.SourceColumn, value => column.SourceColumn = value);
            foreach (var partition in candidate.Partitions)
                if (partition.Source is TOM.CalculatedPartitionSource source) Add(source.Expression, value => source.Expression = value);
            foreach (var set in candidate.Sets) Add(set.Expression, value => set.Expression = value);
            if (candidate.DefaultDetailRowsDefinition is { } rows) Add(rows.Expression, value => rows.Expression = value);
            foreach (var measure in candidate.Measures)
            {
                Add(measure.Expression, value => measure.Expression = value);
                if (measure.DetailRowsDefinition is { } detail) Add(detail.Expression, value => detail.Expression = value);
                if (measure.FormatStringDefinition is { } format) Add(format.Expression, value => format.Expression = value);
                if (measure.KPI is { } kpi)
                {
                    Add(kpi.TargetExpression, value => kpi.TargetExpression = value);
                    Add(kpi.StatusExpression, value => kpi.StatusExpression = value);
                    Add(kpi.TrendExpression, value => kpi.TrendExpression = value);
                }
            }
            if (candidate.CalculationGroup is not { } group) continue;
            foreach (var item in group.CalculationItems)
            {
                Add(item.Expression, value => item.Expression = value);
                if (item.FormatStringDefinition is { } format) Add(format.Expression, value => format.Expression = value);
            }
            foreach (var selection in new[] { group.NoSelectionExpression, group.MultipleOrEmptySelectionExpression }.OfType<TOM.CalculationGroupExpression>())
            {
                Add(selection.Expression, value => selection.Expression = value);
                if (selection.FormatStringDefinition is { } format) Add(format.Expression, value => format.Expression = value);
            }
        }
        foreach (var function in model.Functions) Add(function.Expression, value => function.Expression = value);
        foreach (var permission in model.Roles.SelectMany(role => role.TablePermissions)) Add(permission.FilterExpression, value => permission.FilterExpression = value);
        references.RequireKnownExpressions(changes.Select(value => value.Before), token);
        var columns = renames.Select(pair => (Value: table.Columns[pair.Key], Name: pair.Value)).ToArray();
        // 只延续变更前仍完整有效的生成对象; 人工修改对象可以同步引用, 不能因此获得新的生成归属.
        var ownedTables = model.Tables.Where(value => ModelConfigurationOwnership.IsOwned(value, value.Annotations)).ToArray();
        var ownedMeasures = model.Tables.SelectMany(value => value.Measures).Where(value => ModelConfigurationOwnership.IsOwned(value, value.Annotations)).ToArray();
        var ownedFunctions = model.Functions.Where(value => ModelConfigurationOwnership.IsOwned(value, value.Annotations)).ToArray();
        var periodConfiguration = ScaffoldPeriodConfiguration.Read(model);
        return new(renames, () =>
        {
            foreach (var (Before, After, Set) in changes) Set(After);
            // TOM 权限和透视的名称来自其 Column 对象, 不能写派生 Name; 保留原对象即可同步其实际名称.
            foreach (var (Value, Name) in columns) Value.Name = Name;
        }, () =>
        {
            foreach (var measure in ownedMeasures.Where(value => !ModelConfigurationOwnership.IsOwned(value, value.Annotations))) ModelConfigurationOwnership.Mark(measure, measure.Annotations);
            foreach (var function in ownedFunctions.Where(value => !ModelConfigurationOwnership.IsOwned(value, value.Annotations))) ModelConfigurationOwnership.Mark(function, function.Annotations);
            foreach (var candidate in ownedTables.Where(value => !ModelConfigurationOwnership.IsOwned(value, value.Annotations))) ModelConfigurationOwnership.Mark(candidate, candidate.Annotations);
            if (periodConfiguration is not null) ScaffoldPeriodConfiguration.RenameDateBinding(model, table.Name, renames);
        });

        /// <summary>只有真实下游 M 查询/函数依赖阻止覆盖, 注释和局部记录不会误判为查询.</summary>
        void CheckM(string? expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            if (PowerQueryFormatter.MayReferenceQuery(expression, table.Name, token)
                || binding is not null && PowerQueryFormatter.MayReferenceQuery(expression, binding.FunctionName, token)) throw Blocked();
        }
        /// <summary>先收集全部可安全改写的 SDK 表达式, 预检结束前不调用 setter.</summary>
        void Add(string? expression, Action<string> setter)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            var rewritten = references.Rewrite(expression, token);
            if (rewritten != expression) changes.Add((expression, rewritten, setter));
        }
    }

    /// <summary>不在错误信息中暴露业务表达式; 用户可先明确限定或处理下游引用后重试.</summary>
    internal static AppException Blocked() => new("CONFIGURATION_CALENDAR_RENAME_REFERENCED", "日期编号覆盖存在无法安全同步的查询、字段或元数据引用, 请先明确引用后重试", 3);
}
