// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Updates\ProductReleaseHttpSource.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定 GitHub/Gitee 仓库的匿名有限分页 HTTP 发布适配.

using System.Globalization;
using System.Net;
using System.Text.Json;
using PbiUtils.Application.Updates;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Updates;

/// <summary>调用方拥有 HttpClient; 工厂客户端禁用重定向, 所有分页共享20秒总预算.</summary>
public sealed class ProductReleaseHttpSource(HttpClient client) : IProductReleaseSource
{
    private const int PageBytes = 4 * 1024 * 1024;
    private const int TotalBytes = 8 * 1024 * 1024;

    /// <summary>完整取回最多10页, API/解析/容量失败抛固定码, 外部取消原样传播.</summary>
    public async Task<ProductRelease[]> ReadAsync(string source, CancellationToken token)
    {
        ProductUpdatePolicy.ValidateSource(source);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var result = new List<ProductRelease>();
            var bytesRead = 0;
            for (var page = 1; page <= 10; page++)
            {
                var api = source == "github" ? "https://api.github.com/repos/jiaopengzi/pbi-utils/releases"
                    : "https://gitee.com/api/v5/repos/jiaopengzi/pbi-utils/releases";
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{api}?per_page=100&page={page}");
                request.Headers.UserAgent.ParseAdd("pbi-utils/" + Product.Version);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden
                    && (response.Headers.RetryAfter is not null || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0", StringComparer.Ordinal)))
                    throw Error("UPDATE_RATE_LIMITED");
                if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri) throw Error("UPDATE_HTTP_FAILED");
                if (response.Content.Headers.ContentLength > PageBytes) throw Error("UPDATE_RESPONSE_TOO_LARGE");
                await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int count;
                while ((count = await stream.ReadAsync(chunk, budget.Token).ConfigureAwait(false)) != 0)
                {
                    bytesRead += count;
                    if (buffer.Length + count > PageBytes || bytesRead > TotalBytes) throw Error("UPDATE_RESPONSE_TOO_LARGE");
                    buffer.Write(chunk, 0, count);
                }
                using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var rows = document.RootElement;
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100) throw Error("UPDATE_INVALID_RESPONSE");
                foreach (var row in rows.EnumerateArray()) result.Add(Parse(row, source));
                if (!HasMore(response, rows.GetArrayLength(), page)) return [.. result];
            }
            throw Error("UPDATE_PAGE_LIMIT");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Error("UPDATE_TIMEOUT"); }
        catch (HttpRequestException) { throw Error("UPDATE_NETWORK_FAILED"); }
        catch (IOException) { throw Error("UPDATE_NETWORK_FAILED"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw Error("UPDATE_INVALID_RESPONSE"); }
    }

    /// <summary>满页或服务器明确声明后续页时继续固定分页, 不跟随响应中的任意分页URL.</summary>
    private static bool HasMore(HttpResponseMessage response, int count, int page)
    {
        var hasLinks = response.Headers.TryGetValues("Link", out var links);
        if (hasLinks && links!.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal)
            || link.Contains("rel=next", StringComparison.Ordinal))) return true;
        if (!response.Headers.TryGetValues("total_page", out var totals)) return !hasLinks && count == 100;
        var values = totals.ToArray();
        if (values.Length != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total < 0)
            throw Error("UPDATE_INVALID_RESPONSE");
        return total > page;
    }

    /// <summary>分别处理 GitHub 的 draft/size 与 Gitee 缺失字段, 不使用 html_url 作为可信链接.</summary>
    private static ProductRelease Parse(JsonElement row, string source)
    {
        if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw Error("UPDATE_INVALID_RESPONSE");
        var tag = Text(row, "tag_name", true);
        var draft = Boolean(row, "draft");
        var preview = Boolean(row, "prerelease");
        var notes = Text(row, "body", false);
        var assets = new List<ProductReleaseAsset>();
        if (row.TryGetProperty("assets", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 100) throw Error("UPDATE_INVALID_RESPONSE");
            foreach (var asset in values.EnumerateArray())
            {
                var name = Text(asset, "name", true);
                var url = Text(asset, "browser_download_url", true);
                long? size = null;
                if (source == "github" && asset.TryGetProperty("size", out var rawSize) && rawSize.ValueKind != JsonValueKind.Null)
                {
                    if (!rawSize.TryGetInt64(out var parsed) || parsed < 0) throw Error("UPDATE_INVALID_RESPONSE");
                    size = parsed;
                }
                assets.Add(new(name, url, size));
            }
        }
        return new(tag, draft, preview, notes, [.. assets]);
    }

    /// <summary>缺少可选纯文本时使用空字符串, 类型错误或必要字段缺失则明确失败.</summary>
    private static string Text(JsonElement row, string name, bool required)
    {
        if (row.ValueKind != JsonValueKind.Object) throw Error("UPDATE_INVALID_RESPONSE");
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw Error("UPDATE_INVALID_RESPONSE");
            return "";
        }
        if (value.ValueKind != JsonValueKind.String) throw Error("UPDATE_INVALID_RESPONSE");
        return value.GetString()!;
    }

    /// <summary>缺失的 draft/prerelease 视为 false, 已存在但非布尔则拒绝.</summary>
    private static bool Boolean(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error("UPDATE_INVALID_RESPONSE");
        return value.GetBoolean();
    }

    /// <summary>固定安全错误, 不输出远端正文、地址或底层异常.</summary>
    private static AppException Error(string code) => new(code, "更新查询未能完成, 请重试或切换来源", 4);
}
