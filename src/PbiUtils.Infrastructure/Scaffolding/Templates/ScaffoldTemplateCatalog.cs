// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\Templates\ScaffoldTemplateCatalog.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 读取内置及固定用户目录的受限 PBIP 模板快照.

using PbiUtils.Infrastructure.Models.Pbip;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Scaffolding.Templates;

/// <summary>模板只读发现边界, 不执行模板中的 M/DAX 或源连接.</summary>
public sealed class ScaffoldTemplateCatalog(string userRoot) : IPbipTemplateCatalog
{
    private const string ResourcePrefix = "PbiUtils.Scaffold/";
    private const int MaxFiles = 4096;
    private const long MaxBytes = 128L * 1024 * 1024;
    private readonly string templatesRoot = Path.GetFullPath(Path.Combine(userRoot, "templates", "pbip"));
    private static readonly string[] BuiltinNames = ["blank"];

    /// <summary>发现独立目录中的可选模板.</summary>
    public Task<IReadOnlyList<PbipTemplateInfo>> ListAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        List<PbipTemplateInfo> result = [];
        foreach (var name in BuiltinNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ReadBuiltin(name, cancellationToken).Info);
        }
        foreach (var folder in UserDirectories(cancellationToken))
        {
            var id = UserId(folder);
            try { result.Add((await ReadUserAsync(folder, id, cancellationToken).ConfigureAwait(false)).Info); }
            catch (AppException error)
            {
                // 空基线是不可选择诊断条目; 既不回传磁盘位置, 也不赋予路径访问权.
                result.Add(new(id, Path.GetFileName(folder), "user", "", "", [error.Code]));
            }
        }
        return (IReadOnlyList<PbipTemplateInfo>)result;
    }, cancellationToken);

    /// <summary>读取并冻结文件快照.</summary>
    public Task<ResolvedPbipTemplate> GetAsync(string id, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var name in BuiltinNames)
            if (id == "builtin:" + name) return ReadBuiltin(name, cancellationToken);
        foreach (var folder in UserDirectories(cancellationToken))
            if (UserId(folder) == id) return await ReadUserAsync(folder, id, cancellationToken).ConfigureAwait(false);
        throw Error("TEMPLATE_NOT_FOUND", "模板不存在, 请刷新目录");
    }, cancellationToken);

    /// <summary>只从程序集内的版本化固定资源读取, 不访问开发源码目录.</summary>
    private static ResolvedPbipTemplate ReadBuiltin(string name, CancellationToken token)
    {
        var assembly = typeof(ScaffoldTemplateCatalog).Assembly;
        var prefix = ResourcePrefix + name + "/";
        List<ScaffoldTemplateFile> files = [];
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using MemoryStream content = new();
            stream.CopyTo(content);
            files.Add(new(resource[prefix.Length..].Replace('\\', '/'), content.ToArray()));
        }
        var entry = ValidateBindings(files);
        var manifest = files.SingleOrDefault(f => f.RelativePath == "pbi-utils.template.json")
            ?? throw Error("TEMPLATE_RESOURCE_MISSING", "内置模板资源缺失");
        using var document = Parse(manifest.Content);
        var json = document.RootElement;
        var info = new PbipTemplateInfo("builtin:" + name, name, "builtin", Baseline(files),
            json.GetProperty("description").GetString()!, ["TEMPLATE_SOURCE_REQUIRES_LOCALIZATION"]);
        return new(info, entry, files);
    }

    /// <summary>固定用户目录每个直接子目录代表一个工程; 先检查祖先, 再有界枚举.</summary>
    private string[] UserDirectories(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureNoLinks(templatesRoot);
        if (!Directory.Exists(templatesRoot)) return [];
        var folders = Directory.EnumerateDirectories(templatesRoot).Take(129).Order(StringComparer.Ordinal).ToArray();
        if (folders.Length > 128) throw Error("TEMPLATE_LIMIT_EXCEEDED", "用户模板数量超过限制");
        return folders;
    }

    /// <summary>内容按清单读取两次并由共用官方读取器解析, 变化或绑定不完整时拒绝快照.</summary>
    private static async Task<ResolvedPbipTemplate> ReadUserAsync(string folder, string id, CancellationToken token)
    {
        try
        {
            // 目录身份与展示名称一致; 用户模板不能遮盖内置模板, 直接解析ID也必须拒绝冲突.
            if (BuiltinNames.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
                throw Error("TEMPLATE_NAME_CONFLICT", "用户模板目录名与内置模板冲突, 请重命名用户模板目录");
            var first = ReadTree(folder, token);
            var entry = ValidateBindings(first);
            var snapshot = await new ProjectReader().ReadAsync(Path.Combine(folder, entry), token).ConfigureAwait(false);
            var name = Path.GetFileName(folder);
            var description = "";
            var metadata = first.SingleOrDefault(f => f.RelativePath == "pbi-utils.template.json");
            if (metadata is not null)
            {
                using var document = Parse(metadata.Content);
                var json = document.RootElement;
                if (!json.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number) || number != 1)
                    throw Error("TEMPLATE_MANIFEST_INVALID", "模板描述版本不受支持");
                string[] allowed = ["schemaVersion", "name", "description", "source", "sourceFiles", "compatibilityLevel", "entryFile", "reportDirectory", "modelDirectory", "tables", "expressions", "relationships", "localization", "license"];
                if (json.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
                    throw Error("TEMPLATE_MANIFEST_INVALID", "模板描述包含未知字段");
                // 来源清单的显示名只校验类型, 用户模板的实际身份和名称始终取直接父目录.
                if (json.TryGetProperty("name", out var display)) _ = BoundedText(display, 200);
                if (json.TryGetProperty("description", out var detail)) description = BoundedText(detail, 2000);
            }
            var second = ReadTree(folder, token);
            var baseline = Baseline(first);
            if (baseline != Baseline(second)) throw Error("TEMPLATE_CHANGED", "模板已改变, 请重新预览");
            return new(new(id, name, "user", baseline, description, ["TEMPLATE_EXTERNAL_SOURCES_UNVERIFIED", .. snapshot.Warnings]), entry, first);
        }
        catch (AppException error) when (error.Code is "UNSAFE_PROJECT_PATH" or "REMOTE_PATH_UNSUPPORTED")
        { throw Error("TEMPLATE_PATH_UNSAFE", "模板路径不安全"); }
        catch (AppException) { throw; }
        catch (UnauthorizedAccessException) { throw Error("TEMPLATE_ACCESS_DENIED", "无法读取模板, 请检查权限"); }
        catch (IOException) { throw Error("TEMPLATE_CHANGED", "模板已改变或不可读取, 请重新预览"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException)
        { throw Error("TEMPLATE_BINDING_INVALID", "模板结构或本地模型绑定无效"); }
    }

    /// <summary>不跟随任何链接, 排除机器缓存; 单次最多4096文件和128MiB, 深度最多32层.</summary>
    private static List<ScaffoldTemplateFile> ReadTree(string root, CancellationToken token)
    {
        EnsureNoLinks(root);
        List<ScaffoldTemplateFile> files = [];
        Stack<(string Path, int Depth)> pending = new();
        pending.Push((root, 0));
        long total = 0;
        var directories = 0;
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(next.Path);
            if (next.Depth > 32 || ++directories > MaxFiles) throw Error("TEMPLATE_LIMIT_EXCEEDED", "模板目录超过限制");
            foreach (var path in Directory.EnumerateFileSystemEntries(next.Path))
            {
                token.ThrowIfCancellationRequested();
                EnsureNoLinks(path);
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (IsExcluded(relative)) continue;
                if (Directory.Exists(path)) { pending.Push((path, next.Depth + 1)); continue; }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 32L * 1024 * 1024 || total + stream.Length > MaxBytes || files.Count >= MaxFiles)
                    throw Error("TEMPLATE_LIMIT_EXCEEDED", "模板文件超过读取限制");
                using MemoryStream memory = new();
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    total += read;
                    if (total > MaxBytes || memory.Length + read > 32L * 1024 * 1024) throw Error("TEMPLATE_LIMIT_EXCEEDED", "模板文件超过读取限制");
                    memory.Write(buffer, 0, read);
                }
                EnsureNoLinks(path);
                files.Add(new(relative, memory.ToArray()));
            }
        }
        return [.. files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)];
    }

    /// <summary>排除准确的机器缓存路径, 保留.pbi/editorSettings等必要编辑设置.</summary>
    private static bool IsExcluded(string path)
    {
        var parts = path.Split('/');
        if (parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.Equals(".mimosa", StringComparison.OrdinalIgnoreCase))) return true;
        var name = parts[^1];
        return parts.Length >= 2 && parts[^2].Equals(".pbi", StringComparison.OrdinalIgnoreCase)
            && (name.Equals("cache.abf", StringComparison.OrdinalIgnoreCase) || name.Equals("localSettings.json", StringComparison.OrdinalIgnoreCase))
            || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用JSON路径语义验证PBIP→PBIR→本地PBISM, 不把schema URL当外部数据源.</summary>
    private static string ValidateBindings(IReadOnlyList<ScaffoldTemplateFile> files)
    {
        var entries = files.Where(f => f.RelativePath.EndsWith(".pbip", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length == 0) throw Error("TEMPLATE_ENTRY_MISSING", "模板缺少PBIP入口");
        if (entries.Length != 1 || entries[0].RelativePath.Contains('/')
            && !string.Equals(Path.GetDirectoryName(entries[0].RelativePath), "pbip", StringComparison.OrdinalIgnoreCase))
            throw Error("TEMPLATE_ENTRY_AMBIGUOUS", "模板需要根目录或 pbip 子目录内唯一 PBIP 入口");
        try
        {
            var lookup = files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
            using var project = Parse(entries[0].Content);
            RequireVersion(project.RootElement, ["1.0"]);
            var artifacts = project.RootElement.GetProperty("artifacts");
            if (artifacts.GetArrayLength() != 1) throw Error("TEMPLATE_BINDING_INVALID", "模板需要唯一报表绑定");
            var report = Resolve((Path.GetDirectoryName(entries[0].RelativePath) ?? "").Replace('\\', '/'), artifacts[0].GetProperty("report").GetProperty("path").GetString()!);
            using var definition = Parse(lookup[report + "/definition.pbir"].Content);
            RequireVersion(definition.RootElement, ["1.0", "4.0"]);
            var reference = definition.RootElement.GetProperty("datasetReference");
            if (!reference.TryGetProperty("byPath", out var byPath) || reference.TryGetProperty("byConnection", out _))
                throw Error("TEMPLATE_BINDING_INVALID", "模板必须绑定本地模型");
            var model = Resolve(report, byPath.GetProperty("path").GetString()!);
            using var pbism = Parse(lookup[model + "/definition.pbism"].Content);
            RequireVersion(pbism.RootElement, ["1.0", "4.0", "4.2"]);
            if (!files.Any(f => f.RelativePath.StartsWith(model + "/definition/", StringComparison.OrdinalIgnoreCase)) && !lookup.ContainsKey(model + "/model.bim"))
                throw Error("TEMPLATE_BINDING_INVALID", "模板缺少模型定义");
            return entries[0].RelativePath;
        }
        catch (AppException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw Error("TEMPLATE_BINDING_INVALID", "模板结构或本地模型绑定无效"); }
    }

    /// <summary>只解析相对片段, 允许模型位于报表兄弟目录, 不进行全局文本替换.</summary>
    private static string Resolve(string parent, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')) throw Error("TEMPLATE_PATH_UNSAFE", "模板绑定必须位于工程内");
        List<string> parts = [.. parent.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw Error("TEMPLATE_PATH_UNSAFE", "模板绑定不能超出工程");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        if (parts.Count == 0) throw Error("TEMPLATE_PATH_UNSAFE", "模板绑定不能指向工程根");
        return string.Join('/', parts);
    }

    /// <summary>检查所有已存在祖先, 不因目标暂时缺失而漏过父级链接.</summary>
    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Path.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Error("TEMPLATE_PATH_UNSAFE", "模板路径包含链接或重解析点");
        }
    }

    /// <summary>严格解析JSON并递归拒绝重复字段.</summary>
    private static JsonDocument Parse(byte[] content)
    {
        var document = JsonDocument.Parse(content.AsMemory().Span.StartsWith(Encoding.UTF8.Preamble) ? content.AsMemory(3) : content.AsMemory(), new() { MaxDepth = 64 });
        try { Unique(document.RootElement); return document; }
        catch { document.Dispose(); throw; }

        static void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw Error("TEMPLATE_BINDING_INVALID", "模板JSON包含重复字段");
                    Unique(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
        }
    }

    /// <summary>入口版本采用共用读取器当前支持的白名单.</summary>
    private static void RequireVersion(JsonElement element, string[] allowed)
    {
        if (!allowed.Contains(element.GetProperty("version").GetString(), StringComparer.Ordinal))
            throw Error("TEMPLATE_VERSION_UNSUPPORTED", "模板入口版本不受支持");
    }
    /// <summary>用户描述仅接受有界字符串, 不读取任意资源位置.</summary>
    private static string BoundedText(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw Error("TEMPLATE_MANIFEST_INVALID", "模板描述字段无效");
        return text;
    }
    /// <summary>稳定ID来源于固定根内目录身份, 摘要独立反映内容.</summary>
    private static string UserId(string path) => "user:" + Hash(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToUpperInvariant()));
    /// <summary>路径和内容按确定顺序计入摘要, 不依赖时间戳或开发机器位置.</summary>
    private static string Baseline(IEnumerable<ScaffoldTemplateFile> files) => Hash(Encoding.UTF8.GetBytes(string.Join('\n', files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => f.RelativePath + ":" + Hash(f.Content)))));
    /// <summary>统一使用小写SHA256内容摘要.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    /// <summary>安全错误只包含稳定代码及操作提示.</summary>
    private static AppException Error(string code, string message) => new(code, message, 3);
}
