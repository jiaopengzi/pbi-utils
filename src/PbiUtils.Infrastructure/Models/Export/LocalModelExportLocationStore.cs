// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Export\LocalModelExportLocationStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原子记录最近成功导出目录, 使用随机身份和时间戳保护跨会话打开及并发更新.

using PbiUtils.Infrastructure.Storage;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Models.Export;

/// <summary>宿主独占的单条目录记录; 保存失败仅建立会话回退, 不把已经发布的导出改成失败.</summary>
/// <param name="root">宿主绑定的当前用户数据目录, 不接受网页路径.</param>
public sealed class LocalModelExportLocationStore(string root)
{
    private const int MaximumBytes = 16384;
    private const string ReadWarning = "导出目录记录无法读取, 原文件已保留";
    private const string SaveWarning = "文件已导出, 但无法保存导出目录记录";
    private const string UnavailableMessage = "导出目录已不存在或不可用, 请重新导出后再打开";
    private readonly string path = Path.Combine(root, "model-export-location.json");
    private readonly Lock sync = new();
    private Entry? fallback;

    /// <summary>仅保存宿主成功回执的目录、随机身份与完成时间, 不记录模型名或业务值.</summary>
    private sealed record Entry(string Id, string Directory, DateTimeOffset CompletedAtUtc);

    /// <summary>每次先读取磁盘最新记录, 较晚的未持久会话结果才优先, 不以旧缓存遮蔽其它实例.</summary>
    /// <returns>真实目录可用性和必要警告; 无记录返回空身份, IO/格式故障保留原文件.</returns>
    public ModelExportLocation Read()
    {
        lock (sync) return Snapshot(Current(out var warning), warning);
    }

