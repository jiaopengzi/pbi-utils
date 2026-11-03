// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Connections\LocalRecentProjects.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 保存已成功连接的最近 PBIP/PBIX 文件, 提供受限身份重开而非任意路径输入.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Connections;

/// <summary>最近 PBIP/PBIX 文件的本机显示记录.</summary>
public sealed record RecentProject(string Id, string Path, string Name, DateTimeOffset OpenedAt);

/// <summary>最多20条, 路径去重, 每次保存读取最新文件以合并多个实例的历史.</summary>
public sealed class LocalRecentProjects(string directory, ILogger<LocalRecentProjects>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<LocalRecentProjects>.Instance;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string FilePath => System.IO.Path.Combine(directory, "recent-projects.json");

    /// <summary>损坏记录明确报错并保留原文件, 不静默清空历史.</summary>
    public IReadOnlyList<RecentProject> Load() => BusinessLog.Run<IReadOnlyList<RecentProject>>(log, "LocalRecentProjects.Load", () =>
    {
        UserDataStore.Check(FilePath);
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 131072) throw new AppException("INVALID_RECENT_PROJECTS", "最近工程文件过大, 已保留", 3);
        try
        {
            var bytes = File.ReadAllBytes(FilePath);
            using var doc = JsonDocument.Parse(bytes); JsonInputValidation.RejectDuplicates(doc.RootElement);
            var items = JsonSerializer.Deserialize<RecentProject[]>(bytes, Json) ?? throw new JsonException();
            if (items.Length > 20 || items.Any(p => p is null || p.Path is null || !System.IO.Path.IsPathFullyQualified(p.Path) || p.Path.Length > 2048 || !IsSupportedFile(p.Path) || p.Id != Identity(p.Path) || p.Name != System.IO.Path.GetFileName(p.Path))
                || items.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != items.Length) throw new JsonException();
            return items;
        }
        catch (JsonException) { throw new AppException("INVALID_RECENT_PROJECTS", "最近工程记录损坏, 已保留原文件", 3); }
    });

    /// <summary>只在本地文件连接成功后记录 PBIP/PBIX, 不写入模型内容或会话信息.</summary>
    /// <param name="path">宿主绑定的已成功连接文件路径.</param>
    /// <exception cref="AppException">文件类型不受支持、历史损坏或另一实例正在保存.</exception>
    public void Record(string path) => BusinessLog.Run(log, "LocalRecentProjects.Record", () =>
    {
        path = System.IO.Path.GetFullPath(path);
        if (path.Length > 2048 || !IsSupportedFile(path)) throw new AppException("INVALID_PROJECT_PATH", "请选择 PBIP 或 PBIX 文件");
        UserDataStore.Check(FilePath); Directory.CreateDirectory(directory);
        using var lease = UserDataStore.Lock(FilePath + ".lock");
        var entry = new RecentProject(Identity(path), path, System.IO.Path.GetFileName(path), DateTimeOffset.UtcNow);
        var items = new[] { entry }.Concat(Load().Where(p => p.Id != entry.Id)).Take(20).ToArray();
        UserDataStore.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(items, Json));
    });

    /// <summary>网页只能提交已记录身份, 不能提供任意磁盘路径.</summary>
    public string Resolve(string id) => Load().FirstOrDefault(p => p.Id == id)?.Path ?? throw new AppException("RECENT_PROJECT_NOT_FOUND", "最近工程记录不存在", 3);

    /// <summary>共享锁内按绑定身份移除一条历史, 不访问或删除记录指向的模型文件.</summary>
    /// <param name="id">宿主已发布的最近记录身份.</param>
    /// <param name="token">允许取消原子保存之前的准备工作.</param>
    /// <param name="committing">保存前由宿主原子裁决取消, 开始后必须返回实际提交结果.</param>
    /// <returns>是否实际删除一条记录; 不存在时返回 false.</returns>
    /// <exception cref="AppException">身份无效、历史损坏或存储被其他实例锁定.</exception>
    public bool Delete(string id, CancellationToken token, Action? committing = null) => BusinessLog.Run(log, "LocalRecentProjects.Delete", () =>
    {
        RecentRecordDeletionRequest.ValidateId(id);
        token.ThrowIfCancellationRequested(); UserDataStore.Check(FilePath); Directory.CreateDirectory(directory);
        using var lease = UserDataStore.Lock(FilePath + ".lock");
        var current = Load(); var items = current.Where(item => item.Id != id).ToArray();
        token.ThrowIfCancellationRequested();
        if (items.Length == current.Count) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(items, Json);
        token.ThrowIfCancellationRequested(); committing?.Invoke();
        UserDataStore.Write(FilePath, bytes);
        return true;
    });

    /// <summary>最近文件仅允许本地连接支持的两种扩展名, 不接受其他类型的路径记录.</summary>
    /// <param name="path">待记录或读取的完整文件路径.</param>
    /// <returns>扩展名是否为 PBIP 或 PBIX, 忽略大小写.</returns>
    private static bool IsSupportedFile(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return string.Equals(extension, ".pbip", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".pbix", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Windows 路径忽略大小写, 同一路径大小写变化不新增历史记录.</summary>
    private static string Identity(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
}
