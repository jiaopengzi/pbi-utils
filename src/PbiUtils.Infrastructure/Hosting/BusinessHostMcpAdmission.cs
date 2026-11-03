// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostMcpAdmission.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在安装隔离 runtime 树严格持久化 MCP 准入, 不修改用户配置或模型.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>唯一 Host owner 写入, 其他进程只读原子快照; 缺失默认接入, 损坏与 I/O 失败关闭通路.</summary>
internal static class BusinessHostMcpAdmission
{
    /// <summary>读取严格的当前格式, 仅文件确实不存在时默认允许; 不修复或覆盖损坏内容.</summary>
    internal static bool Read(BusinessHostSettings settings)
    {
        var path = Path.Combine(settings.CoordinationDirectory, "mcp-admission.json");
        try
        {
            UserDataStore.Check(path);
            RequireDirectoryAncestors(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 256) throw Invalid();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 2 || properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 2
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) || value != 1
                || !root.TryGetProperty("acceptingConnections", out var accepting) || accepting.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            return accepting.GetBoolean();
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw Invalid(); }
    }

    /// <summary>先核验原格式再原子替换; 调用方持有 Host 注册门闩, 失败保留既有准入事实.</summary>
    internal static void Write(BusinessHostSettings settings, bool acceptingConnections)
    {
        _ = Read(settings);
        try
        {
            UserDataStore.Check(settings.CoordinationDirectory);
            Directory.CreateDirectory(settings.CoordinationDirectory);
            UserDataStore.Write(Path.Combine(settings.CoordinationDirectory, "mcp-admission.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { version = 1, acceptingConnections }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Invalid(); }
    }

    /// <summary>启动和注册都执行同一判断, 暂停不会触发后台启动或业务重放.</summary>
    internal static void RequireAllowed(BusinessHostSettings settings)
    { if (!Read(settings)) throw Paused(); }

    internal static AppException Paused() => new("MCP_CONNECTIONS_PAUSED", "本安装的 MCP 接入已暂停, 请在桌面设置中恢复", 3);

    /// <summary>不存在的文件才可默认允许; 祖先普通文件或不可访问路径不能被 DirectoryNotFound 掩盖.</summary>
    private static void RequireDirectoryAncestors(string path)
    {
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.Directory) == 0) throw Invalid(); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static AppException Invalid() => new("MCP_ADMISSION_INVALID", "无法读取或保存 MCP 接入状态, 请检查本机运行状态文件", 3);
}
