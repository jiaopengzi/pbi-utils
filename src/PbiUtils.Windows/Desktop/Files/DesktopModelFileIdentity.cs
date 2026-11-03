// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopModelFileIdentity.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 校验本地模型文件并按 PBIP 真实报表绑定识别 Desktop 文件身份.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>只解析入口身份, 不读取 PBIX 内容或执行模型定义.</summary>
public sealed record DesktopModelFileIdentity(string EntryPath, string? ReportPath, bool IsRemoteReport = false)
{
    /// <summary>检查本机完整文件路径和唯一报表绑定, 无效或不安全输入抛出稳定应用错误.</summary>
    /// <param name="path">原生窗口选择的 PBIP 或 PBIX 完整路径.</param>
    /// <returns>入口及其确切报表文件身份.</returns>
    public static DesktopModelFileIdentity Read(string path)
    {
        try
        {
            var full = Normalize(path);
            var extension = Path.GetExtension(full);
            if (extension.Equals(".pbix", StringComparison.OrdinalIgnoreCase)) return new(full, null);
            if (!extension.Equals(".pbip", StringComparison.OrdinalIgnoreCase))
                throw new AppException("LOCAL_MODEL_FILE_UNSUPPORTED", "请选择 .pbip 或 .pbix 文件", 2);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 256 * 1024) throw InvalidProject();
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(json.RootElement);
            var root = json.RootElement;
            if (root.GetProperty("version").GetString() != "1.0") throw InvalidProject();
            var artifacts = root.GetProperty("artifacts");
            if (artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() != 1) throw InvalidProject();
            var relative = artifacts[0].GetProperty("report").GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw InvalidProject();
            var directory = Path.GetDirectoryName(full)!;
            var reportDirectory = Path.GetFullPath(Path.Combine(directory, relative));
            var directoryPrefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
            if (!reportDirectory.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)) throw InvalidProject();
            var report = Normalize(Path.Combine(reportDirectory, "definition.pbir"));
            return new(full, report, HasRemoteBinding(report));
        }
        catch (AppException) { throw; }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or NotSupportedException)
        { throw InvalidProject(); }
        catch (UnauthorizedAccessException) { throw new AppException("LOCAL_MODEL_FILE_ACCESS", "无法读取所选文件, 请检查文件权限", 3); }
        catch (IOException) { throw new AppException("LOCAL_MODEL_FILE_IO", "无法读取所选文件, 请确认文件存在且未被其他程序占用", 3); }
    }

    /// <summary>只读取已核验 PBIP/PBIR 的明确远端引用; PBIX 不解包, 未知绑定不推断为远端.</summary>
    /// <param name="path">本机真实文件路径, 不按文件名寻找同目录替代文件.</param>
    /// <returns>仅存在有效 byConnection 时为 true; 损坏或歧义输入抛出安全错误.</returns>
    public static bool HasRemoteBinding(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pbip", StringComparison.OrdinalIgnoreCase)) return Read(path).IsRemoteReport;
        if (!extension.Equals(".pbir", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new FileStream(Normalize(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 256 * 1024) throw InvalidProject();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            if (!document.RootElement.TryGetProperty("datasetReference", out var binding)
                || !binding.TryGetProperty("byConnection", out var connection)) return false;
            if (binding.TryGetProperty("byPath", out _) || connection.ValueKind != JsonValueKind.Object
                || !connection.TryGetProperty("connectionString", out var value) || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidProject();
            return true;
        }
        catch (AppException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw InvalidProject(); }
    }

    /// <summary>仅接受真实完整路径, 拒绝网络、设备、替代数据流与链接.</summary>
    /// <param name="path">待验证路径.</param>
    /// <returns>规范化后的本机文件路径.</returns>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/') || path.AsSpan(2).Contains(':'))
            throw new AppException("LOCAL_MODEL_FILE_UNSAFE", "请选择本机的普通 PBIP/PBIX 文件, 不支持网络路径或链接", 3);
        var full = Path.GetFullPath(path);
        try { UserDataStore.Check(full); }
        catch (AppException error) when (error.Code == "UNSAFE_USER_DATA_PATH")
        { throw new AppException("LOCAL_MODEL_FILE_UNSAFE", "请选择本机的普通 PBIP/PBIX 文件, 不支持网络路径或链接", 3); }
        if (!File.Exists(full)) throw new AppException("LOCAL_MODEL_FILE_MISSING", "所选文件不存在, 请重新选择", 3);
        return full;
    }

    /// <summary>完整入口或确切绑定的 PBIR 才能匹配, 不比较标题或文件名.</summary>
    /// <param name="currentPath">Desktop Bridge 返回的当前文件路径.</param>
    /// <returns>安全路径是否指向同一入口.</returns>
    public bool Matches(string currentPath)
    {
        var full = Normalize(currentPath);
        return full.Equals(EntryPath, StringComparison.OrdinalIgnoreCase) || full.Equals(ReportPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>递归拒绝重复 JSON 属性, 防止入口绑定被解析器差异覆盖.</summary>
    /// <param name="element">待检查的入口 JSON 节点.</param>
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidProject();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    /// <summary>生成不包含入口内容和路径的稳定错误.</summary>
    /// <returns>PBIP 身份解析失败.</returns>
    private static AppException InvalidProject() => new("LOCAL_MODEL_FILE_INVALID", "PBIP 入口必须具有唯一且安全的本地报表绑定, 请在 Desktop 检查工程", 3);
}
