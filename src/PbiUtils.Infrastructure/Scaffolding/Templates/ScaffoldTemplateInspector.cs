// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\Templates\ScaffoldTemplateInspector.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 读取宿主绑定模板的真实模型角色, 不执行公式或读取外部数据源.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;

using PbiUtils.Application.Calendar;

namespace PbiUtils.Infrastructure.Scaffolding.Templates;

/// <summary>将受限快照释放到独占临时目录, 官方 TOM 只解析元数据.</summary>
public sealed class ScaffoldTemplateInspector(IPbipTemplateCatalog catalog, string userRoot)
{
    /// <summary>从冻结模板内存字节只读编译, 不展开临时目录、生成样例或准备创建事务.</summary>
    public Task<ScaffoldCalendarProgram> PreviewCalendarAsync(string id, string baseline, ScaffoldCalendarOptions options, string locale,
        CancellationToken token) => Task.Run(async () =>
    {
        var template = await catalog.GetAsync(id, token);
        if (template.Info.Baseline != baseline) throw new AppException("STALE_TEMPLATE", "模板内容已变化, 请刷新目录", 3);
        using var database = ReadFrozenDatabase(template, token);
        var builtin = template.Info.Source == "builtin";
        var roles = ScaffoldModelWriter.ReadRoles(template, database.Model, builtin,
            new(1, template.Info.Id, template.Info.Baseline, "preview", locale, null, null, options, null, null));
        if (builtin) ScaffoldModelWriter.LocalizeBuiltinExpressions(database.Model);
        var program = CalendarProgramPlanner.Preview(database, options, locale, token,
            builtin && options.TableName == "Calendar",
            trustedTemplateRole: !builtin && roles.GetValueOrDefault(options.TableName) == "calendar");
        token.ThrowIfCancellationRequested();
        if ((await catalog.GetAsync(id, token)).Info.Baseline != baseline) throw new AppException("STALE_TEMPLATE", "模板在预览期间已变化, 请刷新目录", 3);
        return program;
    }, token);

    /// <summary>以冻结 PBIP 入口所在目录解析本地绑定和文档, M 源和任何外部数据都不执行.</summary>
    /// <param name="template">目录已核验的完整冻结模板, 入口可位于固定 pbip 子目录.</param>
    /// <param name="token">逐文档解析前检查的取消令牌.</param>
    /// <returns>由调用方释放的完整私有数据库, 不产生磁盘文件.</returns>
    /// <exception cref="AppException">绑定超出模板范围或缺少所绑定的文档.</exception>
    private static TOM.Database ReadFrozenDatabase(ResolvedPbipTemplate template, CancellationToken token)
    {
        var lexicalRoot = Path.Combine(Path.GetTempPath(), "pbi-utils-template-memory");
        string Relative(string parent, string child) => Path.GetRelativePath(lexicalRoot, Resolve(lexicalRoot, Path.Combine(lexicalRoot, parent), child)).Replace('\\', '/');
        byte[] Read(string path) => template.Files.SingleOrDefault(file => file.RelativePath.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase))?.Content
            ?? throw new AppException("INVALID_TEMPLATE", "模板绑定的文档缺失", 3);
        using var entry = ScaffoldModelWriter.ParseJson(Read(Relative("", template.EntryFile)));
        var report = Relative(Path.GetDirectoryName(template.EntryFile) ?? "", entry.RootElement.GetProperty("artifacts")[0].GetProperty("report").GetProperty("path").GetString()!);
        using var binding = ScaffoldModelWriter.ParseJson(Read(report + "/definition.pbir"));
        var model = Relative(report, binding.RootElement.GetProperty("datasetReference").GetProperty("byPath").GetProperty("path").GetString()!);
        var prefix = model + "/definition/";
        var documents = template.Files.Where(file => file.RelativePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && file.RelativePath.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (documents.Length == 0) return TOM.JsonSerializer.DeserializeDatabase(System.Text.Encoding.UTF8.GetString(Read(model + "/model.bim")));
        var metadata = TOM.Serialization.MetadataSerializationContext.Create(TOM.Serialization.MetadataSerializationStyle.Tmdl);
        foreach (var document in documents)
        {
            token.ThrowIfCancellationRequested();
            var logicalPath = document.RelativePath.Replace('\\', '/')[prefix.Length..^5];
            using var content = new MemoryStream(document.Content, writable: false);
            metadata.ReadFromDocument(logicalPath, content);
        }
        return metadata.ToDatabase();
    }

