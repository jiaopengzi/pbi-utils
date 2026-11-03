// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Commands\ChangeCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 后台执行严格副本请求和精确批准, 使用共享提交回调保存可靠回执.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Input;
using PbiUtils.Application.Projects;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Commands;

/// <summary>仅处理副本变更, 所有最终写入在共享后台完成.</summary>
internal static class ChangeCommands
{
    private static readonly JsonSerializerOptions StrictChangeJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    /// <summary>严格读取有界请求并核对批准摘要; 失败不宣称写入成功.</summary>
    internal static async Task<object> RunAsync(string[] args, ILoggerFactory logs, Action? committing, string workingDirectory, CancellationToken token)
    {
        if (args.Length < 2 || args[1] is not ("plan" or "apply")) throw new AppException("INVALID_ARGUMENT", "需要 change plan 或 change apply");
        Dictionary<string, string> values = [];
        HashSet<string> seenChange = [];
        for (var i = 2; i < args.Length; i++)
        {
            var option = args[i];
            if (!seenChange.Add(option)) throw new AppException("INVALID_ARGUMENT", "选项不能重复");
            if (option == "--json") continue;
            if (option != "--request" && !(option == "--approve" && args[1] == "apply")) throw new AppException("INVALID_ARGUMENT", "未知或不适用的选项");
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new AppException("INVALID_ARGUMENT", "选项缺少值");
            values.Add(option, args[i]);
        }
        if (!values.TryGetValue("--request", out var requestFile) || (args[1] == "apply" && !values.ContainsKey("--approve")))
            throw new AppException("INVALID_ARGUMENT", "需要请求文件; apply 还需要精确的 --approve 摘要");
        if (!File.Exists(requestFile) || new FileInfo(requestFile).Length > 65536) throw new AppException("INVALID_ARGUMENT", "请求文件不存在或超过64KiB");
        ChangeRequest request;
        try
        {
            var bytes = await File.ReadAllBytesAsync(requestFile, token);
            if (bytes.Length > 65536) throw new AppException("INVALID_ARGUMENT", "请求文件超过64KiB");
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.RootElement.EnumerateObject().Count())
                throw new AppException("INVALID_ARGUMENT", "请求必须为字段不重复的JSON对象");
            JsonInputValidation.RejectDuplicates(doc.RootElement);
            request = JsonSerializer.Deserialize<ChangeRequest>(bytes, StrictChangeJson) ?? throw new AppException("INVALID_ARGUMENT", "请求为空");
        }
        catch (JsonException) { throw new AppException("INVALID_ARGUMENT", "请求文件格式或字段无效"); }
        request = request with { ProjectPath = Path.GetFullPath(request.ProjectPath, workingDirectory), OutputPath = Path.GetFullPath(request.OutputPath, workingDirectory) };
        using var changes = new ChangeService(new ChangeWorkspace(logger: logs.CreateLogger<ChangeWorkspace>()), TimeProvider.System, logs.CreateLogger<ChangeService>());
        var plan = await changes.PrepareAsync(request, token);
        object data = args[1] == "plan" ? plan : await changes.ApplyAsync(plan.Id, values["--approve"], token, committing);
        return data;
    }
}
