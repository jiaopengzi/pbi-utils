// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\LocalUdfStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 本地函数的稳定 ID、跨进程锁、基线复核及原子存储.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Udf.Packages;

namespace PbiUtils.Infrastructure.Udf;

/// <summary>所有路径均从宿主用户根与内部稳定 ID 构造.</summary>
public sealed class LocalUdfStore(string? userDataRoot = null) : ILocalUdfStore
{
    private readonly string _root = Path.Combine(userDataRoot ?? RuntimeEnvironment.UserDataDirectory(), "udf", "local");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    /// <inheritdoc />
    public Task<IReadOnlyList<LocalUdfEntry>> ListAsync(CancellationToken cancellationToken) => Run<IReadOnlyList<LocalUdfEntry>>(() =>
    {
        using var gate = Gate();
        return ReadEntries(cancellationToken);
    }, cancellationToken);
    /// <inheritdoc />
    public Task<LocalUdfEntry> GetAsync(string id, CancellationToken cancellationToken) => Run(() => { using var gate = Gate(); return Read(id); }, cancellationToken);
    /// <inheritdoc />
    public Task<LocalUdfEntry> CreateAsync(LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => Run(() =>
    {
        using var gate = Gate(); Validate(definition); EnsureUnique(definition, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); var id = Guid.NewGuid().ToString("N"); return Write(id, definition, committing);
    }, cancellationToken);
    /// <inheritdoc />
    public Task<LocalUdfEntry> UpdateAsync(string id, string baseline, LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => Run(() =>
    {
        using var gate = Gate(); Validate(definition); CheckBaseline(Read(id), baseline); EnsureUnique(definition, id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); CheckBaseline(Read(id), baseline); return Write(id, definition, committing);
    }, cancellationToken);
    /// <inheritdoc />
    public Task DeleteAsync(string id, string baseline, CancellationToken cancellationToken, Action? committing = null) => Run(() =>
    { using var gate = Gate(); CheckBaseline(Read(id), baseline); cancellationToken.ThrowIfCancellationRequested(); CheckBaseline(Read(id), baseline); committing?.Invoke(); File.Delete(PathFor(id)); return true; }, cancellationToken);

    /// <summary>在调用方持有独占锁时读取全部精确版本, 损坏、未知或同名同 tag 的重复存量均失败, 不任意选择一个 GUID.</summary>
    private LocalUdfEntry[] ReadEntries(CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(_root, "*.json").Take(2049).ToArray();
        if (files.Length > 2048) throw new AppException("UDF_STORE_LIMIT", "本地函数数量超出限制");
        var entries = new List<LocalUdfEntry>();
        var versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Read(Path.GetFileNameWithoutExtension(path));
            if (!versions.TryGetValue(entry.Definition.Name, out var tags)) versions[entry.Definition.Name] = tags = new(StringComparer.Ordinal);
            if (!tags.Add(entry.Definition.Version)) throw new AppException("UDF_STORE_INVALID", "本地函数存在同名同版本的重复记录, 请核对本地函数库");
            entries.Add(entry);
        }
        return [.. entries];
    }

    /// <summary>写入前在同一锁内核验名称和精确 tag 唯一, 更新排除自己的稳定 ID, 冲突不写入任何文件.</summary>
    private void EnsureUnique(LocalUdfDefinition definition, string? ownId, CancellationToken cancellationToken)
    {
        var entries = ReadEntries(cancellationToken);
        if (entries.Any(entry => entry.Id != ownId && entry.Definition.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase) && entry.Definition.Version == definition.Version))
            throw new AppException("UDF_LOCAL_VERSION_EXISTS", "本地函数已存在同名同版本, 请编辑已有版本或使用不同版本号");
        if (ownId is null && entries.Length >= 2048) throw new AppException("UDF_STORE_LIMIT", "本地函数数量超出限制");
    }

    private FileStream Gate() { UserDataStore.Check(_root); Directory.CreateDirectory(_root); UserDataStore.Check(_root); return UserDataStore.Lock(Path.Combine(_root, ".store.lock")); }
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !id.Equals(id, StringComparison.InvariantCultureIgnoreCase)) throw new AppException("UDF_LOCAL_ID_INVALID", "本地函数标识无效");
        var path = Path.Combine(_root, id + ".json"); UserDataStore.Check(path); return path;
    }
    private LocalUdfEntry Read(string id)
    {
        var path = PathFor(id);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 256 * 1024) throw new AppException("UDF_STORE_INVALID", "本地函数文件超出限制");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        using var document = UdfJson.Parse(bytes);
        var saved = document.RootElement.Deserialize<Saved>(Json) ?? throw new AppException("UDF_STORE_INVALID", "本地函数文件损坏");
        if (saved.SchemaVersion != 1 || saved.Id != id) throw new AppException("UDF_STORE_INVALID", "本地函数存储版本或身份无效");
        Validate(saved.Definition);
        return new(id, Hash(bytes), saved.Definition);
    }
    private LocalUdfEntry Write(string id, LocalUdfDefinition definition, Action? committing)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(1, id, definition), Json);
        if (bytes.Length > 256 * 1024) throw new AppException("UDF_STORE_LIMIT", "本地函数完整定义超出存储限制");
        var path = PathFor(id);
        committing?.Invoke();
        UserDataStore.Write(path, bytes);
        return new(id, Hash(bytes), definition);
    }
    private static void Validate(LocalUdfDefinition definition)
    {
        UdfValidation.LocalDefinition(definition);
        var parsed = DaxLibPackageReader.ParseFunctions("function '" + definition.Name.Replace("'", "''", StringComparison.Ordinal) + "' =\n" +
            string.Join('\n', definition.Expression.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => "\t\t" + line)));
        if (parsed.Count != 1 || parsed[0].Name != definition.Name) throw new AppException("UDF_DEFINITION_INVALID", "本地函数元数据格式无效");
    }
    private static void CheckBaseline(LocalUdfEntry entry, string baseline) { if (entry.Baseline != baseline) throw new AppException("UDF_BASELINE_CHANGED", "本地函数已改变, 请重新读取"); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken) => Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return action(); }, cancellationToken);
    private sealed record Saved(int SchemaVersion, string Id, LocalUdfDefinition Definition);
}