    /// <summary>从真实 PBIP 入口相对绑定读取模型字段, 模板及基线一致时才返回, 不接收网页文件路径.</summary>
    /// <param name="id">宿主目录中稳定的模板身份.</param>
    /// <param name="baseline">本次请求绑定的完整模板内容摘要.</param>
    /// <param name="token">读取和逐文件展开中的取消令牌.</param>
    /// <returns>实际模型表列、默认目标和日期能力; 不执行任何数据源.</returns>
    /// <exception cref="AppException">模板过期、路径不安全或绑定内容无效.</exception>
    public Task<ScaffoldTemplateDetails> InspectAsync(string id, string baseline, CancellationToken token) => Task.Run(async () =>
    {
        var template = await catalog.GetAsync(id, token);
        if (template.Info.Baseline != baseline) throw new AppException("STALE_TEMPLATE", "模板内容已变化, 请刷新目录", 3);
        var temporary = Path.Combine(userRoot, "scaffold", "inspect", Guid.NewGuid().ToString("N"));
        UserDataStore.Check(temporary);
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var file in template.Files)
            {
                token.ThrowIfCancellationRequested();
                var target = Resolve(temporary, temporary, file.RelativePath);
                UserDataStore.Check(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, file.Content, token);
            }
            var entry = Resolve(temporary, temporary, template.EntryFile);
            using var project = JsonDocument.Parse(await File.ReadAllBytesAsync(entry, token));
            var report = Resolve(temporary, Path.GetDirectoryName(entry)!, project.RootElement.GetProperty("artifacts")[0].GetProperty("report").GetProperty("path").GetString()!);
            using var binding = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "definition.pbir"), token));
            var model = Resolve(temporary, report, binding.RootElement.GetProperty("datasetReference").GetProperty("byPath").GetProperty("path").GetString()!);
            using var database = Directory.Exists(Path.Combine(model, "definition")) ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(model, "definition"))
                : TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(model, "model.bim"), token));
            var tables = database.Model.Tables.Select(table => new ScaffoldTemplateTable(table.Name,
                table.Columns.Select(column => new ScaffoldTemplateColumn(column.Name, column.DataType.ToString())).ToArray(),
                table.Measures.Select(measure => measure.Name).ToArray(), table.DataCategory == "Time")).ToArray();
            var date = database.Model.Tables.FirstOrDefault(table => table.DataCategory == "Time");
            if (date is null && template.Info.Source == "builtin") date = database.Model.Tables.Find("Calendar");
            var key = date?.Columns.FirstOrDefault(column => column.IsKey && column.DataType == TOM.DataType.DateTime)
                ?? date?.Columns.FirstOrDefault(column => column.DataType == TOM.DataType.DateTime);
            return new ScaffoldTemplateDetails(tables, date?.Name, key?.Name, database.Model.Tables.Find("Measure")?.Name
                ?? database.Model.Tables.FirstOrDefault(table => table.Measures.Count > 0)?.Name,
                ScaffoldCalendarFields.All,
                database.Model.Tables.Select(table => ScaffoldCalendarCapabilities.Describe(table,
                    template.Info.Source == "builtin" && table.Name == "Calendar"))
                    .OfType<ScaffoldCalendarCapability>().ToArray());
        }
        finally { DeleteOwned(temporary); }
    }, token);

    /// <summary>只解析模板内相对绑定, 不接受绝对或根外路径.</summary>
    private static string Resolve(string root, string parent, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new AppException("UNSAFE_TEMPLATE_PATH", "模板相对绑定无效", 3);
        var path = Path.GetFullPath(Path.Combine(parent, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new AppException("UNSAFE_TEMPLATE_PATH", "模板绑定超出工程范围", 3);
        return path;
    }

    /// <summary>逐节点复核后清理唯一临时树, 不跟随链接.</summary>
    private static void DeleteOwned(string directory)
    {
        UserDataStore.Check(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var child in Directory.GetFileSystemEntries(directory))
        {
            UserDataStore.Check(child);
            if (Directory.Exists(child)) DeleteOwned(child); else File.Delete(child);
        }
        UserDataStore.Check(directory);
        Directory.Delete(directory);
    }
}
