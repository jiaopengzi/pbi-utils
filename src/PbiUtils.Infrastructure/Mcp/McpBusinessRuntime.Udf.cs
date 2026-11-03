// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpBusinessRuntime.Udf.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : MCP 复用完整本地 UDF 目录和精确版本函数存储, 联网仅经显式刷新.

using PbiUtils.Application.Udf;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Mcp;

public sealed partial class McpBusinessRuntime
{
    /// <summary>注册目录读取、显式更新与本地函数 CRUD, 每个写工具均由公共启动权限约束.</summary>
    private void RegisterUdfTools()
    {
        Register<EmptyInput>("udf_sources", "List registered UDF sources and their exact baselines. Does not connect to the network.", false,
            async (_, token) => new { sources = await scaffold.Sources.Sources.ListAsync(token) });
        Register<UdfSourceSaveInput>("udf_source_save", "Register or update a UDF Git source configuration. Requires --allow-write; an update requires its existing id and baseline. Saving does not connect to the network or execute repository code.", true,
            async (input, token) => await scaffold.Sources.Sources.SaveAsync(input.Name, input.BaseUrl, input.Id, input.Baseline, token, CommitLocalWrite, gitRef: input.GitRef));
        Register<UdfDeleteInput>("udf_source_delete", "Remove an exact custom UDF source configuration with its current baseline. Requires --allow-write and confirm=true; built-in sources cannot be removed.", true,
            async (input, token) => { RequireConfirmation(input.Confirm); await scaffold.Sources.Sources.DeleteAsync(input.Id, input.Baseline, token, CommitLocalWrite); return new { deleted = true }; });
        Register<UdfSourceInput>("udf_catalog_status", "Read local full-catalog status. An uninitialized catalog is not treated as an empty successful search.", false,
            async (input, token) => await (await CatalogAsync(input.Source, input.SourceBaseline, token)).GetStatusAsync(token));
        Register<UdfSourceInput>("udf_catalog_refresh", "Explicitly download and atomically replace the full UDF catalog for the selected registered source. Requires --allow-write; this tool accesses the network. Failed/cancelled updates retain the old catalog.", true,
            async (input, token) =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(10));
                return await (await CatalogAsync(input.Source, input.SourceBaseline, deadline.Token)).RefreshAsync(null, deadline.Token, CommitLocalWrite);
            }, openWorld: true);
        Register<UdfSearchInput>("udf_catalog_search", "Search the existing complete local UDF catalog. No network request; call udf_catalog_refresh explicitly if initialization is needed.", false,
            async (input, token) => new { packages = await (await scaffold.Sources.GetProviderAsync(input.Source, input.SourceBaseline, token)).SearchAsync(input.Query, input.IncludePrerelease, token) });
        Register<UdfPackageInput>("udf_catalog_versions", "List exact versions available for one package in the local catalog.", false,
            async (input, token) => new { versions = await (await scaffold.Sources.GetProviderAsync(input.Source, input.SourceBaseline, token)).GetVersionsAsync(input.PackageId, token) });
        Register<UdfVersionInput>("udf_catalog_show", "Read an exact local-catalog package including functions, dependencies and licenses. Does not install it.", false,
            async (input, token) => await (await scaffold.Sources.GetProviderAsync(input.Source, input.SourceBaseline, token)).GetAsync(input.PackageId, input.Version, token));
        Register<EmptyInput>("udf_local_list", "List local custom UDF versions and their storage baselines.", false,
            async (_, token) => new { items = await scaffold.Local.ListAsync(token) });
        Register<IdInput>("udf_local_show", "Read one local function by the exact host-issued ID.", false,
            async (input, token) => await scaffold.Local.GetAsync(input.Id, token));
        Register<UdfCreateInput>("udf_local_create", "Create one local UDF version in the user's library. Requires --allow-write. Does not modify any model.", true,
            async (input, token) => await scaffold.Local.CreateAsync(input.Definition, token, CommitLocalWrite));
        Register<UdfUpdateInput>("udf_local_update", "Update the exact local UDF ID only when its current baseline matches. Requires --allow-write. Does not modify installed model functions.", true,
            async (input, token) => await scaffold.Local.UpdateAsync(input.Id, input.Baseline, input.Definition, token, CommitLocalWrite));
        Register<UdfDeleteInput>("udf_local_delete", "Delete one exact local UDF version with a matching baseline. Requires --allow-write and confirm=true. Other versions and installed functions remain.", true,
            async (input, token) => { RequireConfirmation(input.Confirm); await scaffold.Local.DeleteAsync(input.Id, input.Baseline, token, CommitLocalWrite); return new { deleted = true }; });
    }

    /// <summary>从已登记来源取得完整目录能力; 未支持时明确失败, 不隐式切换来源.</summary>
    private async Task<IUdfCatalogProvider> CatalogAsync(string source, string? baseline, CancellationToken token)
        => await scaffold.Sources.GetProviderAsync(source, baseline, token) as IUdfCatalogProvider
            ?? throw new AppException("UNSUPPORTED", "当前来源不支持完整目录", 3);
}
