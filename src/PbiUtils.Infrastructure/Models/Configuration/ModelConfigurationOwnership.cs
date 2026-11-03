// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Configuration\ModelConfigurationOwnership.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 版本化生成归属和流式资产指纹, 模板显式覆盖使用独立的最小恢复入口.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Core;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.TemplateMeasures;

using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Models.Configuration;

internal sealed record ConfigurationOwnership(int SchemaVersion, Dictionary<string, string> Roles, bool Retail, Dictionary<string, string> Assets);

internal static class ModelConfigurationOwnership
{
    internal const string Stamp = "PbiUtils.Configuration.v1";
    internal const string RolesStamp = "PbiUtils.Configuration.Roles.v1";

    /// <summary>排除 SDK 明确标识的推导对象、属性和时间, 保留显式类型、推导策略、lineage 和业务注解.</summary>
    /// <param name="item">宿主持有的完整模型或归属核验对象, 本方法不修改其状态.</param>
    /// <returns>仅包含可写定义并固定键序与换行的 JSON, 供完整基线和生成归属共用.</returns>
    internal static JsonNode Definition(TOM.MetadataObject item)
    {
        var definition = JsonNode.Parse(TOM.JsonSerializer.SerializeObject(item,
            new TOM.SerializeOptions { IgnoreInferredProperties = true, IgnoreInferredObjects = true, IgnoreTimestamps = true }))!;
        NormalizeInferredColumnTypes(item, definition);
        return Canonical(definition)!;
    }

    /// <summary>按实际 TOM 列标志规范化推导类型, 不递归改写扩展属性等业务 JSON.</summary>
    /// <param name="item">与定义节点对应的实际 SDK 对象, 其推导策略是唯一判断来源.</param>
    /// <param name="definition">SDK 已排除推导对象后的定义, 不为 RowNumber 等对象补建节点.</param>
    private static void NormalizeInferredColumnTypes(TOM.MetadataObject item, JsonNode definition)
    {
        switch (item)
        {
            case TOM.Model model when definition["tables"] is JsonArray tables:
                foreach (var table in tables)
                    NormalizeInferredColumnTypes(model.Tables[table!["name"]!.GetValue<string>()], table);
                break;
            case TOM.Table table when definition["columns"] is JsonArray columns:
                foreach (var column in columns)
                    NormalizeInferredColumnTypes(table.Columns[column!["name"]!.GetValue<string>()], column);
                break;
            case TOM.Column { IsDataTypeInferred: true }:
                // SDK 排除推导属性的序列化仍可能保留列类型, 引擎运行时推导值不属于可写定义.
                // 保留 true 策略以区分显式类型; 类型尚未推导为 Unknown 时也不能省略其推导策略.
                definition.AsObject().Remove("dataType");
                definition["isDataTypeInferred"] = true;
                break;
        }
    }

