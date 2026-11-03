// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Commands\ScaffoldCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 严格解析新建脚手架命令, 复用实际预览和跨进程精确批准.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;

namespace PbiUtils.Infrastructure.Commands;

/// <summary>控制台只持有显式参数, 业务和文件提交共用桌面用例.</summary>
public static class ScaffoldCommands
{
    public const string HelpText = """
        pbi-utils new templates [--json]
        pbi-utils new plan --request <配置.json> --output <新目录> [--git] [--json]
        pbi-utils new create --request <配置.json> --output <新目录> --approve <digest> [--git] [--json]
        pbi-utils udf catalog status|refresh [--source <ID>] [--source-baseline <摘要>] [--json]
        pbi-utils udf catalog search [--query <文本>] [--prerelease] [--json]
        pbi-utils udf catalog show --package <ID> --version <版本> [--json]
        pbi-utils udf local list|show|create|update|delete [--id <ID>] [--request <函数.json>] [--baseline <摘要>] [--confirm] [--json]
        """;

    /// <summary>请求文件与 Git 选项必须和已预览摘要完整一致.</summary>
    internal static async Task<object> RunAsync(string[] args, string userRoot, ILoggerFactory logs, CancellationToken token,
        Action? committing = null, Action<JsonElement>? report = null)
    {
        if (args.Length < 2 || args[1] is not ("templates" or "plan" or "create")) throw CommandInput.Invalid();
        var action = args[1];
        var values = CommandInput.Options(args, 2, action == "templates" ? [] : action == "plan" ? ["--request", "--output"] : ["--request", "--output", "--approve"],
            action == "templates" ? [] : ["--git"]);
        using var runtime = new ScaffoldRuntime(userRoot, logs);
        if (action == "templates") return new { templates = await runtime.Catalog.ListAsync(token), git = await new ScaffoldGit().ProbeAsync(token) };
        var file = CommandInput.Required(values, "--request");
        var output = CommandInput.Required(values, "--output");
        var request = await CommandInput.RequestAsync<ScaffoldRequest>(file, token);
        if (values.ContainsKey("--git")) request = request with { GitInitialize = true };
        ScaffoldValidation.Validate(request);
        var progress = new ProgressSink(report);
        if (action == "plan") return await runtime.Service.PreviewAsync(request, output, progress, token);
        return await runtime.Service.CreateAsync(CommandInput.Required(values, "--approve"), request, output, progress, token, committing);
    }

    /// <summary>阶段只写 stderr, 不将日志或数据文件放入 stdout.</summary>
    private sealed class ProgressSink(Action<JsonElement>? report) : IProgress<ScaffoldProgress>
    {
        private int lastPercent = -1;
        private string? lastStage;
        /// <summary>按真实阶段和整数百分比限流.</summary>
        public void Report(ScaffoldProgress value)
        {
            var percent = (int)Math.Floor(value.Percent);
            if (lastPercent == percent && lastStage == value.Stage) return;
            lastPercent = percent; lastStage = value.Stage;
            report?.Invoke(JsonSerializer.SerializeToElement(new { type = "scaffoldProgress", value.Percent, value.Stage, value.CanCancel }, JsonSerializerOptions.Web));
        }
    }
}

/// <summary>新命令共用严格选项与 JSON 边界, 不执行 shell 或加载任意代码.</summary>
public static class CommandInput
{
    private static readonly JsonSerializerOptions Json = ScaffoldInputJson.Create();

    /// <summary>未知、重复、缺值和不适用开关均失败, json 只可出现一次.</summary>
    public static Dictionary<string, string> Options(string[] args, int offset, string[] valueOptions, string[] flags)
    {
        Dictionary<string, string> result = [];
        for (var index = offset; index < args.Length; index++)
        {
            var option = args[index];
            if (result.ContainsKey(option)) throw Invalid();
            if (option == "--json" || flags.Contains(option, StringComparer.Ordinal)) { result.Add(option, ""); continue; }
            if (!valueOptions.Contains(option, StringComparer.Ordinal) || ++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw Invalid();
            result.Add(option, args[index]);
        }
        return result;
    }

    /// <summary>读取业务必需值, 不补隐含路径或批准.</summary>
    public static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw Invalid();

    /// <summary>有界 UTF-8 严格请求, 拒绝重复属性与未知字段.</summary>
    public static async Task<T> RequestAsync<T>(string path, CancellationToken token)
    {
        UserDataStore.Check(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) throw Invalid();
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (bytes.Length > 4 * 1024 * 1024) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            JsonInputValidation.RejectDuplicates(document.RootElement);
            return document.RootElement.Deserialize<T>(Json) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }

    /// <summary>固定参数提示, 不回显任意正文或路径.</summary>
    public static AppException Invalid() => new("INVALID_ARGUMENT", "命令参数无效, 请查看 --help 并检查必需参数");
}
