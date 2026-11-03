// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Pbip\ProjectReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以受限本地路径和官方 TOM 读取 PBIP, TMDL/TMSL 及 PBIR 清单.

using PbiUtils.Application.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Infrastructure.TemplateMeasures;

using PbiUtils.Infrastructure.Models;

namespace PbiUtils.Infrastructure.Models.Pbip;

/// <summary>离线只读适配器; 不连接引擎, 不执行公式, 不写入源工程.</summary>
public sealed class ProjectReader : IProjectReader
{
    /// <summary>在线程池中执行同步 TOM 解析, 在边界检查取消.</summary>
    public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => Task.Run(() => Read(projectPath, cancellationToken), cancellationToken);

    /// <summary>解析本地文件并映射稳定错误; 内部路径和源内容不进入错误消息.</summary>
    private static ProjectSnapshot Read(string path, CancellationToken token)
    {
        try { return ReadCore(path, token); }
        catch (AppException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { throw new AppException("PROJECT_ACCESS_DENIED", "无法读取所选工程, 请检查权限", 3); }
        catch (IOException) { throw new AppException("PROJECT_IO_ERROR", "工程读取失败或文件正在变化", 3); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new AppException("INVALID_PROJECT", "工程内容无效或格式不受支持", 2); }
    }

    /// <summary>从显式入口识别绑定和版本, 验证前后内容一致后返回快照.</summary>
    private static ProjectSnapshot ReadCore(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal)) throw new AppException("REMOTE_PATH_UNSUPPORTED", "仅支持本机工程路径", 3);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new AppException("PROJECT_NOT_FOUND", "工程路径不存在", 3);
        EnsureNoLinks(full);
        var root = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        var scan = new Scan(root, token);
        string? entry = File.Exists(full) ? full : null;
        if (entry is null)
        {
            var candidates = Directory.GetFiles(root, "*.pbip", SearchOption.TopDirectoryOnly);
            if (candidates.Length > 1) throw new AppException("AMBIGUOUS_PROJECT", "存在多个 PBIP 入口, 请选择具体文件", 2);
            entry = candidates.SingleOrDefault();
        }
        string? report = null;
        string model;
        string? reportVersion = null;
        var reportFormat = "none";
        List<string> warnings = ["离线结构读取不代表 DAX/M 执行或 Desktop 验收通过"];
        if (entry is not null)
        {
            if (!string.Equals(Path.GetExtension(entry), ".pbip", StringComparison.OrdinalIgnoreCase))
                throw new AppException("UNSUPPORTED_ENTRY", "请选择 .pbip 文件或模型/报表目录", 2);
            var project = scan.Json(entry);
            RequireVersion(project, ["1.0"], "PBIP");
            var artifacts = project.GetProperty("artifacts");
            if (artifacts.GetArrayLength() != 1) throw new AppException("AMBIGUOUS_PROJECT", "需要唯一报表入口", 2);
            report = scan.Resolve(root, artifacts[0].GetProperty("report").GetProperty("path").GetString()!);
        }
        else if (File.Exists(Path.Combine(root, "definition.pbir"))) report = root;
        if (report is not null)
        {
            var definition = scan.Json(Path.Combine(report, "definition.pbir"));
            reportVersion = RequireVersion(definition, ["1.0", "4.0"], "PBIR");
            var binding = definition.GetProperty("datasetReference");
            if (!binding.TryGetProperty("byPath", out var byPath) || binding.TryGetProperty("byConnection", out _))
                throw new AppException("REMOTE_MODEL_UNSUPPORTED", "仅支持唯一的本地相对路径模型绑定", 3);
            model = scan.Resolve(report, byPath.GetProperty("path").GetString()!);
            var enhanced = Directory.Exists(Path.Combine(report, "definition"));
            var legacy = File.Exists(Path.Combine(report, "report.json"));
            if (enhanced == legacy) throw new AppException("AMBIGUOUS_FORMAT", "报表定义缺失或同时存在两种格式", 2);
            reportFormat = enhanced ? "PBIR" : "legacy";
            if (enhanced && reportVersion != "4.0") throw new AppException("UNSUPPORTED_VERSION", "增强 PBIR 需要已支持的 4.0 入口", 3);
            if (legacy) warnings.Add("legacy 报表仅识别格式, 不读取或修改元素");
        }
        else model = root;
        var modelDefinition = scan.Json(Path.Combine(model, "definition.pbism"));
        var modelVersion = RequireVersion(modelDefinition, ["1.0", "4.0", "4.2"], "PBISM");
        var tmdl = Directory.Exists(Path.Combine(model, "definition"));
        var tmsl = File.Exists(Path.Combine(model, "model.bim"));
        if (tmdl == tmsl) throw new AppException("AMBIGUOUS_FORMAT", "模型定义缺失或同时存在 TMDL 与 TMSL", 2);
        if (tmdl && modelVersion == "1.0") throw new AppException("UNSUPPORTED_VERSION", "当前 TMDL 读取需要 PBISM 4.0 或 4.2", 3);
        if (File.Exists(Path.Combine(model, ".pbi", "unappliedChanges.json")))
            warnings.Add("存在未应用的 Power Query 更改, 禁止据此快照写入");
        // 官方解析器只接收已检查过的定义目录; 所有子路径均先检查链接和大小.
        if (tmdl) scan.Tree(Path.Combine(model, "definition"));
        else scan.Bytes(Path.Combine(model, "model.bim"));
        TOM.Database database;
        try
        {
            database = tmdl ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(model, "definition"))
                : TOM.JsonSerializer.DeserializeDatabase(Encoding.UTF8.GetString(scan.Bytes(Path.Combine(model, "model.bim"))).TrimStart('\uFEFF'));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        { throw new AppException("MODEL_PARSE_FAILED", "官方模型解析器拒绝了定义; 请在 Desktop 检查格式、版本和引用", 2); }
        using (database)
        {
            token.ThrowIfCancellationRequested();
            _ = database.Model ?? throw new AppException("MODEL_MISSING", "工程没有模型定义");
            var modelInfo = ModelSnapshotMapper.Map(database);
            var pages = reportFormat == "PBIR" ? ReadPages(scan, report!) : [];
            var files = scan.Verify();
            var baseline = Hash(Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => f.Path + ":" + f.Sha256))));
            return new(entry ?? full, Hash(Encoding.UTF8.GetBytes(entry ?? full)), baseline, tmdl ? "TMDL" : "TMSL",
                reportFormat, modelVersion, reportVersion, modelInfo, pages, files, warnings);
        }
    }

    /// <summary>只读 PBIR 页面和视觉身份, 未知 Schema 返回警告式阻断而非猜测.</summary>
    private static PageInfo[] ReadPages(Scan scan, string report)
    {
        var definition = Path.Combine(report, "definition");
        scan.Json(Path.Combine(definition, "report.json"));
        var pagesRoot = Path.Combine(definition, "pages");
        var index = scan.Json(Path.Combine(pagesRoot, "pages.json"));
        var order = index.GetProperty("pageOrder").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (order.Distinct(StringComparer.Ordinal).Count() != order.Length) throw new AppException("INVALID_REPORT_REFERENCE", "报表页面 ID 重复");
        return [.. order.Select(id =>
        {
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['/', '\\', ':']) >= 0 || id is "." or "..")
                throw new AppException("INVALID_REPORT_REFERENCE", "页面 ID 无效");
            var directory = scan.Resolve(pagesRoot, id);
            var page = scan.Json(Path.Combine(directory, "page.json"));
            if (page.GetProperty("name").GetString() != id) throw new AppException("INVALID_REPORT_REFERENCE", "页面 ID 与目录不一致");
            var visualsRoot = Path.Combine(directory, "visuals");
            List<VisualInfo> visuals = [];
            if (Directory.Exists(visualsRoot))
            {
                EnsureNoLinks(visualsRoot);
                foreach (var visualDirectory in Directory.GetDirectories(visualsRoot))
                {
                    var visual = scan.Json(Path.Combine(visualDirectory, "visual.json"));
                    var visualId = visual.GetProperty("name").GetString()!;
                    if (visualId != Path.GetFileName(visualDirectory)) throw new AppException("INVALID_REPORT_REFERENCE", "视觉 ID 与目录不一致");
                    var position = visual.TryGetProperty("position", out var p) ? p : default;
                    visuals.Add(new(visualId, visual.TryGetProperty("visual", out var v) && v.TryGetProperty("visualType", out var type) ? type.GetString()! : "unknown",
                        Number(position, "x"), Number(position, "y"), Number(position, "width"), Number(position, "height")));
                }
            }
            return new PageInfo(id, page.GetProperty("displayName").GetString()!, Number(page, "width"), Number(page, "height"), visuals);
        })];
    }

    /// <summary>保持未定义坐标为 null, 不捏造默认位置.</summary>
    private static double? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var n) ? n.GetDouble() : null;
    /// <summary>只接受已明确支持的入口版本.</summary>
    private static string RequireVersion(JsonElement value, string[] versions, string label)
    {
        var version = value.GetProperty("version").GetString();
        if (version is null || !versions.Contains(version)) throw new AppException("UNSUPPORTED_VERSION", $"{label} 版本不受支持", 3);
        return version;
    }
    /// <summary>计算内容指纹.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    /// <summary>拒绝目标及祖先的链接和 reparse point, 不跟随目录跳转.</summary>
    private static void EnsureNoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AppException("UNSAFE_PROJECT_PATH", "工程路径包含链接或重解析点", 3);
    }

    /// <summary>一次读取的有界路径和内容清单.</summary>
    private sealed class Scan(string root, CancellationToken token)
    {
        private readonly Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        private long total;

        /// <summary>解析相对绑定, 允许工程内兄弟目录, 拒绝绝对路径和根外访问.</summary>
        public string Resolve(string from, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
                throw new AppException("UNSAFE_PROJECT_PATH", "绑定必须是工程内相对路径", 3);
            var result = Path.GetFullPath(Path.Combine(from, relative.Replace('/', Path.DirectorySeparatorChar)));
            Check(result);
            return result;
        }
        /// <summary>验证根内位置和所有祖先.</summary>
        private void Check(string path)
        {
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new AppException("UNSAFE_PROJECT_PATH", "绑定超出所选工程目录", 3);
            EnsureNoLinks(path);
        }
        /// <summary>读取最多 8 MiB 的定义文件, 每次扫描最多 64 MiB / 4096 文件.</summary>
        public byte[] Bytes(string path)
        {
            token.ThrowIfCancellationRequested();
            Check(path);
            var size = new FileInfo(path).Length;
            if (size > 8 * 1024 * 1024 || total + size > 64 * 1024 * 1024 || hashes.Count >= 4096)
                throw new AppException("PROJECT_TOO_LARGE", "工程超出当前读取大小限制", 3);
            var bytes = File.ReadAllBytes(path);
            var hash = Hash(bytes);
            if (hashes.TryGetValue(path, out var previous) && hash != previous) throw new AppException("STALE_BASELINE", "读取期间工程已改变, 请重试", 3);
            if (!hashes.ContainsKey(path)) total += bytes.Length;
            hashes[path] = hash;
            return bytes;
        }
        /// <summary>有界 JSON 解析, 禁止重复字段避免含糊绑定.</summary>
        public JsonElement Json(string path)
        {
            var bytes = Bytes(path);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 64 });
            RejectDuplicates(doc.RootElement);
            return doc.RootElement.Clone();
        }
        /// <summary>递归验证 JSON 对象唯一键.</summary>
        private static void RejectDuplicates(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = [];
                foreach (var p in value.EnumerateObject())
                {
                    if (!names.Add(p.Name)) throw new AppException("INVALID_PROJECT", "JSON 包含重复字段");
                    RejectDuplicates(p.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
        }
        /// <summary>逐级检查定义目录, 不通过递归枚举跟随链接.</summary>
        public void Tree(string directory)
        {
            Check(directory);
            foreach (var file in Directory.GetFiles(directory)) Bytes(file);
            foreach (var child in Directory.GetDirectories(directory)) Tree(child);
        }
        /// <summary>读取后复核已读文件 hash, 返回按相对路径排序的清单.</summary>
        public ProjectFile[] Verify()
        {
            foreach (var pair in hashes.ToArray())
            {
                token.ThrowIfCancellationRequested();
                Check(pair.Key);
                if (Hash(File.ReadAllBytes(pair.Key)) != pair.Value) throw new AppException("STALE_BASELINE", "读取期间工程已改变, 请重试", 3);
            }
            return [.. hashes.Select(p => new ProjectFile(Path.GetRelativePath(root, p.Key).Replace('\\', '/'), p.Value)).OrderBy(p => p.Path, StringComparer.Ordinal)];
        }
    }
}
