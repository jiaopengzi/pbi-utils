// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\TemplateMeasures\TemplateMeasureConfigurationFile.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从宿主绑定的 PBIP/PBIX 固定根读取业务配置, 缺目录可首次创建并以文件修订控制并发.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Infrastructure.TemplateMeasures;

/// <summary>只访问宿主绑定的固定配置文件; 不搜索祖先、旧路径或读取模型中的路径参数.</summary>
internal static class TemplateMeasureConfigurationFile
{
    internal const string RelativePath = "pbi-utils/config.json";
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal const string MissingRevision = "missing";

    /// <summary>配置文件读取结果, 不可用时不授予文件创建、覆盖或删除能力.</summary>
    internal sealed record State(bool Available, string? Revision, TemplateMeasureConfiguration? Configuration, string? UnavailableReason = null);

    /// <summary>核实入口与配置根一致, 缺目录或文件返回可创建的 missing 修订, 读取不产生目录或模型写入.</summary>
    /// <param name="root">宿主核实的工程根.</param>
    /// <param name="projectPath">宿主绑定的已存在 PBIP/PBIX 入口.</param>
    /// <returns>目录或文件缺失仍可首次创建; 根不符、路径类型冲突、文件损坏、超限或不支持的结构均返回不可用.</returns>
    /// <exception cref="IOException">目录或文件无法稳定读取, 调用方不得假设文件缺失.</exception>
    internal static State Read(string root, string projectPath)
    {
        var directory = Path.Combine(root, "pbi-utils");
        var path = Path.Combine(root, RelativePath);
        if (!File.Exists(projectPath) || !new[] { ".pbip", ".pbix" }.Contains(Path.GetExtension(projectPath), StringComparer.OrdinalIgnoreCase))
            return new(false, null, null, "layout");
        try
        {
            if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(TemplateMeasureConfigurationStorage.ProjectRoot(projectPath), StringComparison.OrdinalIgnoreCase))
                return new(false, null, null, "layout");
            UserDataStore.Check(projectPath); UserDataStore.Check(directory); UserDataStore.Check(path);
            if (File.Exists(directory) || Directory.Exists(path)) return new(false, null, null, "invalid");
            if (!File.Exists(path)) return new(true, MissingRevision, null);
            if (new FileInfo(path).Length > MaximumBytes) return new(false, null, null, "invalid");
            var bytes = File.ReadAllBytes(path);
            var configuration = Parse(bytes);
            return new(true, ModelConfigurationOwnership.Hash(bytes), configuration);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or AppException)
        { return new(false, null, null, "invalid"); }
    }

    /// <summary>严格读取 MeasureTemplates 六字段, 重复键、未知字段、非法配置或超限均失败.</summary>
    /// <param name="bytes">本轮完整 UTF-8 文件内容.</param>
    /// <returns>Config 模式的完整显式配置, 包括禁用条目.</returns>
    /// <exception cref="AppException">内容超过预算或配置字段不满足共享输入契约.</exception>
    /// <exception cref="JsonException">JSON 或六字段结构损坏, 不读取旧字段作为回退.</exception>
    internal static TemplateMeasureConfiguration Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new AppException("CONFIGURATION_ASSET_LIMIT", "模板配置 JSON 超过允许大小, 请减少条目或内容", 2);
        using var document = JsonDocument.Parse(bytes);
        JsonInputValidation.RejectDuplicates(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("schemaVersion", out var version)
            || !version.TryGetInt32(out var schema) || schema != 1) throw new JsonException();
        if (!document.RootElement.TryGetProperty("MeasureTemplates", out var rows)) return new("config", []);
        if (rows.ValueKind != JsonValueKind.Array) throw new JsonException();
        var values = new List<TemplateMeasureDefinition>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 6
                || row.EnumerateObject().Any(field => !TemplateMeasureConfigurationStorage.Columns.Contains(field.Name, StringComparer.Ordinal))) throw new JsonException();
            /// <summary>读取六字段中必需的非 null 字符串, 不进行隐式类型转换.</summary>
            string Text(string name) => row.GetProperty(name).ValueKind == JsonValueKind.String ? row.GetProperty(name).GetString()! : throw new JsonException();
            var status = row.GetProperty("status");
            if (status.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            values.Add(new(Text("name"), Text("value"), Text("dataCategory"), status.GetBoolean(), Text("description"), Text("displayFolder")));
        }
        var definitions = values.ToArray();
        TemplateMeasureValidation.Validate(definitions);
        return new("config", definitions);
    }
}
