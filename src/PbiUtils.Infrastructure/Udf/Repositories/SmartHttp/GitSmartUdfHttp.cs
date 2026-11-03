// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\SmartHttp\GitSmartUdfHttp.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为显式仓库提供有界、同源、无凭据且拒绝重定向的 Git smart HTTP 请求.

using System.Net;
using System.Net.Http.Headers;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Udf.Repositories;

namespace PbiUtils.Infrastructure.Udf.Repositories.SmartHttp;

/// <summary>仅构造 info/refs 和 git-upload-pack 端点, 不使用服务器返回的 URL 或错误正文.</summary>
internal sealed class GitSmartUdfHttp(HttpClient client, Uri repository)
{
    /// <summary>获取 Git v0/v1 引用广告, HTML 登录页面或静态 dumb HTTP 不作为成功结果.</summary>
    internal Task<byte[]> AdvertiseAsync(CancellationToken cancellationToken) => SendAsync("info/refs?service=git-upload-pack", null,
        "application/x-git-upload-pack-advertisement", 8 * 1024 * 1024, cancellationToken);

    /// <summary>提交一次无凭据浅获取, 仅响应压缩字节在容量限制内时返回内存包.</summary>
    internal Task<byte[]> FetchAsync(byte[] body, CancellationToken cancellationToken) => SendAsync("git-upload-pack", body,
        "application/x-git-upload-pack-result", GitUdfFetch.MaxContentBytes, cancellationToken);

    /// <summary>请求和实际最终地址必须相同; 每轮两分钟且保留调用方取消, 长度和流式预算均校验.</summary>
    private async Task<byte[]> SendAsync(string endpoint, byte[]? body, string mediaType, long maximum, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        var uri = new Uri(repository.AbsoluteUri.TrimEnd('/') + "/" + endpoint);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        request.Headers.UserAgent.ParseAdd("pbi-utils-udf/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        if (body is not null) request.Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/x-git-upload-pack-request") } };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } actual && actual != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new AppException("UDF_GIT_REDIRECT", "Git 仓库不接受重定向, 请使用仓库当前克隆地址");
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new AppException("UDF_GIT_AUTH_REQUIRED", "Git 仓库必须无需登录即可读取");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new AppException("UDF_GIT_FORBIDDEN", "Git 仓库拒绝匿名读取");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new AppException("UDF_GIT_RATE_LIMIT", "Git 仓库请求额度已用尽, 请稍后重新更新");
        if (response.StatusCode != HttpStatusCode.OK) throw new AppException("UDF_GIT_HTTP_FAILED", "Git 仓库或引用读取失败, 原目录保持不变");
        if (response.Content.Headers.ContentType?.MediaType != mediaType || response.Content.Headers.ContentEncoding.Count != 0) throw GitUdfCatalogSource.Invalid();
        if (response.Content.Headers.ContentLength is { } size && (size < 0 || size > maximum)) throw Limit();
        await using var input = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, bounded.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximum) throw Limit();
            output.Write(buffer, 0, read);
        }
        if (response.Content.Headers.ContentLength is { } expected && output.Length != expected) throw GitUdfCatalogSource.Invalid();
        return output.ToArray();
    }

    /// <summary>预算错误不泄漏目标或远端正文.</summary>
    private static AppException Limit() => new("UDF_GIT_LIMIT", "Git 来源超过下载或容量限制");
}
