// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldModelWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在隔离目录组合可回读 PBIP, 保留报表与计算对象, 不执行 M 或 DAX.

using PbiUtils.Infrastructure.Models.Pbip;
using System.Text;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular.Extensions;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.SampleData;
using TOM = Microsoft.AnalysisServices.Tabular;
using static PbiUtils.Infrastructure.Scaffolding.Templates.ScaffoldTemplateMetadata;
using static PbiUtils.Infrastructure.Scaffolding.ScaffoldModelFiles;
using static PbiUtils.Infrastructure.Scaffolding.ScaffoldModelExpressions;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.SampleData.Model;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.TemplateMeasures;
using PbiUtils.Infrastructure.Udf.Model;

using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.Models;

using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>只组合调用方拥有的 staging, 输出参数写最终目录, 真实发布由工作区负责.</summary>
public sealed class ScaffoldModelWriter(UdfDependencyResolver? udfResolver = null, long maximumBytes = 8L * 1024 * 1024 * 1024) : IScaffoldProjectComposer
{
    /// <summary>后台复制冻结快照并组合模型, 所有模型冲突在最终提交前失败.</summary>
    public Task<ScaffoldComposition> ComposeAsync(ResolvedPbipTemplate template, ScaffoldRequest request, string staging, string output,
        IProgress<ScaffoldProgress>? progress, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ScaffoldValidation.Validate(request);
        if (template.Info.Id != request.TemplateId || template.Info.Baseline != request.TemplateBaseline) throw new AppException("TEMPLATE_CHANGED", "模板与请求不一致", 3);
        var sourceTemplate = template;
        var initial = Fingerprint(template);
        template = ScaffoldOutputLayout.Map(template, request.ProjectName, staging);
        var builtin = template.Info.Source == "builtin";
        var budget = new ScaffoldWriteBudget(maximumBytes);
        var warnings = new List<string> { "模型定义回读不表示 M/DAX 已执行" };
        var results = new List<ScaffoldTableResult>();
        var generatedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolution = request.Udfs is { Length: > 0 }
            ? await (udfResolver ?? throw new AppException("UDF_PROVIDER_UNAVAILABLE", "未配置函数来源", 3)).ResolveAsync(request.Udfs, cancellationToken).ConfigureAwait(false)
            : new UdfResolution([], []);
        var selectedUdfs = resolution;
        foreach (var file in template.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 源 manifest 含转换前 M 和作者路径, 不直接出现在新产物.
            if (file.RelativePath.Equals("pbi-utils.template.json", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Inside(staging, file.RelativePath);
            budget.Reserve(file.Content.LongLength);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, file.Content, cancellationToken).ConfigureAwait(false);
        }
        WriteGitIgnore(staging, budget, cancellationToken);
        // 两种存储模式均建立固定配置目录, TMDL 不生成 JSON, 后续连接模型可显式切换到 Config.
        Directory.CreateDirectory(Path.Combine(staging, "pbi-utils"));
        progress?.Report(new(10, "scaffold.preparing"));
        var (modelDirectory, reportDirectory) = Bindings(template, staging);
        var definition = Path.Combine(modelDirectory, "definition");
        var tmdl = Directory.Exists(definition);
        using var database = tmdl ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(definition)
            : TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(modelDirectory, "model.bim"), cancellationToken).ConfigureAwait(false));
        var model = database.Model;
        var originalDocuments = tmdl ? database.ToTmdl().Select(document => document.LogicalPath + ".tmdl").ToArray() : [];
        var generated = model.Annotations.Find(ModelConfigurationOwnership.RolesStamp) is not null;
        var templateOnly = request.Measures is not null && request.Sample is null && request.Calendar is null && request.Periods is null
            && request.Udfs is not { Length: > 0 };
        var previousOwnership = templateOnly ? ModelConfigurationOwnership.ReadForTemplate(model) : ModelConfigurationOwnership.ReadRoles(model);
        // 纯模板操作保留其他功能的定义和原归属, 不因重用工程而重新生成或认领未选对象.
        var preservedTables = !builtin && templateOnly ? model.Tables.Where(table => table.Name != "Measure"
            && previousOwnership.Roles.GetValueOrDefault(table.Name) != "measure-config"
            && !ModelConfigurationOwnership.IsOwned(table, table.Annotations)).Select(table => table.Name).ToHashSet(StringComparer.Ordinal) : null;
        foreach (var asset in previousOwnership.Assets)
        {
            // 公共业务配置允许用户编辑, 本轮实际字节由准备资产阶段冻结, 不要求等于历史生成摘要.
            if (asset.Key == TemplateMeasureConfigurationFile.RelativePath || templateOnly) continue;
            var path = Inside(staging, asset.Key);
            if (!File.Exists(path) || ModelConfigurationOwnership.FileHash(path, cancellationToken) != asset.Value)
                throw new AppException("CONFIGURATION_ASSET_CONFLICT", "模板生成资产已被修改或删除, 未重新生成", 3);
        }
        if (!builtin) warnings.Add("用户模板外部源仅复制, 未执行或核验; 仅明确映射的表会接入本地数据");
        var roles = builtin || request.Sample is not null || request.Calendar is not null ? ReadRoles(template, model, builtin, request) : new Dictionary<string, string>(StringComparer.Ordinal);
        if (generated)
            foreach (var (name, role) in previousOwnership.Roles)
            {
                var table = model.Tables.Find(name);
                if (request.Measures is not null && role == "measure-config") { roles[name] = role; continue; }
                if (templateOnly) { if (table is not null) roles[name] = role; continue; }
                if (table is null) throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "模板已生成表被删除, 未发布工程", 3);
                ModelConfigurationOwnership.Require(table, table.Annotations);
                roles[name] = role;
            }
        var needsLocal = builtin || generated || request.Sample is not null || request.Calendar is not null || request.Measures is not null;
        if (needsLocal)
        {
            LocalizeExpressions(model, builtin, output, generated, templateMeasures: request.Measures is not null);
            warnings.Add("移动工程后需更新 PbiUtilsProjectRoot 参数为新的项目绝对目录");
        }
        ScaffoldTemplateQueryWriter.Apply(model, request.TemplateQueries, builtin, request.TemplateQueryGroups);
        SampleCsvStorage? sample = null;
        if (request.Sample is { } sampleOptions)
        {
            sample = ScaffoldDataWriter.Generate(staging, sampleOptions, budget, cancellationToken, progress, replaceExisting: true);
            foreach (var file in SampleTables.AllFiles) generatedAssets.Add("data/" + file);
            ScaffoldSampleModel.ApplyTables(model, roles, warnings);
        }
        if (builtin || request.Calendar is not null || request.Sample is not null)
            ScaffoldCalendarWriter.Apply(model, request.Calendar, request.Sample, request.Locale, builtin, staging, results, warnings, cancellationToken, sample,
                roles.FirstOrDefault(role => role.Value == "calendar" && (request.Calendar is null || role.Key == request.Calendar.TableName)).Key);
        if (request.Calendar is { } calendar) roles[calendar.TableName] = "calendar";
        if (sample is not null) ScaffoldSampleModel.ApplyDefinitions(model, roles, warnings);
        // 模板和样例先合并基础度量, 用户本次显式配置最后覆盖或隐藏同表对象.
        var measureDefinitions = request.Measures ?? (builtin && roles.Any(pair => pair.Value == "measure-config") ? TemplateMeasureValidation.DefaultMeasures() : null);
        var measureMode = request.Measures is null ? "tmdl" : request.MeasuresMode;
        var previousConfigName = roles.Where(pair => pair.Value == "measure-config").OrderByDescending(pair => model.Tables.ContainsName(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault().Key;
        if (measureDefinitions is not null)
        {
            var context = TemplateMeasureConfigurationStorage.ReadAssetContext(database, staging,
                Path.Combine(staging, template.EntryFile), _ => Task.CompletedTask) with
            { ConfigTableName = previousConfigName };
            if (measureMode == "config" && !context.CanUseMeasureConfig)
            {
                measureMode = "tmdl";
                warnings.Add("模板配置目录或 JSON 无效, 已使用 TMDL 模式并保留原文件");
            }
            ScaffoldTemplateMeasureMode.Apply(model, measureDefinitions, measureMode, roles, configuration: false, builtin);
            foreach (var asset in TemplateMeasureConfigurationStorage.PrepareAssets(staging, context, measureMode, measureDefinitions, cancellationToken))
            {
                if (asset.AfterHash is null)
                {
                    File.Delete(Inside(staging, asset.RelativePath));
                    previousOwnership.Assets.Remove(asset.RelativePath);
                }
                else generatedAssets.Add(asset.RelativePath);
            }
        }
        progress?.Report(new(80, "scaffold.preparing"));
        var pageRows = PageConfiguration(template, reportDirectory, staging);
        foreach (var (tableName, role) in roles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 参数/日期分区已由对应生成器处理, 在索引模型表之前排除, 不将旧参数角色交给通用 CSV 生成.
            if (role is "calendar" or "calendar-mirror" or "sample-helper" or "measure-config") continue;
            var table = model.Tables[tableName];
            var columns = table.Columns.OfType<TOM.DataColumn>().ToArray();
            if (columns.Length == 0 || table.Partitions.Any(p => p.Source is not TOM.MPartitionSource)) continue;
            // 未开启的配置保留模板原文件和分区, 不用缺失输入重建或认领用户资产.
            if (generated && !builtin && !(request.Measures is not null && role == "measure-config")
                && !(sample is not null && (ScaffoldDataWriter.RoleFiles.ContainsKey(role) || role is "sample-direct" or "sample-compatible"))) continue;
            var relative = (role.EndsWith("-config", StringComparison.Ordinal) ? "data/config/" : role == "sample-direct" ? "data/" : "data/model/") + ScaffoldDataWriter.FileName(table.Name);
            var generatedReplacement = generated && File.Exists(Inside(staging, relative));
            if (generatedReplacement && !previousOwnership.Assets.ContainsKey(relative))
                throw new AppException("CONFIGURATION_ASSET_CONFLICT", "模板文件没有完整生成归属, 未覆盖", 3);
            IEnumerable<string[]> rows = role switch
            {
                "page-config" => pageRows.Select(page => columns.Select(c => page.GetValueOrDefault(c.SourceColumn, "")).ToArray()),
                "security-config" => [],
                "sample-direct" => sample!.Read(table.Name + ".csv"),
                "sample-compatible" => ScaffoldSampleModel.CompatibleRows(table, sample!),
                _ => ScaffoldDataWriter.MapRows(table, role, sample, previousOwnership.Retail, warnings),
            };
            var header = columns.Select(c => c.SourceColumn).ToArray();
            // 无转换的独立表直接消费示例原始文件, 避免第二份 CSV 和重复预算.
            results.Add(role == "sample-direct"
                ? ScaffoldDataWriter.Inspect(staging, relative, table.Name, header, cancellationToken)
                : ScaffoldDataWriter.Write(staging, relative, table.Name, header, rows, budget, cancellationToken,
                    replaceExisting: generatedReplacement || sample is not null && (role == "sample-compatible" || ScaffoldDataWriter.RoleFiles.ContainsKey(role))));
            generatedAssets.Add(relative);
            var expression = ScaffoldDataWriter.CsvExpression(relative, columns);
            ModelImportPartition.Set(table, expression);
        }
        ScaffoldPeriodMeasures.Apply(model, request.Periods, configuration: false);
        if (resolution.Functions.Count > 0)
        {
            if (model.Tables.Count == 0) throw new AppException("UDF_MODEL_EMPTY", "添加函数的模型至少需要一张表", 2);
            // 模板已有安装事实由完整模型证明, 不重新查询旧来源; 本轮选择只替换其精确闭包.
            var installed = ModelConfigurationUdfLocks.ExcludeLintRemoved(model, ModelConfigurationUdfLocks.FromModel(model));
            ModelConfigurationUdfLocks.VerifyInstalled(model, installed);
            resolution = ModelConfigurationUdfLocks.Merge(installed, resolution);
            database.CompatibilityLevel = Math.Max(1702, database.CompatibilityLevel);
            foreach (var item in resolution.Functions)
            {
                var function = model.Functions.Find(item.Function.Name);
                if (function is not null)
                {
                    if (!installed.Functions.Any(value => value.Function.Name.Equals(item.Function.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "函数名称与模板现有函数冲突", 2);
                    ModelConfigurationOwnership.Require(function, function.Annotations);
                }
                else { function = new TOM.Function { Name = item.Function.Name }; model.Functions.Add(function); }
                function.Expression = item.Function.Expression; function.Description = item.Function.Description;
                foreach (var annotation in item.Function.Annotations)
                {
                    if (function.Annotations.Find(annotation.Key) is { } prior) function.Annotations.Remove(prior);
                    function.Annotations.Add(new TOM.Annotation { Name = annotation.Key, Value = annotation.Value });
                }
            }
            ModelConfigurationUdfLocks.PersistModel(model, resolution);
            warnings.Add("UDF 输出副本兼容级别至少 1702; 元数据已解析不表示函数已在 Desktop 执行");
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(90, "scaffold.preparing"));
        ModelConfigurationOwnership.StampCreated(database, roles, previousOwnership.Retail, builtin, request, resolution, preservedTables);
        var originalAssets = template.Files.Select(file => file.RelativePath).Where(path => !generatedAssets.Contains(path));
        ModelConfigurationOwnership.SetRoles(model, ModelConfigurationOwnership.ReadRoles(model) with { Assets = previousOwnership.Assets });
        ModelConfigurationOwnership.CaptureCreatedAssets(staging, model, originalAssets, cancellationToken);
        if (tmdl)
        {
            var documents = database.ToTmdl().ToArray();
            var finalPaths = documents.Select(document => document.LogicalPath + ".tmdl").ToHashSet(StringComparer.Ordinal);
            foreach (var removed in originalDocuments.Where(path => !finalPaths.Contains(path))) File.Delete(Inside(definition, removed));
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Inside(definition, document.LogicalPath + ".tmdl");
                WriteModelDocument(path, stream => document.WriteTo(stream), budget, cancellationToken);
            }
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(TOM.JsonSerializer.SerializeDatabase(database));
            WriteModelDocument(Path.Combine(modelDirectory, "model.bim"), stream => stream.Write(bytes), budget, cancellationToken);
        }
        var snapshot = await new ProjectReader().ReadAsync(Path.Combine(staging, template.EntryFile), cancellationToken).ConfigureAwait(false);
        if (snapshot.Model.CompatibilityLevel != database.CompatibilityLevel || snapshot.Model.Functions.Count != model.Functions.Count)
            throw new AppException("SCAFFOLD_MODEL_VERIFY", "模型定义回读与组合结果不一致", 4);
        if (Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) > maximumBytes)
            throw new AppException("SCAFFOLD_SIZE_LIMIT", "脚手架产物超过允许的总大小", 3);
        if (Fingerprint(sourceTemplate) != initial) throw new AppException("TEMPLATE_CHANGED", "模板冻结内容在组合时发生变化", 3);
        return new ScaffoldComposition(template.EntryFile, results, warnings.Distinct(StringComparer.Ordinal).ToArray(), database.CompatibilityLevel,
            selectedUdfs.Functions.Select(item => new ScaffoldUdfPreview(item.Function.Name, item.Source, item.PackageId, item.Version, item.IsDependency, item.Baseline, item.SourceBaseline)).ToArray());
    }, cancellationToken);

    /// <summary>内置角色来自受版本管理的 manifest; 用户只消费明确且完整匹配实际 schema 的业务映射.</summary>
    internal static Dictionary<string, string> ReadRoles(ResolvedPbipTemplate template, TOM.Model model, bool builtin, ScaffoldRequest request)
    {
        // 内置日期角色总会转换为自包含函数; 不接受同批另一张输出映射不同的日期表, 预览与冻结组合在写表前共同拒绝.
        if (builtin && model.Tables.ContainsName("Calendar") && request.Calendar is { TableName: not "Calendar" })
            throw new AppException("CONFIGURATION_CALENDAR_FUNCTION_CONFLICT", "固定日期函数只能绑定一张日期表, 请配置已有日期表", 3);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var descriptor = template.Files.SingleOrDefault(f => f.RelativePath == "pbi-utils.template.json");
        if (descriptor is null) return result;
        using var document = ParseJson(descriptor.Content);
        if (!document.RootElement.TryGetProperty("tables", out var tables)) return result;
        foreach (var item in tables.EnumerateArray())
        {
            if (!item.TryGetProperty("role", out var roleValue)) continue;
            var role = roleValue.GetString()!;
            var name = item.GetProperty("name").GetString()!;
            if (!builtin && !(request.Sample is not null && ScaffoldDataWriter.RoleFiles.ContainsKey(role))
                && !(role == "calendar" && request.Calendar?.TableName == name)) continue;
            var table = model.Tables.Find(name) ?? throw new AppException("SCAFFOLD_COLUMN_MAPPING", "模板映射表不存在", 2);
            var expected = item.GetProperty("columns").EnumerateArray().ToArray();
            if (expected.Length != table.Columns.Count || expected.Any(column => table.Columns.Find(column.GetProperty("name").GetString()!) is not { } actual
                || actual.DataType.ToString() != column.GetProperty("dataType").GetString()
                || actual is TOM.DataColumn data && data.SourceColumn != column.GetProperty("sourceColumn").GetString()))
                throw new AppException("SCAFFOLD_COLUMN_MAPPING", "模板列映射与实际模型不一致", 2);
            result.Add(name, role);
        }
        return result;
    }

    /// <summary>内置转换仅保留安全常量辅助查询, 不将原文件或百度函数带入产物; 预览复用同一规则.</summary>
    /// <param name="model">调用方拥有的私有模型副本.</param>
    internal static void LocalizeBuiltinExpressions(TOM.Model model) => ScaffoldModelExpressions.LocalizeBuiltinExpressions(model);

    /// <summary>与模板读取器保持相同 UTF-8 BOM 边界, 不修改源字节.</summary>
    /// <param name="content">冻结的原始 JSON 字节.</param>
    /// <returns>由调用方释放的解析文档; 无效 JSON 保留原解析错误.</returns>
    internal static JsonDocument ParseJson(byte[] content) => ScaffoldTemplateMetadata.ParseJson(content);

}
