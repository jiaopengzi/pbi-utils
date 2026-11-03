// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostProtocol.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享后台的有界长度前缀 JSON 帧及严格字段规则, 不放宽各业务输入预算.

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

/// <summary>20 MiB 仅容纳原 16 MiB 业务结果及信封, 请求仍由原业务入口执行更小预算.</summary>
internal static class BusinessHostProtocol
{
    internal const int MaximumBytes = 20 * 1024 * 1024;
    internal const int Version = 1;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        MaxDepth = 64
    };

    /// <summary>分配正文之前核验完整长度, 截断和非法 JSON 明确失败.</summary>
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaximumBytes) throw Invalid();
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
    }

    /// <summary>先完整序列化及核验上限, 再写入单帧; 调用方保证该连接只有一个发送者.</summary>
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumBytes) throw new AppException("MESSAGE_TOO_LARGE", "共享后台消息超过传输上限", 3);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static bool ValidId(string? value) => value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static AppException Invalid() => new("BUSINESS_HOST_PROTOCOL_INVALID", "共享后台消息格式或身份无效", 3);
}

internal sealed record BusinessHostHello(string Type, int Version, BusinessHostClientOptions Options);
internal sealed record BusinessHostWelcome(string Type, int Version, BusinessHostRegistration? Registration, BusinessHostFailure? Error = null);
internal sealed record BusinessHostFailure(string Code, string Message, int ExitCode);
internal sealed record BusinessHostFrame(string Type, string HostGeneration, string ContextId,
    string? Id = null, string? Method = null, JsonElement? Data = null, BusinessHostFailure? Error = null);
