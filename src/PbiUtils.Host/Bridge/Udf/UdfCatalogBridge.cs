// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Udf\UdfCatalogBridge.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 读取函数目录的独立动作和来源快照, 不拥有本地保存或草稿事务.

using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeBusinessInput;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Udf;

/// <summary>每次请求仍按宿主来源身份与基线取得 provider, 不按同名包跨来源迁移.</summary>
internal sealed partial class UdfCatalogBridge(IUdfPackageProvider? packages, IUdfSourceProvider? sources)
{
    /// <summary>目录的每个动作有独立字段白名单, 只接受宿主来源身份和配置基线.</summary>
    internal async Task<object> ReadAsync(JsonElement input, CancellationToken token)
    {
        var action = String(input, "action");
        var fields = Fields(action);
        if (input.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal))) throw Invalid();
        var provider = await ProviderAsync(input, token);
        if (action is "packages" or "icon") return await ReadBrowserAsync(input, provider, action, token);
        if (action == "status") return new { status = await (provider as IUdfCatalogProvider ?? throw Unsupported()).GetStatusAsync(token) };
        UdfCatalogStatus? status;
        // 浏览仅固定索引与所需包体; 显式status及最终冻结仍保留完整正文核验.
        using var pinned = provider is IUdfCatalogSnapshotSource snapshots ? await snapshots.OpenSnapshotAsync(token) : null;
        if (pinned is not null) { provider = pinned; status = pinned.Status; }
        else status = provider is IUdfCatalogProvider catalog ? await catalog.GetStatusAsync(token) : null;
        if (input.TryGetProperty("catalogBaseline", out var expected) && expected.ValueKind != JsonValueKind.Null)
        {
            var digest = String(input, "catalogBaseline");
            if (digest.Length != 64 || digest.Any(character => !char.IsAsciiHexDigitLower(character))) throw Invalid();
            if (status?.Baseline != digest) throw new AppException("UDF_CATALOG_CHANGED", "函数目录在读取期间已更新, 请重新读取并核验选择", 3);
        }
        var queryText = input.TryGetProperty("query", out _) ? String(input, "query") : "";
        if (queryText.Length > 256 || input.TryGetProperty("includePrerelease", out var include) && include.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        if (action == "search")
        {
            var offset = Integer(input, "offset", 0, 0, 1024);
            var limit = Integer(input, "limit", 50, 1, 100);
            var all = await provider.SearchAsync(queryText, input.TryGetProperty("includePrerelease", out var prerelease) && prerelease.GetBoolean(), token);
            if (all.Count > 1024 || offset > all.Count) throw Invalid();
            var page = new List<UdfPackageSummary>();
            foreach (var item in all.Skip(offset).Take(limit))
            {
                page.Add(item);
                // 摘要页留出协议与状态空间, UTF-8 字节预算覆盖中文等多字节文案.
                if (JsonSerializer.SerializeToUtf8Bytes(page, Json).Length <= 48 * 1024) continue;
                page.RemoveAt(page.Count - 1);
                if (page.Count == 0) throw new AppException("UDF_CATALOG_RESPONSE_LIMIT", "单个函数库摘要超出显示大小限制", 3);
                break;
            }
            return new
            {
                packages = page,
                total = all.Count,
                nextOffset = offset + page.Count < all.Count ? offset + page.Count : (int?)null,
                fromCache = status is not null,
                retrievedAt = status?.RetrievedAt,
                status
            };
        }
        if (action == "versions") return new
        {
            versions = await provider.GetVersionsAsync(String(input, "packageId"), token),
            fromCache = status is not null,
            retrievedAt = status?.RetrievedAt,
            status
        };
        return new { package = await provider.GetAsync(String(input, "packageId"), String(input, "version"), token), status };
    }

    /// <summary>仅显式更新访问远端, 参数仍只接受宿主持久来源身份, 发布边界交给目录存储负责.</summary>
    internal async Task<object> RefreshAsync(JsonElement input, IProgress<ScaffoldProgress>? progress, CancellationToken token, Action? committing = null)
    {
        if (input.EnumerateObject().Any(property => property.Name is not ("source" or "sourceBaseline"))) throw Invalid();
        var provider = await ProviderAsync(input, token) as IUdfCatalogProvider ?? throw Unsupported();
        return await provider.RefreshAsync(progress is null ? null : new CatalogProgress(progress), token, committing);
    }

    /// <summary>只路由已登记且基线匹配的来源, 网页无法指定网络地址或存储路径.</summary>
    private async Task<IUdfPackageProvider> ProviderAsync(JsonElement input, CancellationToken token)
    {
        var source = input.TryGetProperty("source", out _) ? String(input, "source") : "daxlib";
        var sourceBaseline = input.TryGetProperty("sourceBaseline", out var baseline) && baseline.ValueKind != JsonValueKind.Null ? String(input, "sourceBaseline") : null;
        if (!UdfValidation.IsSourceId(source) || source == "local" || sourceBaseline is not null && (sourceBaseline.Length != 64 || sourceBaseline.Any(character => !char.IsAsciiHexDigitLower(character)))) throw Invalid();
        return source == "daxlib" && packages is not null ? packages : await (sources ?? throw Unsupported()).GetProviderAsync(source, sourceBaseline, token);
    }

    /// <summary>有界分页只接受 JSON 整数, 不把负值、浮点或过大偏移解释成有效页.</summary>
    private static int Integer(JsonElement input, string name, int fallback, int minimum, int maximum)
    {
        if (!input.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer) && integer >= minimum && integer <= maximum ? integer : throw Invalid();
    }

    /// <summary>同步转发已经校验的有限进度, 进度不延长请求时限或授权提交.</summary>
    private sealed class CatalogProgress(IProgress<ScaffoldProgress> target) : IProgress<UdfCatalogProgress>
    {
        public void Report(UdfCatalogProgress value) => target.Report(new(value.Total > 0 ? Math.Clamp(100d * value.Completed / value.Total, 0, 99) : 0, value.Stage));
    }
}
