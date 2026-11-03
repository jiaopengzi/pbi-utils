// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\Templates\ScaffoldTemplateMetadata.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 读取模板清单、实际 PBIP 绑定与页面配置, 不执行外部源或发布文件.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using static PbiUtils.Infrastructure.Scaffolding.ScaffoldModelFiles;

namespace PbiUtils.Infrastructure.Scaffolding.Templates;

/// <summary>只消费冻结模板字节并解析明确绑定, 不依赖额外脚手架清单.</summary>
internal static class ScaffoldTemplateMetadata
{
    /// <summary>从固定 PBIP/PBIR 绑定解析模型目录, 不凭文件夹名称猜测.</summary>
    internal static (string Model, string Report) Bindings(ResolvedPbipTemplate template, string staging)
    {
        using var project = ParseJson(template.Files.Single(f => f.RelativePath == template.EntryFile).Content);
        var reportRelative = project.RootElement.GetProperty("artifacts")[0].GetProperty("report").GetProperty("path").GetString()!;
        var report = Inside(staging, Path.Combine(Path.GetDirectoryName(template.EntryFile) ?? "", reportRelative));
        using var reportJson = ParseJson(File.ReadAllBytes(Path.Combine(report, "definition.pbir")));
        var model = Path.GetFullPath(Path.Combine(report, reportJson.RootElement.GetProperty("datasetReference").GetProperty("byPath").GetProperty("path").GetString()!));
        _ = Inside(staging, Path.GetRelativePath(staging, model));
        return (model, report);
    }
    /// <summary>页面名称与序号来自实际报告文件, 八项导航属性保持独立配置.</summary>
    internal static List<Dictionary<string, string>> PageConfiguration(ResolvedPbipTemplate template, string report, string staging)
    {
        var result = new List<Dictionary<string, string>>();
        var prefix = Path.GetRelativePath(staging, report).Replace('\\', '/') + "/definition/pages/";
        var orderFile = template.Files.SingleOrDefault(file => file.RelativePath == prefix + "pages.json");
        using var order = orderFile is null ? null : ParseJson(orderFile.Content);
        var names = order?.RootElement.TryGetProperty("pageOrder", out var values) == true ? values.EnumerateArray().Select(value => value.GetString()!).ToArray() : [];
        foreach (var file in template.Files.Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal) && file.RelativePath.EndsWith("/page.json", StringComparison.Ordinal)))
        {
            using var page = ParseJson(file.Content); var root = page.RootElement;
            var name = root.GetProperty("name").GetString()!;
            var display = root.GetProperty("displayName").GetString()!;
            var ordinal = Array.IndexOf(names, name);
            result.Add(new(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["displayName"] = display,
                ["ordinal"] = Math.Max(0, ordinal).ToString(CultureInfo.InvariantCulture),
                ["height"] = root.GetProperty("height").ToString(),
                ["width"] = root.GetProperty("width").ToString(),
                ["displayOption"] = root.TryGetProperty("displayOption", out var option) ? option.GetString()! : "FitToPage",
                ["visibility"] = root.TryGetProperty("visibility", out var visibility) ? visibility.GetString()! : "AlwaysVisible",
                ["verticalAlignment"] = "Middle",
                ["pageTitleText"] = display,
                ["pageTitleTextColor"] = "#1E2858",
                ["pageTitleBackgroundColor"] = "#FFFFFF",
                ["navigationButtonName"] = name,
                ["navigationButtonDisplayName"] = display,
                ["navigationButtonTextColorYes"] = "#FFFFFF",
                ["navigationButtonTextColorNo"] = "#1E2858",
                ["navigationButtonBackgroundColorYes"] = "#1E2858",
                ["navigationButtonBackgroundColorNo"] = "#FFFFFF",
                ["navigationButtonTooltipYes"] = display,
                ["navigationButtonTooltipNo"] = display,
                ["note"] = "",
            });
        }
        return result;
    }
    /// <summary>与模板读取器保持相同 UTF-8 BOM 边界, 不修改源字节.</summary>
    internal static JsonDocument ParseJson(byte[] content) => JsonDocument.Parse(content.AsMemory().Span.StartsWith(Encoding.UTF8.Preamble) ? content.AsMemory(3) : content.AsMemory());
    /// <summary>组合前后重新计算输入快照内容, 不修改调用方的字节数组.</summary>
    internal static string Fingerprint(ResolvedPbipTemplate template) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', template.Files.Select(file => file.RelativePath + ":" + Convert.ToHexString(SHA256.HashData(file.Content)))))));
}
