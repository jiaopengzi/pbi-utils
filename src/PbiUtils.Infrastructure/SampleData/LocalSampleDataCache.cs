// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\LocalSampleDataCache.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在用户目录严格有界地缓存示例结果, 通过固定产物元数据复核并原子保存.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Application;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using PbiUtils.Infrastructure.SampleData;

namespace PbiUtils.Infrastructure.SampleData;

/// <summary>只保存一份宿主结果与轻量文件标记; 不缓存 CSV 内容, 不改变生成和清空授权.</summary>
public sealed class LocalSampleDataCache(string root) : ISampleDataCache
{
    private const int MaximumBytes = 16384;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 6,
    };
    private readonly string path = Path.Combine(root, "sample-data-result.json");

    /// <summary>缓存版本仅由本机宿主维护, 固定标记与统计分开序列化.</summary>
    private sealed record CacheDocument(int SchemaVersion, SampleDataState State, SampleDataFileStamp[] Stamps);

    /// <summary>只从有界用户缓存恢复目录文本, 不探测此前选择的磁盘或网络目录.</summary>
    /// <returns>上次权威结果目录, 缺失时返回 null.</returns>
    /// <exception cref="AppException">缓存无法识别或用户缓存路径不安全.</exception>
    public string? RestoreOutputPath() => ReadDocument()?.State.OutputPath;

    /// <summary>匹配当前目录后才复核固定文件标记, 不探测其他缓存目标或解析 CSV 全文.</summary>
    /// <param name="expectedOutputPath">经过适配器规范化和验证的当前宿主目录.</param>
    /// <returns>缓存及其当前有效性; 无缓存时返回 null.</returns>
    /// <exception cref="AppException">缓存损坏、版本未知或路径不安全, 原文件保持.</exception>
    public SampleDataCacheEntry? Read(string expectedOutputPath)
    {
        var document = ReadDocument();
        if (document is null || !string.Equals(document.State.OutputPath, expectedOutputPath, StringComparison.OrdinalIgnoreCase)) return null;
        var output = SampleDataAdapter.ValidateOutput(document.State.OutputPath);
        var current = document.Stamps.SequenceEqual(CaptureStamps(output));
        return new(document.State with { FileStamps = document.Stamps }, current);
    }

    /// <summary>仅保存稳定观察且仍与实际产物一致的结果, 使用跨进程锁及同目录原子替换.</summary>
    /// <param name="state">实际统计前后文件标记一致的权威状态.</param>
    /// <exception cref="AppException">旧缓存损坏、版本未知或用户存储被占用, 原文件保持.</exception>
    public void Save(SampleDataState state)
    {
        if (state.FileStamps is null) return;
        var output = SampleDataAdapter.ValidateOutput(state.OutputPath);
        var document = new CacheDocument(1, state, [.. state.FileStamps]);
        Validate(document);
        if (!document.Stamps.SequenceEqual(CaptureStamps(output))) return;
        UserDataStore.Check(root);
        Directory.CreateDirectory(root);
        using var lease = UserDataStore.Lock(Path.Combine(root, "sample-data-result.lock"));
        // 损坏和未知版本只能保留, 不因新一次生成而静默覆盖用户文件.
        ReadDocument();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes) throw InvalidCache();
        if (!document.Stamps.SequenceEqual(CaptureStamps(output))) return;
        UserDataStore.Write(path, bytes);
    }

    /// <summary>只对十一张固定 CSV 和 PBIP 是否存在取标记, 不枚举任意目录或读取 CSV 内容.</summary>
    /// <param name="directory">已经过适配器安全验证的实际目录, 可以是待发布隔离目录.</param>
    /// <returns>固定顺序的十二项元数据.</returns>
    internal static SampleDataFileStamp[] CaptureStamps(string directory)
    {
        var stamps = new List<SampleDataFileStamp>(SampleTables.AllFiles.Count + 1);
        foreach (var name in SampleTables.AllFiles)
        {
            var file = new FileInfo(Path.Combine(directory, "data", name));
            var exists = file.Exists;
            stamps.Add(new(name, exists, exists ? file.Length : 0, exists ? file.LastWriteTimeUtc.Ticks : 0));
        }
        stamps.Add(new("pbip", Directory.Exists(Path.Combine(directory, "pbip")), 0, 0));
        return [.. stamps];
    }

    /// <summary>以固定内存上限解析严格字段与版本, 不读取缓存指向目录的内容.</summary>
    /// <returns>合法文档, 文件不存在时返回 null.</returns>
    /// <exception cref="AppException">格式、字段、版本或统计值无法识别.</exception>
    private CacheDocument? ReadDocument()
    {
        UserDataStore.Check(path);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumBytes) throw new JsonException();
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes.AsSpan(length));
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumBytes) throw new JsonException();
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 6 });
            JsonInputValidation.RejectDuplicates(json.RootElement);
            RequireFields(json.RootElement, "schemaVersion", "state", "stamps");
            var state = json.RootElement.GetProperty("state");
            RequireFields(state, "outputPath", "hasData", "lastFactDate", "tables");
            foreach (var table in state.GetProperty("tables").EnumerateArray()) RequireFields(table, "name", "rows", "size");
            foreach (var stamp in json.RootElement.GetProperty("stamps").EnumerateArray()) RequireFields(stamp, "name", "exists", "size", "lastWriteUtcTicks");
            var document = JsonSerializer.Deserialize<CacheDocument>(bytes.AsSpan(0, length), Json) ?? throw new JsonException();
            Validate(document);
            return document;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw InvalidCache();
        }
    }

    /// <summary>拒绝未知、缺失和重复属性, JSON 值类型由严格反序列化继续检查.</summary>
    /// <param name="value">待核验的对象.</param>
    /// <param name="names">完整字段白名单.</param>
    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var properties = value.EnumerateObject().Select(field => field.Name).ToArray();
        if (properties.Length != names.Length || properties.Except(names, StringComparer.Ordinal).Any()) throw new JsonException();
    }

    /// <summary>校验唯一固定表、非负统计、日期与文件标记之间的一致性, 不信任缓存路径授权.</summary>
    /// <param name="document">反序列化或准备保存的文档.</param>
    /// <exception cref="AppException">版本或任一字段不符合本机缓存格式.</exception>
    private static void Validate(CacheDocument document)
    {
        var state = document.State;
        if (document.SchemaVersion != 1 || state is null || state.OutputPath is null || state.OutputPath.Length is 0 or > 240
            || !Path.IsPathFullyQualified(state.OutputPath) || state.OutputPath.Any(char.IsControl)
            || state.OutputPath.StartsWith(@"\\?\", StringComparison.Ordinal) || state.OutputPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || state.OutputPath != Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.OutputPath))
            || string.Equals(state.OutputPath, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(state.OutputPath)!), StringComparison.OrdinalIgnoreCase)
            || state.Tables is null || state.Tables.Count > SampleTables.AllFiles.Count || document.Stamps is null
            || !document.Stamps.Select(stamp => stamp?.Name).SequenceEqual(SampleTables.AllFiles.Append("pbip"))) throw InvalidCache();
        if (state.LastFactDate is not null && !DateOnly.TryParseExact(state.LastFactDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw InvalidCache();
        if (state.HasData != document.Stamps.Any(stamp => stamp.Exists)
            || !state.HasData && (state.Tables.Count != 0 || state.LastFactDate is not null)
            || state.Tables.Any(table => table is null || table.Rows < 0 || table.Size < 0)
            || state.Tables.Select(table => table.Name).Distinct(StringComparer.Ordinal).Count() != state.Tables.Count) throw InvalidCache();
        foreach (var stamp in document.Stamps)
        {
            if (stamp.Size < 0 || stamp.LastWriteUtcTicks < 0 || stamp.LastWriteUtcTicks > DateTime.MaxValue.Ticks
                || !stamp.Exists && (stamp.Size != 0 || stamp.LastWriteUtcTicks != 0)) throw InvalidCache();
            if (stamp.Name == "pbip")
            {
                if (stamp.Size != 0 || stamp.LastWriteUtcTicks != 0) throw InvalidCache();
                continue;
            }
            var table = state.Tables.SingleOrDefault(table => table.Name == Path.GetFileNameWithoutExtension(stamp.Name));
            if (stamp.Exists != (table is not null) || table is not null && table.Size != stamp.Size || stamp.Exists && stamp.LastWriteUtcTicks == 0) throw InvalidCache();
        }
        if (state.Tables.Any(table => !SampleTables.AllFiles.Any(name => Path.GetFileNameWithoutExtension(name) == table.Name))
            || state.LastFactDate is not null && !document.Stamps.Any(stamp => stamp.Name == SampleTables.FileOrder && stamp.Exists)) throw InvalidCache();
    }

    /// <summary>缓存错误只返回稳定安全文案, 不包含目录或解析正文.</summary>
    private static AppException InvalidCache() => new("INVALID_SAMPLE_CACHE", "示例结果缓存无法识别, 已保留原文件.", 3);
}
