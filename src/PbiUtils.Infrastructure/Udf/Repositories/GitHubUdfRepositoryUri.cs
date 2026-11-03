// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\GitHubUdfRepositoryUri.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 离线校验无凭据 HTTP(S) 仓库地址, 保留 GitHub 既有规范身份.

using System.Net;
using System.Text.RegularExpressions;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Udf.Repositories;

/// <summary>来源登记与下载共用地址边界; 类型名保留现有调用契约, 非 GitHub 使用标准 Git HTTP.</summary>
public static partial class GitHubUdfRepositoryUri
{
    /// <summary>不联网规范地址; 拒绝凭据、转义和路径歧义, 允许无需登录的内网 HTTP 仓库.</summary>
    /// <param name="value">ASCII HTTP(S) 仓库地址, 非 GitHub 可含端口和多层命名空间.</param>
    /// <returns>GitHub 保持小写且移除 .git; 其他主机保留路径大小写及 .git, 均移除末尾斜线.</returns>
    public static Uri Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !value.All(char.IsAscii) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)
            || value.IndexOfAny(['%', '\\', '?', '#', '@']) >= 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Host.Length == 0 || uri.Host.EndsWith('.')
            || uri.Port is < 1 or > 65535) throw Invalid();
        if (uri.Host != "github.com")
        {
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && !UdfSourceNetwork.IsRepositoryAddress(address)) throw Invalid();
            var pathStart = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
            if (pathStart < 0) throw Invalid();
            var path = value[pathStart..];
            if (path.EndsWith('/')) path = path[..^1];
            if (path.Length < 2 || path[1..].Split('/').Any(part => part is "" or "." or ".." or ".git" || part.Length > 255
                || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))) throw Invalid();
            return new Uri(uri.GetLeftPart(UriPartial.Authority) + path);
        }
        var match = RepositoryPattern().Match(value);
        if (!match.Success) throw Invalid();
        var owner = match.Groups[1].Value.ToLowerInvariant();
        var repository = match.Groups[2].Value;
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repository = repository[..^4];
        if (repository.Length is < 1 or > 100 || repository is "." or "..") throw Invalid();
        return new Uri($"https://github.com/{owner}/{repository.ToLowerInvariant()}");
    }

    /// <summary>只接受 ASCII 仓库标识, 避免 URL 解码和路径规范化改变目标.</summary>
    [GeneratedRegex(@"\Ahttps://github\.com(?::443)?/([a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?)/([a-z0-9_.-]{1,104})/?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    /// <summary>固定错误不回显用户地址或潜在凭据.</summary>
    private static AppException Invalid() => new("UDF_SOURCE_REPOSITORY_INVALID", "请填写无需登录且不含凭据、查询或片段的 HTTP(S) Git 仓库地址");
}
