// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Commands\SampleCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 示例数据 CLI 参数边界与共享全量、增量、统计和清空入口.

using PbiUtils.Infrastructure.Input;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.SampleData;

namespace PbiUtils.Infrastructure.Commands;

/// <summary>CLI 仅解析参数, 所有生成规则和文件事务复用桌面共享服务.</summary>
internal static class SampleCommands
{
    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    /// <summary>验证显式目录、请求文件及覆盖/清空授权, 返回可序列化业务结果.</summary>
    internal static async Task<object> RunAsync(string[] args, ILoggerFactory logs, CancellationToken token,
        Action? committing = null, Action<JsonElement>? report = null)
    {
        if (args.Length < 2 || args[1] is not ("generate" or "append" or "inspect" or "clear"))
            throw new AppException("INVALID_ARGUMENT", "需要 sample generate、append、inspect 或 clear");
        var action = args[1];
        Dictionary<string, string> values = [];
        HashSet<string> seen = [];
        for (var i = 2; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new AppException("INVALID_ARGUMENT", "选项不能重复");
            if (option == "--json") continue;
            if (option == "--overwrite" && action == "generate" || option == "--confirm" && action == "clear") continue;
            if (option != "--output" && !(option == "--request" && action is "generate" or "append"))
                throw new AppException("INVALID_ARGUMENT", "未知或不适用的选项");
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new AppException("INVALID_ARGUMENT", "选项缺少值");
            values.Add(option, args[i]);
        }
        if (!values.TryGetValue("--output", out var output) || string.IsNullOrWhiteSpace(output) || output.Length > 2048)
            throw new AppException("INVALID_ARGUMENT", "需要 --output <示例目录>");
        var service = new SampleDataService(new SampleDataAdapter(logs.CreateLogger<SampleDataAdapter>()), logs.CreateLogger<SampleDataService>());
        if (action == "inspect") return await service.GetAsync(output, token);
        if (action == "clear")
        {
            if (!seen.Contains("--confirm")) throw new AppException("INVALID_ARGUMENT", "清空 11 张 CSV 和整个 pbip 子目录需要 --confirm");
            return await service.ClearAsync(output, beginCommit: committing, cancellationToken: token);
        }
        if (!values.TryGetValue("--request", out var requestFile) || !File.Exists(requestFile) || new FileInfo(requestFile).Length > 65536)
            throw new AppException("INVALID_ARGUMENT", "需要不超过64KiB的 --request <参数.json>");
        SampleDataOptions options;
        try
        {
            var bytes = await File.ReadAllBytesAsync(requestFile, token);
            if (bytes.Length > 65536) throw new AppException("INVALID_ARGUMENT", "参数文件超过64KiB");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new AppException("INVALID_ARGUMENT", "参数必须为 JSON 对象");
            JsonInputValidation.RejectDuplicates(document.RootElement);
            options = document.RootElement.Deserialize<SampleDataOptions>(StrictOptions)
                ?? throw new AppException("INVALID_ARGUMENT", "参数为空");
        }
        catch (JsonException) { throw new AppException("INVALID_ARGUMENT", "参数文件格式或字段无效"); }
        var progress = new ProgressSink(report);
        return action == "generate"
            ? await service.GenerateAsync(output, options, seen.Contains("--overwrite"), progress, beginCommit: committing, cancellationToken: token)
            : await service.IncrementAsync(output, options, progress, beginCommit: committing, cancellationToken: token);
    }

    /// <summary>阶段只写 stderr, stdout 保持恰好一个业务 JSON 文档.</summary>
    private sealed class ProgressSink(Action<JsonElement>? report) : IProgress<SampleDataProgress>
    {
        private double lastPercent = -1;
        private string? lastStage;
        /// <summary>按真实阶段与整数百分比限流, 防止大量门店生成刷屏.</summary>
        public void Report(SampleDataProgress value)
        {
            var percent = Math.Floor(value.Percent);
            if (percent == lastPercent && value.Stage == lastStage) return;
            lastPercent = percent; lastStage = value.Stage;
            report?.Invoke(JsonSerializer.SerializeToElement(new { type = "sampleProgress", percent = value.Percent, value.Stage }, JsonSerializerOptions.Web));
        }
    }
}