    /// <summary>只在文件成功发布后记录实际输出目录, 不复用业务取消令牌或向外抛出存储故障.</summary>
    /// <param name="directory">单文件的父目录或多CSV新目录本身, 仅来自宿主系统选择结果.</param>
    /// <param name="completedAtUtc">本次实际成功完成时间; 旧结果晚到不得覆盖更新的持久记录.</param>
    /// <returns>持久成功的目录, 或附警告的会话回退; 该警告不能改变文件导出成功事实.</returns>
    public ModelExportLocation Remember(string directory, DateTimeOffset completedAtUtc)
    {
        lock (sync)
        {
            Entry? candidate = null;
            try
            {
                if (completedAtUtc < DateTimeOffset.UnixEpoch) throw InvalidRecord();
                candidate = new(Guid.NewGuid().ToString("N"), Normalize(directory), completedAtUtc.ToUniversalTime());
                UserDataStore.Check(candidate.Directory);
                UserDataStore.Check(root);
                Directory.CreateDirectory(root);
                UserDataStore.Check(root);
                using var lease = UserDataStore.Lock(Path.Combine(root, "model-export-location.lock"));
                // 锁内读取, 不用本实例过期快照覆盖其它实例已记录的更晚成功结果.
                var stored = ReadDocument();
                var latest = fallback is not null && (stored is null || fallback.CompletedAtUtc > stored.CompletedAtUtc) ? fallback : stored;
                if (latest is not null && latest.CompletedAtUtc > candidate.CompletedAtUtc)
                    return Snapshot(Current(out var warning), warning);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    id = candidate.Id,
                    directory = candidate.Directory,
                    completedAtUtc = candidate.CompletedAtUtc,
                });
                UserDataStore.Write(path, bytes);
                fallback = null;
                return Snapshot(candidate);
            }
            catch (Exception error) when (ExpectedFailure(error))
            {
                if (candidate is not null && (fallback is null || candidate.CompletedAtUtc >= fallback.CompletedAtUtc)) fallback = candidate;
                return Snapshot(Current(out _), SaveWarning);
            }
        }
    }

    /// <summary>按当前最新记录精确解析身份, 另一实例更新后拒绝静默打开不同目录.</summary>
    /// <param name="id">网页上次收到的随机记录身份, 不能传入目录文本.</param>
    /// <returns>重新核验过的宿主目录; 调用方实际打开前仍须做即时路径检查.</returns>
    /// <exception cref="AppException">身份过期返回 EXPORT_LOCATION_CHANGED, 不存在/链接返回 EXPORT_LOCATION_UNAVAILABLE.</exception>
    public string Resolve(string id)
    {
        var location = Read();
        if (!Guid.TryParseExact(id, "N", out _) || location.Id != id)
            throw new AppException("EXPORT_LOCATION_CHANGED", "导出目录记录已变化, 请刷新后再打开", 3);
        if (!location.Available || location.DisplayPath is null) throw new AppException("EXPORT_LOCATION_UNAVAILABLE", UnavailableMessage, 3);
        return location.DisplayPath;
    }

    /// <summary>失败回退仅优先于更早记录, 磁盘同新或更新时撤销会话回退及其保存警告.</summary>
    /// <param name="warning">磁盘无法读取或会话尚未持久的稳定提示.</param>
    /// <returns>当前最新成功记录, 无可识别记录则为 null.</returns>
    private Entry? Current(out string? warning)
    {
        Entry? stored = null;
        warning = null;
        try { stored = ReadDocument(); }
        catch (Exception error) when (ExpectedFailure(error)) { warning = ReadWarning; }
        if (fallback is not null && (stored is null || fallback.CompletedAtUtc > stored.CompletedAtUtc))
        {
            warning = SaveWarning;
            return fallback;
        }
        fallback = null;
        return stored;
    }

    /// <summary>严格有界解析当前格式, 不探测记录目录的子树, 损坏/未知格式不自动覆盖.</summary>
    /// <returns>当前单条记录, 文件不存在则返回 null.</returns>
    /// <exception cref="AppException">记录或用户数据路径无法安全识别, 正文和路径不进入异常.</exception>
    private Entry? ReadDocument()
    {
        UserDataStore.Check(path);
        FileStream stream;
        try { stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            if (stream.Length > MaximumBytes) throw InvalidRecord();
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes.AsSpan(length));
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumBytes) throw InvalidRecord();
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw InvalidRecord();
            var names = value.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 4 || names.Distinct(StringComparer.Ordinal).Count() != 4
                || names.Except(["schemaVersion", "id", "directory", "completedAtUtc"], StringComparer.Ordinal).Any()
                || !value.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1
                || value.GetProperty("id").ValueKind != JsonValueKind.String
                || value.GetProperty("directory").ValueKind != JsonValueKind.String
                || value.GetProperty("completedAtUtc").ValueKind != JsonValueKind.String) throw InvalidRecord();
            var id = value.GetProperty("id").GetString()!;
            var directory = value.GetProperty("directory").GetString()!;
            if (!Guid.TryParseExact(id, "N", out var parsed) || id != parsed.ToString("N")
                || !value.GetProperty("completedAtUtc").TryGetDateTimeOffset(out var completed) || completed.Offset != TimeSpan.Zero
                || completed < DateTimeOffset.UnixEpoch || Normalize(directory) != directory) throw InvalidRecord();
            return new(id, directory, completed);
        }
    }

    /// <summary>仅规范化有界完整目录文本, 不接受设备路径、相对路径、控制字符或无效路径段.</summary>
    /// <param name="directory">宿主成功结果或严格记录内的目录文本.</param>
    /// <returns>规范化完整目录; 无效文本抛出稳定记录错误.</returns>
    private static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 2048 || !Path.IsPathFullyQualified(directory)
            || directory.Any(char.IsControl) || directory.Replace('/', '\\').StartsWith(@"\\?\", StringComparison.Ordinal)
            || directory.Replace('/', '\\').StartsWith(@"\\.\", StringComparison.Ordinal)) throw InvalidRecord();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var segments = full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw InvalidRecord();
        return full;
    }

    /// <summary>目标及祖先链接检查在调用者后台范围执行, 不枚举或建立目录.</summary>
    /// <param name="entry">宿主最新成功绑定, 为 null 时返回无记录状态.</param>
    /// <param name="warning">已有存储警告优先保留, 不用目录探测失败覆盖原因.</param>
    private static ModelExportLocation Snapshot(Entry? entry, string? warning = null)
    {
        if (entry is null) return new(null, null, false, warning);
        var available = false;
        try { UserDataStore.Check(entry.Directory); available = Directory.Exists(entry.Directory); }
        catch (Exception error) when (ExpectedFailure(error)) { }
        return new(entry.Id, entry.Directory, available, warning ?? (available ? null : UnavailableMessage));
    }

    /// <summary>仅将预期路径/格式/磁盘故障转换为稳定状态, 不吞编程错误或记录原始异常.</summary>
    private static bool ExpectedFailure(Exception error) => error is AppException or JsonException or IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or InvalidOperationException;

    /// <summary>记录格式错误不包含文件路径或JSON正文, 原文件必须保持.</summary>
    private static AppException InvalidRecord() => new("INVALID_EXPORT_LOCATION", ReadWarning, 3);
}
