// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitHub\GitHubUdfHttp.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为 GitHub 元数据和归档提供有界、无凭据、拒绝重定向的 HTTPS 请求.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Udf.Packages;
using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.GitHub;

/// <summary>只请求调用方构造的官方端点, 不读取 HTTP 错误正文或携带仓库凭据.</summary>
internal sealed class GitHubUdfHttp(HttpClient client)
{
    /// <summary>请求有界 JSON 并拒绝重复字段; HTML、错误类型和坏 JSON 均安全失败.</summary>
    internal async Task<JsonDocument> JsonAsync(string url, int maximum, CancellationToken cancellationToken)
    {
        var bytes = await GetAsync(url, maximum, archive: false, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => UdfJson.Parse(bytes), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>归档下载有独立压缩字节上限, 展开限制由归档读取器再核验.</summary>
    internal Task<byte[]> ArchiveAsync(string url, CancellationToken cancellationToken) => GetAsync(url, GitUdfFetch.MaxContentBytes, archive: true, cancellationToken);

    /// <summary>每个请求限制两分钟; 非成功状态不会解析正文或跟随 Location.</summary>
    private async Task<byte[]> GetAsync(string url, long maximum, bool archive, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Host != (archive ? "codeload.github.com" : "api.github.com")) throw GitUdfCatalogSource.Invalid();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("pbi-utils-udf/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(archive ? "application/zip" : "application/vnd.github+json"));
        if (!archive) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } actual && actual != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new AppException("UDF_GIT_REDIRECT", "GitHub 来源不接受重定向, 请使用仓库当前公开地址");
        if (response.StatusCode is HttpStatusCode.Unauthorized)
            throw new AppException("UDF_GIT_AUTH_REQUIRED", "GitHub 来源必须是无需登录的公开仓库");
        if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))
            throw new AppException("UDF_GIT_RATE_LIMIT", "GitHub 请求额度已用尽, 请稍后重新更新");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new AppException("UDF_GIT_FORBIDDEN", "GitHub 拒绝访问该公开仓库");
        if (response.StatusCode != HttpStatusCode.OK) throw new AppException("UDF_GIT_HTTP_FAILED", "GitHub 仓库或引用读取失败, 原目录保持不变");
        var type = response.Content.Headers.ContentType?.MediaType;
        if (archive ? type is not ("application/zip" or "application/octet-stream" or "application/x-zip-compressed")
            : type is not ("application/json" or "application/vnd.github+json")) throw GitUdfCatalogSource.Invalid();
        if (response.Content.Headers.ContentLength is { } length && (length < 0 || length > maximum)) throw Limit();
        await using var input = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, bounded.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximum) throw Limit();
            await output.WriteAsync(buffer.AsMemory(0, read), bounded.Token).ConfigureAwait(false);
        }
        if (response.Content.Headers.ContentLength is { } expected && output.Length != expected) throw GitUdfCatalogSource.Invalid();
        return output.ToArray();
    }

    /// <summary>预算错误不包含来源 URL 或响应正文.</summary>
    private static AppException Limit() => new("UDF_GIT_LIMIT", "GitHub 来源超过下载或容量限制");
}
