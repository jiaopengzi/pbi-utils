// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Commands\UdfCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 函数包只读查询及稳定 ID 和基线绑定的本地函数管理命令.

using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Udf;

using PbiUtils.Infrastructure.Udf.Sources;

namespace PbiUtils.Infrastructure.Commands;

/// <summary>保持既有 udf list --project 路径, 仅处理 catalog/local 子命令.</summary>
internal static class UdfCommands
{
    /// <summary>所有模型安装仍由脚手架计划处理, 此入口不写当前模型.</summary>
    internal static async Task<object> RunAsync(string[] args, string userRoot, CancellationToken token, Action? committing = null)
    {
        if (args.Length < 3) throw CommandInput.Invalid();
        var action = args[2];
        if (args[1] == "catalog")
        {
            if (action is not ("search" or "show" or "status" or "refresh")) throw CommandInput.Invalid();
            string[] catalogOptions = action switch
            {
                "search" => ["--query", "--source", "--source-baseline"],
                "show" => ["--package", "--version", "--source", "--source-baseline"],
                _ => ["--source", "--source-baseline"]
            };
            var options = CommandInput.Options(args, 3, catalogOptions, action == "search" ? ["--prerelease"] : []);
            using var sources = new UdfSourceProvider(userRoot);
            var provider = await sources.GetProviderAsync(options.GetValueOrDefault("--source") ?? "daxlib", options.GetValueOrDefault("--source-baseline"), token);
            if (action is "status" or "refresh")
            {
                var catalog = provider as IUdfCatalogProvider ?? throw new AppException("UNSUPPORTED", "当前来源不支持完整目录", 3);
                if (action == "status") return await catalog.GetStatusAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(10));
                return await catalog.RefreshAsync(null, deadline.Token, committing);
            }
            if (action == "search") return new { packages = await provider.SearchAsync(options.GetValueOrDefault("--query") ?? "", options.ContainsKey("--prerelease"), token) };
            return new { package = await provider.GetAsync(CommandInput.Required(options, "--package"), CommandInput.Required(options, "--version"), token) };
        }
        if (args[1] != "local" || action is not ("list" or "show" or "create" or "update" or "delete")) throw CommandInput.Invalid();
        string[] valueNames = action switch
        {
            "list" => [],
            "show" => ["--id"],
            "create" => ["--request"],
            "update" => ["--id", "--baseline", "--request"],
            _ => ["--id", "--baseline"]
        };
        var values = CommandInput.Options(args, 3, valueNames, action == "delete" ? ["--confirm"] : []);
        var store = new LocalUdfStore(userRoot);
        if (action == "list") return new { items = await store.ListAsync(token) };
        if (action == "show") return await store.GetAsync(CommandInput.Required(values, "--id"), token);
        if (action == "create") return await store.CreateAsync(await CommandInput.RequestAsync<LocalUdfDefinition>(CommandInput.Required(values, "--request"), token), token, committing);
        if (action == "update") return await store.UpdateAsync(CommandInput.Required(values, "--id"), CommandInput.Required(values, "--baseline"),
            await CommandInput.RequestAsync<LocalUdfDefinition>(CommandInput.Required(values, "--request"), token), token, committing);
        if (!values.ContainsKey("--confirm")) throw new AppException("INVALID_ARGUMENT", "删除本地函数需要 --confirm");
        await store.DeleteAsync(CommandInput.Required(values, "--id"), CommandInput.Required(values, "--baseline"), token, committing);
        return new { deleted = true };
    }
}
