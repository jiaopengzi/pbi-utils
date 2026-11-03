// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\TemplateMeasures\TemplateMeasureConfigurationStorage.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 模板度量配置的受保护模型状态、业务 JSON 与冻结资产生命周期.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Input;
using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.SampleData.Model;

using PbiUtils.Application.Input;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Infrastructure.TemplateMeasures;

/// <summary>共享创建、离线配置与宿主已核实的 Desktop 业务资产, 不解析网页路径.</summary>
public static class TemplateMeasureConfigurationStorage
{
    private const int MaximumJsonBytes = TemplateMeasureConfigurationFile.MaximumBytes;
    internal const string Annotation = "PbiUtils.TemplateMeasures.v1";
    internal const string JsonPath = TemplateMeasureConfigurationFile.RelativePath;
    internal const string DefaultTable = "PbiUtilsTemplateMeasures";
    internal static readonly string[] Columns = ["name", "value", "dataCategory", "status", "description", "displayFolder"];

    /// <summary>从宿主已绑定的本机文件解析配置根, 不读取 PBIX 内容或模型内路径参数.</summary>
    /// <param name="projectPath">已存在的 PBIP 或 PBIX 入口; 官方 PBIR 绑定须先解析为对应 PBIP.</param>
    /// <returns>PBIP 沿用固定 pbip 子目录的父根, 普通 PBIP 和 PBIX 使用入口父目录.</returns>
    /// <exception cref="AppException">入口不是本机普通模型文件, 或路径含链接.</exception>
    public static string ProjectRoot(string projectPath)
    {
        var entry = Path.GetFullPath(projectPath);
        if (!Path.IsPathFullyQualified(projectPath) || entry.StartsWith("\\\\", StringComparison.Ordinal))
            throw new AppException("UNSAFE_PROJECT_PATH", "需要宿主已核实的本机模型文件", 3);
        if (Path.GetExtension(entry).Equals(".pbip", StringComparison.OrdinalIgnoreCase)) return PbipProjectLayout.ProjectRoot(entry);
        if (!Path.GetExtension(entry).Equals(".pbix", StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
            throw new AppException("LOCAL_MODEL_FILE_UNSUPPORTED", "需要已存在的 PBIP 或 PBIX 入口", 3);
        UserDataStore.Check(entry);
        return Path.GetDirectoryName(entry)!;
    }

    /// <summary>从完整受信 TOM 提取角色、资产摘要与配置; 由调用方核实工程根和官方文件绑定.</summary>
    /// <param name="database">与权威完整基线一致的只读模型.</param>
    /// <param name="root">宿主官方文件绑定确定的工程根.</param>
    /// <param name="projectPath">宿主绑定的具体 PBIP/PBIX 入口, 用于核实固定配置根.</param>
    /// <param name="verify">继续保存前重新核实同一绑定, 不执行保存或重载.</param>
    /// <returns>仅供宿主内部使用的资产能力.</returns>
    /// <exception cref="AppException">目标、配置读取基线或文件系统访问边界无效.</exception>
    public static ModelConfigurationAssetContext ReadAssetContext(TOM.Database database, string root, string projectPath, Func<CancellationToken, Task> verify)
    {
        var ownership = ModelConfigurationOwnership.ReadForTemplate(database.Model);
        var state = TemplateMeasureConfigurationFile.Read(root, projectPath);
        async Task Verify(CancellationToken token)
        {
            await verify(token).ConfigureAwait(false);
            var current = TemplateMeasureConfigurationFile.Read(root, projectPath);
            if (current.Available != state.Available || current.Revision != state.Revision)
                throw new AppException("STALE_BASELINE", "业务配置在读取后已变化, 请重新读取后重试", 3);
        }
        return new(root, new Dictionary<string, string>(ownership.Assets, StringComparer.Ordinal), Verify,
            ownership.Roles.Where(pair => pair.Value == "measure-config").OrderByDescending(pair => database.Model.Tables.ContainsName(pair.Key))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault().Key, state.Configuration, state.Available, state.Revision, state.UnavailableReason);
    }

    /// <summary>读取模板清单供显示和显式列表删除使用, 不以父表的历史归属限制用户本轮覆盖.</summary>
    internal static TemplateMeasureConfiguration? Read(TOM.Model model)
    {
        var table = model.Tables.Find("Measure");
        if (table?.Annotations.Find(Annotation) is not { } annotation) return null;
        try
        {
            using var document = JsonDocument.Parse(annotation.Value);
            JsonInputValidation.RejectDuplicates(document.RootElement);
            var configuration = document.RootElement.Deserialize<TemplateMeasureConfiguration>(InputJson.CreateStrict())
                ?? throw new JsonException();
            if (configuration.Measures is null) throw new JsonException();
            TemplateMeasureValidation.ValidateMode(configuration.Mode);
            TemplateMeasureValidation.Validate(configuration.Measures);
            return configuration;
        }
        catch (JsonException) { throw new AppException("CONFIGURATION_OWNERSHIP_INVALID", "模板度量配置归属损坏, 未修改模型", 3); }
    }

    /// <summary>损坏或缺少模板清单返回 null, 本轮明确配置仍可重建而不猜测删除旧对象.</summary>
    internal static TemplateMeasureConfiguration? Display(TOM.Model model)
    {
        try { return Read(model); }
        catch (AppException) { return null; }
    }

    /// <summary>将明确生成的六字段状态写入父表, 与完整可写定义共同签名.</summary>
    internal static void WriteState(TOM.Model model, string mode, TemplateMeasureDefinition[] definitions)
    {
        var table = model.Tables["Measure"];
        if (table.Annotations.Find(Annotation) is { } prior) table.Annotations.Remove(prior);
        table.Annotations.Add(new TOM.Annotation { Name = Annotation, Value = JsonSerializer.Serialize(new TemplateMeasureConfiguration(mode, definitions), InputJson.CreateStrict()) });
        ModelConfigurationOwnership.Mark(table, table.Annotations, includeChildren: false);
    }

    /// <summary>保留其他业务 JSON 段, 只替换或移除模板度量段; 无其他段时返回 null 表示删除文件.</summary>
    /// <param name="before">已完成结构和本轮完整字节核验的原 JSON, 无文件时为 null.</param>
    /// <param name="mode">已通过白名单的存储模式.</param>
    /// <param name="definitions">本轮完整六字段输入, 包含禁用条目.</param>
    /// <returns>最终规范 UTF-8 内容, 或不再需要文件时的 null.</returns>
    /// <exception cref="AppException">原 JSON 损坏或版本不受支持时保留原文件并拒绝覆盖.</exception>
    internal static byte[]? JsonBytes(byte[]? before, string mode, TemplateMeasureDefinition[] definitions)
    {
        if (before is { Length: > MaximumJsonBytes }) throw AssetLimit();
        JsonObject content;
        try
        {
            if (before is null) content = new JsonObject { ["schemaVersion"] = 1 };
            else
            {
                using var document = JsonDocument.Parse(before);
                JsonInputValidation.RejectDuplicates(document.RootElement);
                content = JsonNode.Parse(before)?.AsObject() ?? throw new JsonException();
                if (content["schemaVersion"]?.GetValue<int>() != 1) throw new JsonException();
            }
            if (mode == "config")
                content["MeasureTemplates"] = JsonSerializer.SerializeToNode(definitions.Select(value => new
                {
                    name = value.Name,
                    value = value.Value,
                    dataCategory = value.DataCategory,
                    status = value.Enabled,
                    description = value.Description,
                    displayFolder = value.DisplayFolder
                }));
            else if (!content.Remove("MeasureTemplates")) return before;
            if (content.Count == 1 && mode == "tmdl") return null;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(content);
            // 默认 JSON 编码会放大 < 等字符, 以实际输出字节核限, 保证本工具生成的配置可再次读取.
            if (bytes.Length > MaximumJsonBytes) throw AssetLimit();
            return bytes;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new AppException("CONFIGURATION_ASSET_CONFLICT", "业务配置 JSON 损坏或版本不受支持, 原文件已保留", 3); }
    }

    /// <summary>按精确原 hash 冻结 JSON 更新及旧参数 CSV 删除, 不改动最终工程.</summary>
    internal static IReadOnlyList<ModelConfigurationAsset> PrepareAssets(string stage, ModelConfigurationAssetContext context,
        string mode, TemplateMeasureDefinition[] definitions, CancellationToken token)
    {
        var result = new List<ModelConfigurationAsset>();
        byte[]? before = null;
        if (context.CanUseMeasureConfig)
        {
            var path = ModelConfigurationCompiler.AssetPath(context.Root, JsonPath);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaximumJsonBytes) throw AssetLimit();
                before = File.ReadAllBytes(path);
            }
        }
        var hash = before is null ? null : ModelConfigurationOwnership.Hash(before);
        if (context.CanUseMeasureConfig && context.ConfigurationRevision is { } revision
            && revision != (hash ?? TemplateMeasureConfigurationFile.MissingRevision))
            throw new AppException("STALE_BASELINE", "业务配置在读取后已变化, 请重新读取后重试", 3);
        // 不合约定、损坏或未知文件均不参与生命周期; 有效公共配置仅以本轮读取修订控制并发.
        var bytes = !context.CanUseMeasureConfig || mode == "tmdl" && before is null ? null : JsonBytes(before, mode, definitions);
        if (bytes is not null)
        {
            var target = ModelConfigurationCompiler.AssetPath(stage, JsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            result.Add(new(JsonPath, target, hash, ModelConfigurationOwnership.Hash(bytes), bytes.LongLength));
        }
        else if (hash is not null) result.Add(new(JsonPath, null, hash, null, 0));
        // 通用资产 hash 不证明业务角色, 没有明确参数表角色时不能按默认文件名退休其他功能的 CSV.
        if (context.ConfigTableName is not { } configName) return result;
        var csv = "data/config/" + ScaffoldDataWriter.FileName(configName);
        var oldCsv = ModelConfigurationCompiler.AssetPath(context.Root, csv);
        if (File.Exists(oldCsv))
        {
            token.ThrowIfCancellationRequested();
            var actual = ModelConfigurationOwnership.FileHash(oldCsv, token);
            // 旧 CSV 已不参与当前 JSON 加载, 外部改写或缺失只保留文件, 不阻挡明确模式切换.
            if (context.Assets.GetValueOrDefault(csv) == actual) result.Add(new(csv, null, actual, null, 0));
        }
        return result;
    }

    /// <summary>读写共用字节预算错误, 不泄露业务正文或文件路径.</summary>
    private static AppException AssetLimit() => new("CONFIGURATION_ASSET_LIMIT", "模板配置 JSON 超过允许大小, 请减少条目或内容", 2);

    /// <summary>固定六字段 JSON 直接加载, 空数组仍保留完整列与类型, 不读取其他业务段.</summary>
    /// <returns>完整固定 M 查询, 不附加尾换行或在此执行文件读取.</returns>
    internal static string Partition() => """
        let
            Source = Json.Document(File.Contents(PbiUtilsProjectRoot & "/pbi-utils/config.json")),
            Rows = Source[MeasureTemplates],
            Result = Table.FromRecords(Rows, type table [name = text, value = text, dataCategory = text, status = logical, description = text, displayFolder = text])
        in
            Result
        """;
}
