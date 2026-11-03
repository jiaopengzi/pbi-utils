// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Configuration\ModelConfigurationMutation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 新建与持续配置复用冻结生成规则, 仅修改调用方拥有的当前 TOM 对象.

using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using PbiUtils.Infrastructure.SampleData;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.SampleData.Model;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Udf.Model;

using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Infrastructure.Scaffolding;

using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Models.Configuration;

internal static class ModelConfigurationMutation
{

    /// <summary>stage 仅在文件准备阶段提供; 模型操作只应用冻结参数与资产摘要, 不重新访问来源或解析版本.</summary>
    /// <param name="database">调用方独占的当前 TOM 对象; Desktop 失败由原操作会话结束, 离线由文件事务处理.</param>
    /// <param name="plan">绑定原基线、精确函数、字段映射及资产摘要的宿主冻结计划.</param>
    /// <param name="stage">仅准备阶段用于生成文件的私有目录, 实际提交时为 null.</param>
    /// <param name="token">准备和生成中的取消令牌, 不控制外部提交.</param>
    /// <returns>本轮实际扩展的日期范围, 供精确确认与后续核验.</returns>
    /// <exception cref="AppException">已有对象归属、依赖、升级许可或冻结映射不符.</exception>
    internal static IReadOnlyList<ModelConfigurationCalendarChange> Apply(TOM.Database database, ModelConfigurationPlan plan, string? stage = null, CancellationToken token = default)
    {
        var request = plan.Request;
        var model = database.Model;
        // 同批期间绑定只能跟随提交前实际存在的同一列对象, 不从旧名称或语义猜测其他目标.
        var periodDateBefore = request.Periods is { } requestedPeriods && request.Calendar?.TableName == requestedPeriods.DateTable
            ? model.Tables.Find(requestedPeriods.DateTable)?.Columns.Find(requestedPeriods.DateColumn) : null;
        var calendarBefore = plan.CalendarOutputs.ToDictionary(output => output.TableName,
            output => (IReadOnlySet<string>)(model.Tables.Find(output.TableName)?.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal) ?? []), StringComparer.Ordinal);
        var ownership = ModelConfigurationOwnership.IsTemplateOnly(request) ? ModelConfigurationOwnership.ReadForTemplate(model) : ModelConfigurationOwnership.ReadRoles(model);
        var roles = new Dictionary<string, string>(ownership.Roles, StringComparer.Ordinal);
        var warnings = new List<string>();
        var budget = new ScaffoldWriteBudget(8L * 1024 * 1024 * 1024);
        var fileBased = plan.AssetRoot is not null;
        var changedTables = new HashSet<string>(StringComparer.Ordinal);
        var derivedCalendars = new List<ModelConfigurationCalendarChange>();
        if (fileBased && (request.Sample is not null || request.Calendar is not null || request.Measures is not null && request.MeasuresMode == "config"))
        {
            if (model.Expressions.Find("PbiUtilsProjectRoot") is { } existing)
            {
                // 本轮模板 Config 明确授权修复根参数; 日期和样例单独操作仍核验原生成归属.
                if (request.Measures is null || request.MeasuresMode != "config") ModelConfigurationOwnership.Require(existing, existing.Annotations);
                existing.Kind = TOM.ExpressionKind.M;
                existing.Expression = PowerQueryLiterals.Text(plan.AssetRoot!) + " meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]";
                TomModelMetadata.TextParameterMetadata(existing); ModelConfigurationOwnership.Mark(existing, existing.Annotations);
            }
            else
            {
                var expression = new TOM.NamedExpression
                {
                    Name = "PbiUtilsProjectRoot",
                    Kind = TOM.ExpressionKind.M,
                    Expression = PowerQueryLiterals.Text(plan.AssetRoot!) + " meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]"
                };
                TomModelMetadata.TextParameterMetadata(expression);
                model.Expressions.Add(expression); ModelConfigurationOwnership.Mark(expression, expression.Annotations);
            }
        }
        if (request.Measures is { } definitions)
        {
            if (request.MeasuresMode == "config" && !fileBased)
                throw new AppException("CONFIGURATION_ASSETS_UNSUPPORTED", "Config 模式需要已核实的本地 PBIP/PBIX 文件", 3);
            var ownedParents = model.Tables.Where(table => ModelConfigurationOwnership.IsOwned(table, table.Annotations))
                .ToDictionary(table => table.Name, table => ModelConfigurationOwnership.Fingerprint(table), StringComparer.Ordinal);
            // 日期表在模板之后生成, 变量名仍须按本轮最终表集合避让, 不能只检查当前模型.
            ScaffoldTemplateMeasureMode.Apply(model, definitions, request.MeasuresMode, roles, configuration: true,
                reservedTableNames: request.Calendar is { } pendingCalendar ? [pendingCalendar.TableName] : null,
                sourceDefinitions: plan.AssetContext?.ExistingConfiguration?.Measures);
            // 仅延续更新前有效的父表归属, 同时覆盖跨表旧项删除后父表已无选中项的情形.
            foreach (var table in model.Tables.Where(table => ownedParents.TryGetValue(table.Name, out var before)
                && before != ModelConfigurationOwnership.Fingerprint(table))) changedTables.Add(table.Name);
            changedTables.Add("Measure");
        }
        if (request.Sample is { } sampleOptions)
        {
            if (!fileBased) throw new AppException("CONFIGURATION_ASSETS_UNSUPPORTED", "当前模型没有可核实的 PBIP 文件资产能力", 3);
            var sample = stage is null ? null : ScaffoldDataWriter.Generate(stage, sampleOptions, budget, token);
            var mapped = roles.Where(pair => ScaffoldDataWriter.RoleFiles.ContainsKey(pair.Value) || pair.Value is "sample-direct" or "sample-compatible").ToArray();
            foreach (var pair in mapped)
            {
                var table = model.Tables.Find(pair.Key) ?? throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "生成映射表已被删除", 3);
                ModelConfigurationOwnership.Require(table, table.Annotations);
            }
            if (mapped.Length == 0)
            {
                foreach (var file in SampleTables.AllFiles)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (model.Tables.ContainsName(name)) throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "模拟表名称与已有对象冲突", 3);
                    var table = new TOM.Table { Name = name };
                    var headers = SampleTables.Header(file);
                    for (var index = 0; index < headers.Count; index++) table.Columns.Add(new TOM.DataColumn { Name = headers[index], SourceColumn = headers[index], DataType = ScaffoldDataWriter.SampleType(file, index) });
                    model.Tables.Add(table); roles.Add(name, "sample-direct");
                }
                mapped = [.. roles.Where(pair => pair.Value == "sample-direct")];
            }
            if (ownership.Retail && !model.Tables["D20_大区表"].Columns.ContainsName("PbiUtilsRegionId"))
            {
                ScaffoldRetailMapping.Apply(model);
                foreach (var (table, column) in new[] { ("D30_产品表", "产品成本价格"), ("D30_产品表", "产品销售价格"), ("T41_订单子表", "产品销售价格") }) model.Tables[table].Columns[column].DataType = TOM.DataType.Double;
            }
            foreach (var pair in mapped)
            {
                var table = model.Tables[pair.Key];
                var columns = table.Columns.OfType<TOM.DataColumn>().ToArray();
                var relative = (pair.Value == "sample-direct" ? "data/" : "data/model/") + ScaffoldDataWriter.FileName(table.Name);
                if (stage is not null && pair.Value != "sample-direct")
                    ScaffoldDataWriter.Write(stage, relative, table.Name, columns.Select(column => column.SourceColumn).ToArray(),
                        pair.Value == "sample-compatible" ? ScaffoldSampleModel.CompatibleRows(table, sample!)
                            : ScaffoldDataWriter.MapRows(table, pair.Value, sample, ownership.Retail, warnings), budget, token);
                ModelImportPartition.Set(table, ScaffoldDataWriter.CsvExpression(relative, columns));
                changedTables.Add(table.Name);
            }
            foreach (var role in roles.Where(pair => pair.Value == "calendar"))
            {
                var table = model.Tables.Find(role.Key) ?? throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "已生成日期表已被删除", 3);
                ModelConfigurationOwnership.Require(table, table.Annotations);
                ModelConfigurationCalendarChange? change;
                if (stage is not null)
                {
                    var before = ScaffoldCalendarWriter.ReadOptions(table);
                    var expanded = ScaffoldCalendarWriter.ExpandForSample(table, sampleOptions, request.Calendar, sample!, token);
                    if (request.Calendar?.TableName == table.Name) continue;
                    if (before.StartYear == expanded.StartYear && before.EndYear == expanded.EndYear && ScaffoldCalendarWriter.IsSelfContained(table)) continue;
                    change = new(table.Name, before.StartYear, before.EndYear, expanded);
                }
                else change = plan.DerivedCalendars.SingleOrDefault(value => value.TableName == table.Name);
                if (change is null) continue;
                ScaffoldCalendarWriter.Write(table, change.Options, false, stage, [], warnings, token);
                changedTables.Add(table.Name); derivedCalendars.Add(change);
            }
        }
        if (request.Calendar is { } calendar)
        {
            CalendarProgramPlanner.RequireTableName(model, calendar.TableName);
            var table = model.Tables.Find(calendar.TableName);
            if (table is not null)
            {
                ModelConfigurationOwnership.Require(table, table.Annotations);
                CalendarProgramPlanner.RequireCurrentStorage(table);
            }
            else { table = new TOM.Table { Name = calendar.TableName, DataCategory = "Time" }; model.Tables.Add(table); }
            ScaffoldCalendarWriter.Write(table, calendar, true, stage, [], warnings, token, request.Locale);
            roles[table.Name] = "calendar"; changedTables.Add(table.Name);
        }
        // 父表指纹包含其度量值; 只延续变更前仍有效的归属, 不替人工修改重新背书.
        var ownedPeriodParents = request.Periods is null ? new HashSet<string>(StringComparer.Ordinal)
            : model.Tables.Where(table => ModelConfigurationOwnership.IsOwned(table, table.Annotations)).Select(table => table.Name).ToHashSet(StringComparer.Ordinal);
        var effectivePeriods = request.Periods;
        if (effectivePeriods is not null && periodDateBefore is not null
            && model.Tables.Find(effectivePeriods.DateTable)?.Columns.Any(column => ReferenceEquals(column, periodDateBefore)) == true)
            effectivePeriods = effectivePeriods with { DateColumn = periodDateBefore.Name };
        var periodChanges = ScaffoldPeriodMeasures.Apply(model, effectivePeriods, configuration: true);
        changedTables.UnionWith(periodChanges.Where(ownedPeriodParents.Contains));
        if (plan.Udfs.Functions.Count > 0)
        {
            var installed = ModelConfigurationUdfLocks.ExcludeLintRemoved(model, ModelConfigurationUdfLocks.FromModel(model));
            ModelConfigurationUdfLocks.VerifyInstalled(model, installed);
            var resolution = ModelConfigurationUdfLocks.Merge(installed, plan.Udfs);
            if (database.CompatibilityLevel < 1702)
            {
                if (!request.AllowCompatibilityUpgrade) throw new AppException("CONFIGURATION_COMPATIBILITY_APPROVAL_REQUIRED", "安装 UDF 需要本次独立同意将兼容级别提升至 1702", 3);
                database.CompatibilityLevel = 1702;
            }
            if (model.Tables.Count == 0) throw new AppException("UDF_MODEL_EMPTY", "添加函数的模型至少需要一张表", 3);
            foreach (var resolved in resolution.Functions)
            {
                var function = model.Functions.Find(resolved.Function.Name);
                if (function is not null) ModelConfigurationOwnership.Require(function, function.Annotations);
                else { function = new TOM.Function { Name = resolved.Function.Name }; model.Functions.Add(function); }
                function.Expression = resolved.Function.Expression; function.Description = resolved.Function.Description;
                foreach (var pair in resolved.Function.Annotations)
                {
                    if (function.Annotations.Find(pair.Key) is { } old) function.Annotations.Remove(old);
                    function.Annotations.Add(new TOM.Annotation { Name = pair.Key, Value = pair.Value });
                }
                ModelConfigurationOwnership.Mark(function, function.Annotations);
            }
            ModelConfigurationUdfLocks.PersistModel(model, resolution);
        }
        foreach (var table in model.Tables)
            if (changedTables.Contains(table.Name))
                ModelConfigurationOwnership.Mark(table, table.Annotations, includeChildren: request.Measures is null);
        if (roles.Count > 0 || ownership.Roles.Count > 0 || plan.Assets.Count > 0
            || ModelConfigurationOwnership.IsTemplateOnly(request) && model.Annotations.Find(ModelConfigurationOwnership.RolesStamp) is not null)
        {
            var assets = new Dictionary<string, string>(ownership.Assets, StringComparer.Ordinal);
            foreach (var asset in plan.Assets)
                if (asset.AfterHash is { } hash) assets[asset.RelativePath] = hash;
                else assets.Remove(asset.RelativePath);
            ModelConfigurationOwnership.SetRoles(model, ownership with { Roles = roles, Assets = assets });
        }
        if (plan.HasCalendarOutputPlan)
        {
            var actualNames = derivedCalendars.Select(value => value.TableName).ToHashSet(StringComparer.Ordinal);
            if (request.Calendar is { } selectedCalendar) actualNames.Add(selectedCalendar.TableName);
            if (!actualNames.SetEquals(plan.CalendarOutputs.Select(output => output.TableName)))
                throw new AppException("CONFIGURATION_CALENDAR_MAPPING_CHANGED", "日期输出映射与冻结摘要不一致, 未提交", 3);
            foreach (var expected in plan.CalendarOutputs)
            {
                var actual = ScaffoldCalendarWriter.CaptureOutput(model.Tables[expected.TableName], calendarBefore[expected.TableName]);
                if (!expected.Columns.SequenceEqual(actual.Columns) || expected.Function != actual.Function)
                    throw new AppException("CONFIGURATION_CALENDAR_MAPPING_CHANGED", "日期输出映射与冻结摘要不一致, 未提交", 3);
            }
        }
        return derivedCalendars;
    }

}
