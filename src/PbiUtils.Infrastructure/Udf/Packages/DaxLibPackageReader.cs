// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Packages\DaxLibPackageReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 有界只读解包及 TOM 元数据解析, 不执行 DAX 或包内容.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Udf.Packages;

/// <summary>包读取只产生内存定义, 不向真实模型安装.</summary>
public static class DaxLibPackageReader
{
    /// <summary>下载与单文件上限.</summary>
    public const int MaxFileBytes = 32 * 1024 * 1024;
    /// <summary>拒绝重复、链接、非预期内容及压缩炸弹, 再解析精确身份.</summary>
    public static UdfPackage Read(byte[] bytes, string packageId, string version)
    {
        UdfValidation.PackageIdentity(packageId, version);
        if (bytes.Length > MaxFileBytes) throw Invalid();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            if (zip.Entries.Count > 2048) throw Invalid();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                var parts = name.Split('/');
                if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':') || parts.Any(p => p is ".." or "." or "") ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0 ||
                    entry.Length > MaxFileBytes || (total += entry.Length) > 128L * 1024 * 1024 || !Allowed(name) || files.ContainsKey(name)) throw Invalid();
                using var stream = entry.Open();
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer)) != 0)
                { if (output.Length + read > MaxFileBytes || output.Length + read > entry.Length) throw Invalid(); output.Write(buffer, 0, read); }
                files.Add(name, output.ToArray());
            }
        }
        return ParseFiles(files, bytes, packageId, version);
    }

    /// <summary>读取已由 Git blob 提供的部件, 复用 ZIP 的路径、大小、媒体及身份验证; 不读取磁盘路径.</summary>
    internal static UdfPackage ReadFiles(IReadOnlyDictionary<string, byte[]> source, string packageId, string version)
    {
        UdfValidation.PackageIdentity(packageId, version);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using var identity = new MemoryStream();
        using var writer = new BinaryWriter(identity, Encoding.UTF8, true);
        if (source.Count > 2048) throw Invalid();
        foreach (var (name, value) in source.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(part => part is "." or ".." or "") ||
                !Allowed(name) || value.Length > MaxFileBytes || (total += value.Length) > 128L * 1024 * 1024 || !files.TryAdd(name, value)) throw Invalid();
            writer.Write(name);
            writer.Write(value.Length);
            writer.Write(value);
        }
        writer.Flush();
        return ParseFiles(files, identity.ToArray(), packageId, version);
    }

    /// <summary>所有下载格式共享同一 manifest、函数注解、许可及展示元数据约束, Hash 仍绑定完整输入字节.</summary>
    private static UdfPackage ParseFiles(Dictionary<string, byte[]> files, byte[] contentIdentity, string packageId, string version)
    {
        if (!files.TryGetValue("manifest.daxlib", out var manifest) || !files.TryGetValue("lib/functions.tmdl", out var tmdl)) throw Invalid();
        if (files.TryGetValue("[Content_Types].xml", out var contentTypes)) CheckContentTypes(contentTypes, files);
        using var document = UdfJson.Parse(manifest);
        var root = document.RootElement;
        if (root.TryGetProperty("$schema", out var schema) && (schema.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(schema.GetString(), UriKind.Absolute, out var schemaUri) || schemaUri.Scheme != "https" || schemaUri.Host != "raw.githubusercontent.com" ||
            !(schemaUri.AbsolutePath.StartsWith("/daxlib/daxlib/", StringComparison.Ordinal) || schemaUri.AbsolutePath.StartsWith("/sql-bi/daxlib/", StringComparison.Ordinal)) ||
            !(schemaUri.AbsolutePath.EndsWith("/schemas/manifest/1.0.0/manifest.1.0.0.schema.json", StringComparison.Ordinal) ||
              schemaUri.AbsolutePath == "/sql-bi/daxlib/main/schemas/manifest/1.0.0/manifest.schema.json"))) throw Invalid();
        if (root.TryGetProperty("schemaVersion", out _)) throw Invalid();
        var id = root.GetProperty("id").GetString()!;
        var actualVersion = root.GetProperty("version").GetString()!;
        if (!string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase) || actualVersion != version) throw Invalid();
        var dependencies = root.TryGetProperty("dependencies", out var dependencyJson)
            ? dependencyJson.EnumerateArray().Select(d => new UdfPackageDependency(d.GetProperty("id").GetString()!, d.GetProperty("version").GetString()!)).ToArray() : [];
        foreach (var dependency in dependencies) UdfValidation.PackageIdentity(dependency.Id, dependency.Version);
        if (dependencies.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != dependencies.Length) throw Invalid();
        var functions = ParseFunctions(Decode(tmdl));
        if (functions.Count == 0 || functions.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != functions.Count) throw Invalid();
        foreach (var function in functions)
            if (!function.Annotations.TryGetValue("DAXLIB_PackageId", out var annotationId) || !string.Equals(annotationId, id, StringComparison.OrdinalIgnoreCase) ||
                !function.Annotations.TryGetValue("DAXLIB_PackageVersion", out var annotationVersion) || annotationVersion != version) throw Invalid();
        var licenses = files.Where(f => Path.GetFileName(f.Key).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f.Key).StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Key, f => Decode(f.Value), StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("license", out var license)) licenses["manifest.license"] = license.GetRawText();
        if (root.TryGetProperty("licenseUrl", out var licenseUrl)) licenses["manifest.licenseUrl"] = licenseUrl.GetString() ?? "";
        return new(id, version, Convert.ToHexStringLower(SHA256.HashData(contentIdentity)),
            root.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            Readme(root, files), licenses, dependencies, functions, DateTimeOffset.UtcNow,
            Metadata: UdfPackageMetadataReader.Read(root, files));
    }

    /// <summary>读取 manifest 声明的包内 Markdown 文档, 未声明时采用规范默认 readme.md; 不接受外部地址、目录逃逸或缺失声明部件.</summary>
    private static string Readme(JsonElement root, Dictionary<string, byte[]> files)
    {
        var path = "readme.md";
        var declared = root.TryGetProperty("readme", out var readme);
        if (declared)
        {
            if (readme.ValueKind != JsonValueKind.String) throw Invalid();
            path = readme.GetString()!;
            if (path.Length is 0 or > 64 || !path.StartsWith('/') || path.Any(char.IsControl) || path.IndexOfAny(['\\', ':', '%', '?', '#']) >= 0) throw Invalid();
            path = path[1..];
            if (path.Split('/').Any(part => part is "" or "." or "..") || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        }
        if (files.TryGetValue(path, out var bytes)) return Decode(bytes);
        return declared ? throw Invalid() : "";
    }

    /// <summary>使用现有 TOM 验证函数元数据结构; 这不证明 DAX 语义或依赖完整.</summary>
    internal static IReadOnlyList<UdfFunction> ParseFunctions(string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pbi-udf-parse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "model.tmdl"), "model Model\n" + string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => "\t" + line)), new UTF8Encoding(false));
            var model = TOM.TmdlSerializer.DeserializeModelFromFolder(directory);
            if (model.Tables.Count != 0 || model.Expressions.Count != 0 || model.Relationships.Count != 0) throw Invalid();
            return model.Functions.Select(f => new UdfFunction(f.Name, f.Expression, f.Description ?? "", f.Annotations.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal))).ToArray();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool Allowed(string name)
    {
        var leaf = Path.GetFileName(name);
        return name.Equals("manifest.daxlib", StringComparison.OrdinalIgnoreCase) || name.Equals("lib/functions.tmdl", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            (leaf.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)) && Path.GetExtension(leaf).ToLowerInvariant() is "" or ".md" or ".txt" or ".text" or ".rst" ||
            Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".svg";
    }
    /// <summary>校验 OPC 类型声明; 官方 PNG 通用二进制声明仅在实际图片签名正确时兼容.</summary>
    private static void CheckContentTypes(byte[] bytes, IReadOnlyDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFileBytes });
        var document = XDocument.Load(reader);
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        if (document.Root?.Name != ns + "Types") throw Invalid();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Root.Elements())
        {
            if (item.Name != ns + "Default" && item.Name != ns + "Override") throw Invalid();
            var type = item.Attribute("ContentType")?.Value;
            var key = item.Attribute(item.Name.LocalName == "Default" ? "Extension" : "PartName")?.Value;
            if (key is null || !keys.Add(key)) throw Invalid();
            if (type is not ("application/json" or "text/plain" or "text/markdown" or "application/xml" or "text/xml" or "image/png" or "image/jpeg" or "image/svg+xml") &&
                (type != "application/octet-stream" || !IsPngContentType(item.Name.LocalName, key, files))) throw Invalid();
        }
    }
    /// <summary>默认声明可没有对应部件; 精确覆盖必须命中已经安全校验过的 PNG 路径.</summary>
    private static bool IsPngContentType(string kind, string key, IReadOnlyDictionary<string, byte[]> files)
    {
        if (kind == "Default")
            return key.Equals("png", StringComparison.OrdinalIgnoreCase) && files.Where(file => Path.GetExtension(file.Key).Equals(".png", StringComparison.OrdinalIgnoreCase))
                .All(file => HasPngSignature(file.Value));
        return key.StartsWith('/') && Path.GetExtension(key).Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            files.TryGetValue(key[1..], out var part) && HasPngSignature(part);
    }
    /// <summary>图标只保留字节而不解码或执行, 通用 MIME 不允许以扩展名伪装其他二进制内容.</summary>
    private static bool HasPngSignature(byte[] bytes) => bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    private static string Decode(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    private static AppException Invalid() => new("UDF_PACKAGE_INVALID", "函数包格式、身份或安全限制不符合要求");
}

/// <summary>拒绝重复 JSON 字段, 不容许最后一个值静默覆盖.</summary>
internal static class UdfJson
{
    internal static JsonDocument Parse(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try { Check(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void Check(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new AppException("UDF_JSON_INVALID", "函数数据包含重复字段"); Check(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Check(value);
    }
}
