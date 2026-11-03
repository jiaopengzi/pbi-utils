// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldOutputLayout.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 根据真实 PBIP/PBIR 绑定映射新建工程布局, 保留模板原字节与业务引用.

using System.Text.Json;
using System.Text.Json.Nodes;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Scaffolding.Templates;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>只转换冻结快照的路径和两处绑定, 不改模板源文件或模型业务文本.</summary>
internal static class ScaffoldOutputLayout
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>将唯一入口及其绑定目录映射到 pbip 子目录, 其他文件保留相对于工程根的位置.</summary>
    /// <param name="template">已验证结构和内容基线的模板快照.</param>
    /// <param name="projectName">通过脚手架名称校验的项目名.</param>
    /// <param name="staging">用于相对路径安全计算的独占目录, 此方法不写盘.</param>
    /// <returns>复用来源身份、使用新文件路径及绑定的快照.</returns>
    /// <exception cref="AppException">绑定越界或输出路径与模板其他内容冲突.</exception>
    internal static ResolvedPbipTemplate Map(ResolvedPbipTemplate template, string projectName, string staging)
    {
        var entry = template.Files.Single(file => file.RelativePath == template.EntryFile);
        var project = Parse(entry.Content);
        var reportNode = project["artifacts"]![0]!["report"]!;
        var report = Resolve(Path.GetDirectoryName(template.EntryFile) ?? "", reportNode["path"]!.GetValue<string>());
        var reportFile = report + "/definition.pbir";
        var definition = Parse(template.Files.Single(file => file.RelativePath.Equals(reportFile, StringComparison.OrdinalIgnoreCase)).Content);
        var modelNode = definition["datasetReference"]!["byPath"]!;
        var model = Resolve(report, modelNode["path"]!.GetValue<string>());
        var newEntry = "pbip/" + projectName + ".pbip";
        var newReport = "pbip/" + projectName + ".Report";
        var newModel = "pbip/" + projectName + ".SemanticModel";
        var sourceIgnore = Path.Combine(Path.GetDirectoryName(template.EntryFile) ?? "", ".gitignore").Replace('\\', '/');
        reportNode["path"] = projectName + ".Report";
        modelNode["path"] = "../" + projectName + ".SemanticModel";
        var files = template.Files.Select(file => new ScaffoldTemplateFile(MapPath(file.RelativePath),
            file.RelativePath == template.EntryFile ? Serialize(project)
                : file.RelativePath.Equals(reportFile, StringComparison.OrdinalIgnoreCase) ? Serialize(definition) : file.Content)).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            _ = ScaffoldModelFiles.Inside(staging, file.RelativePath);
            if (!paths.Add(file.RelativePath)) throw Conflict();
        }
        foreach (var file in files)
            for (var parent = Path.GetDirectoryName(file.RelativePath); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (paths.Contains(parent.Replace('\\', '/'))) throw Conflict();
        return template with { EntryFile = newEntry, Files = files };

        string Resolve(string parent, string relative) => Path.GetRelativePath(staging,
            ScaffoldModelFiles.Inside(staging, Path.Combine(parent, relative))).Replace('\\', '/');

        string MapPath(string path) => path == template.EntryFile ? newEntry
            : path.Equals(sourceIgnore, StringComparison.OrdinalIgnoreCase) ? "pbip/.gitignore"
            : path.StartsWith(report + "/", StringComparison.OrdinalIgnoreCase) ? newReport + path[report.Length..]
            : path.StartsWith(model + "/", StringComparison.OrdinalIgnoreCase) ? newModel + path[model.Length..] : path;
    }

    /// <summary>沿用模板读取器的 BOM 边界, 只解析需要修改路径的 JSON.</summary>
    private static JsonNode Parse(byte[] bytes)
    {
        using var document = ScaffoldTemplateMetadata.ParseJson(bytes);
        return JsonNode.Parse(document.RootElement.GetRawText())!;
    }

    /// <summary>输出完整 JSON 属性集合, 不对名称、表达式或任意业务字符串执行替换.</summary>
    private static byte[] Serialize(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node, Json);

    /// <summary>模板未知同名文件仍属于用户, 不通过覆盖或跳过消除冲突.</summary>
    private static AppException Conflict() => new("SCAFFOLD_OUTPUT_CONFLICT", "项目布局与模板已有文件冲突, 未覆盖模板内容", 3);
}