    internal static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject([.. obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Canonical(pair.Value)))]),
        JsonArray array when array.All(item => item is JsonObject obj && obj["name"] is JsonValue value && value.TryGetValue<string>(out _))
            => new JsonArray([.. array.OrderBy(item => item!["name"]!.GetValue<string>(), StringComparer.Ordinal).Select(Canonical)]),
        JsonArray array => new JsonArray([.. array.Select(Canonical)]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')),
        _ => node?.DeepClone()
    };

    internal static string Fingerprint(TOM.MetadataObject item)
    {
        var node = Definition(item);
        static void Strip(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                if (obj["annotations"] is JsonArray annotations)
                {
                    foreach (var annotation in annotations.Where(value => value?["name"]?.GetValue<string>() == Stamp).ToArray()) annotations.Remove(annotation);
                    if (annotations.Count == 0) obj.Remove("annotations");
                }
                foreach (var child in obj.ToArray()) Strip(child.Value);
            }
            else if (current is JsonArray array) foreach (var child in array) Strip(child);
        }
        Strip(node); return Hash(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    internal static bool IsOwned(TOM.MetadataObject item, ICollection<TOM.Annotation> annotations)
    {
        var stamp = annotations.SingleOrDefault(annotation => annotation.Name == Stamp)?.Value;
        return stamp is not null && stamp == "v2:" + Fingerprint(item);
    }

    internal static void Require(TOM.MetadataObject item, ICollection<TOM.Annotation> annotations)
    {
        if (!IsOwned(item, annotations))
            throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "已有对象没有可验证的生成归属或已被人工修改, 未覆盖", 3);
    }

    /// <summary>记录当前完整定义; 模板父表仅更新自身身份, 不为无关用户子项补写 lineage.</summary>
    /// <param name="item">本轮明确生成或更新的对象.</param>
    /// <param name="annotations">该对象的注解集合.</param>
    /// <param name="includeChildren">是否为表的全部子项补齐确定性 lineage, 默认用于完整生成表.</param>
    internal static void Mark(TOM.MetadataObject item, ICollection<TOM.Annotation> annotations, bool includeChildren = true)
    {
        // 提前固定工具生成身份, 让Desktop无需在首次打开后补lineage而改变可写定义.
        static string Lineage(string? value, string kind, string name, string? parent = null) => !string.IsNullOrEmpty(value) ? value
            : new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { "PbiUtils.Configuration.Lineage.v2", kind, parent, name })).AsSpan(0, 16)).ToString();
        switch (item.Model?.Database is { CompatibilityLevel: < 1540 } ? null : item)
        {
            case TOM.Table table:
                table.LineageTag = Lineage(table.LineageTag, "table", table.Name);
                if (includeChildren)
                {
                    foreach (var column in table.Columns.Where(column => column is not TOM.RowNumberColumn)) column.LineageTag = Lineage(column.LineageTag, "column", column.Name, table.Name);
                    foreach (var measure in table.Measures) measure.LineageTag = Lineage(measure.LineageTag, "measure", measure.Name, table.Name);
                }
                break;
            case TOM.Measure measure: measure.LineageTag = Lineage(measure.LineageTag, "measure", measure.Name, measure.Table?.Name); break;
            case TOM.Function function: function.LineageTag = Lineage(function.LineageTag, "function", function.Name); break;
            case TOM.NamedExpression expression: expression.LineageTag = Lineage(expression.LineageTag, "expression", expression.Name); break;
        }
        if (annotations.SingleOrDefault(annotation => annotation.Name == Stamp) is { } old) annotations.Remove(old);
        annotations.Add(new TOM.Annotation { Name = Stamp, Value = "v2:" + Fingerprint(item) });
    }

    /// <summary>从完整模型读取角色和资产归属; 损坏、重复键、未知字段或越界资产路径均拒绝使用.</summary>
    /// <param name="model">宿主持有的完整权威模型或私有候选.</param>
    /// <returns>当前角色、模板类型与受保护文件的精确内容摘要, 无注解时不认领任何资产.</returns>
    /// <exception cref="AppException">生成归属格式或内容无效.</exception>
    internal static ConfigurationOwnership ReadRoles(TOM.Model model)
    {
        if (model.Annotations.Find(RolesStamp) is not { } annotation) return new(1, new(StringComparer.Ordinal), false, new(StringComparer.Ordinal));
        try
        {
            if (Encoding.UTF8.GetByteCount(annotation.Value) > 4 * 1024 * 1024) throw new JsonException();
            using var document = JsonDocument.Parse(annotation.Value);
            JsonInputValidation.RejectDuplicates(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (keys.Length != 4 || keys.Any(key => key is not ("SchemaVersion" or "Roles" or "Retail" or "Assets"))) throw new JsonException();
            var value = document.RootElement.Deserialize<ConfigurationOwnership>()!;
            if (value is null || value.SchemaVersion != 1 || value.Roles is null || value.Assets is null || value.Assets.Count > 10000
                || value.Roles.Any(pair => string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(pair.Value))
                || value.Assets.Any(pair => !IsAssetPath(pair.Key) || pair.Value is null || pair.Value.Length != 64
                    || pair.Value.Any(character => !char.IsAsciiHexDigitLower(character)))) throw new JsonException();
            return value;
        }
        catch (JsonException) { throw new AppException("CONFIGURATION_OWNERSHIP_INVALID", "生成归属版本或内容不受支持, 未修改模型", 3); }
    }

    /// <summary>纯模板显式覆盖不依赖旧角色清单; 损坏时不信任任何旧角色或资产, 仅重建本轮模板状态.</summary>
    /// <param name="model">已通过目标身份和完整基线核实的模型.</param>
    /// <returns>有效旧映射或空映射, 空映射不授予日期、样例、UDF 或文件覆盖权限.</returns>
    internal static ConfigurationOwnership ReadForTemplate(TOM.Model model)
    {
        try { return ReadRoles(model); }
        catch (AppException error) when (error.Code == "CONFIGURATION_OWNERSHIP_INVALID")
        { return new(1, new(StringComparer.Ordinal), false, new(StringComparer.Ordinal)); }
    }

    /// <summary>模板恢复例外只用于明确单独提交该段的请求, 不减弱同批其他功能的归属核验.</summary>
    internal static bool IsTemplateOnly(ModelConfigurationRequest request) => request.Measures is not null && request.Sample is null
        && request.Calendar is null && request.Periods is null && request.Udfs is not { Length: > 0 };

    /// <summary>将角色和完整资产摘要写入同一模型候选, 不产生附加配置文件.</summary>
    /// <param name="model">调用方独占的模型候选.</param>
    /// <param name="ownership">已由受信生成流程核验的角色和资产摘要.</param>
    internal static void SetRoles(TOM.Model model, ConfigurationOwnership ownership)
    {
        if (model.Annotations.Find(RolesStamp) is { } old) model.Annotations.Remove(old);
        model.Annotations.Add(new TOM.Annotation { Name = RolesStamp, Value = JsonSerializer.Serialize(ownership) });
    }

    /// <summary>只登记本轮明确生成的对象, 重用模板时未选表保留原归属而不追认人工变化.</summary>
    /// <param name="database">本轮自有 staging 模型.</param>
    /// <param name="roles">最终业务角色映射.</param>
    /// <param name="retail">是否使用零售业务映射.</param>
    /// <param name="builtin">受信内置模板是否由本轮完整生成.</param>
    /// <param name="request">本轮明确选择的配置.</param>
    /// <param name="resolution">已经冻结的 UDF 解析结果.</param>
    /// <param name="preservedTables">纯模板重用时保留原归属的未选父表, 不因同名选中度量追认失效父表.</param>
    internal static void StampCreated(TOM.Database database, Dictionary<string, string> roles, bool retail, bool builtin, ScaffoldRequest request, UdfResolution resolution,
        IReadOnlySet<string>? preservedTables = null)
    {
        var model = database.Model;
        foreach (var table in model.Tables)
        {
            foreach (var measure in table.Measures)
            {
                var requested = request.Measures?.Any(value => value.Enabled && value.Name.Equals(measure.Name, StringComparison.OrdinalIgnoreCase)) == true;
                var period = request.Periods is { } options && table.Name == options.MeasureTable
                    && options.BaseMeasures.SelectMany(name => ScaffoldTimeIntelligence.Generate(name, options)).Any(value => value.Name == measure.Name);
                if (builtin || requested || period) Mark(measure, measure.Annotations);
            }
            if ((builtin || roles.ContainsKey(table.Name)) && preservedTables?.Contains(table.Name) != true)
                Mark(table, table.Annotations, includeChildren: preservedTables is null);
        }
        foreach (var function in model.Functions)
            if (builtin || resolution.Functions.Any(value => value.Function.Name == function.Name)) Mark(function, function.Annotations);
        if (model.Expressions.Find("PbiUtilsProjectRoot") is { } parameter) Mark(parameter, parameter.Annotations);
        SetRoles(model, new(1, roles, retail, new(StringComparer.Ordinal)));
    }

    internal static string FileHash(string file, CancellationToken token = default)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>仅认领本轮新建明确生成的文件, 将流式内容摘要留在待序列化模型中, 不写入旁文件.</summary>
    /// <param name="root">本轮拥有的完整项目 staging 根, 包含并列 data/config/pbip.</param>
    /// <param name="model">尚未序列化的私有模型, 已持有本轮对象角色.</param>
    /// <param name="originalFiles">仍属于原模板的路径, 不因复制或同名被自动认领.</param>
    /// <param name="token">枚举和逐块读取前生效的取消令牌.</param>
    /// <exception cref="AppException">文件路径不安全或模型归属无效.</exception>
    internal static void CaptureCreatedAssets(string root, TOM.Model model, IEnumerable<string> originalFiles, CancellationToken token)
    {
        var roles = ReadRoles(model);
        var assets = new Dictionary<string, string>(roles.Assets, StringComparer.Ordinal);
        var originals = originalFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { "data", "config" })
        {
            var path = Path.Combine(root, folder);
            UserDataStore.Check(path);
            if (!Directory.Exists(path)) continue;
            var pending = new Stack<string>(); pending.Push(path);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested(); UserDataStore.Check(directory);
                foreach (var file in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested(); UserDataStore.Check(file);
                    if (Directory.Exists(file)) { pending.Push(file); continue; }
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!originals.Contains(relative)) assets[relative] = FileHash(file, token);
                }
            }
        }
        var configuration = Path.Combine(root, TemplateMeasureConfigurationFile.RelativePath);
        UserDataStore.Check(configuration);
        if (File.Exists(configuration) && !originals.Contains(TemplateMeasureConfigurationFile.RelativePath))
            assets[TemplateMeasureConfigurationFile.RelativePath] = FileHash(configuration, token);
        SetRoles(model, roles with { Assets = assets });
    }

    /// <summary>资产仅允许工程根下规范的 data/config 相对路径, 不接受反斜线、父级片段或驱动器语法.</summary>
    /// <param name="relative">使用正斜线分隔的工程内资产路径.</param>
    /// <returns>路径可由宿主在真实链接检查后解析时返回 true.</returns>
    internal static bool IsAssetPath(string relative) => !string.IsNullOrWhiteSpace(relative) && relative.Length <= 2048
        && !Path.IsPathRooted(relative) && !relative.Contains(':') && !relative.Contains('\\') && !relative.Any(char.IsControl)
        && !relative.Split('/').Any(part => part is "" or "." or "..")
        && (relative.StartsWith("data/", StringComparison.Ordinal) || relative.StartsWith("config/", StringComparison.Ordinal)
            || relative == TemplateMeasureConfigurationFile.RelativePath);
}
