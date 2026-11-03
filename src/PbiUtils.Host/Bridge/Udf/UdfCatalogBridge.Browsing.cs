// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Udf\UdfCatalogBridge.Browsing.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 严格分发包级摘要分页与包图标, 不向网页开放路径或网络入口.

using System.Text.Json;
using PbiUtils.Application.Udf;
using static PbiUtils.Host.Bridge.Protocol.BridgeBusinessInput;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Udf;

internal sealed partial class UdfCatalogBridge
{
    /// <summary>每个读取动作单独限定字段, 目录搜索与完整包读取分别限制输入.</summary>
    private static string[] Fields(string action) => action switch
    {
        "status" => ["action", "source", "sourceBaseline"],
        "search" => ["action", "query", "includePrerelease", "source", "sourceBaseline", "offset", "limit", "catalogBaseline"],
        "versions" => ["action", "packageId", "source", "sourceBaseline", "catalogBaseline"],
        "package" => ["action", "packageId", "version", "source", "sourceBaseline", "catalogBaseline"],
        "packages" => ["action", "query", "includePrerelease", "source", "sourceBaseline", "offset", "limit", "packageVersions", "catalogBaseline"],
        "icon" => ["action", "packageId", "version", "iconKey", "source", "sourceBaseline", "catalogBaseline"],
        _ => throw Invalid()
    };

    /// <summary>新浏览接口自行绑定不可变目录, 避免先读取完整状态再重复读取所有包体.</summary>
    private static async Task<object> ReadBrowserAsync(JsonElement input, IUdfPackageProvider provider, string action, CancellationToken token)
    {
        var browser = provider as IUdfCatalogBrowser ?? throw Unsupported();
        var baseline = OptionalDigest(input, "catalogBaseline");
        if (action == "packages")
        {
            var query = input.TryGetProperty("query", out _) ? String(input, "query") : "";
            if (query.Length > 256 || input.TryGetProperty("includePrerelease", out var include) && include.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            var request = new UdfPackageQuery(query, input.TryGetProperty("includePrerelease", out var prerelease) && prerelease.GetBoolean(),
                Integer(input, "offset", 0, 0, 1024), Integer(input, "limit", 50, 1, 500), PackageVersions(input));
            return new { packagePage = await browser.QueryPackagesAsync(request, baseline, token) };
        }
        var packageId = String(input, "packageId");
        var version = String(input, "version");
        UdfValidation.PackageIdentity(packageId, version);
        var iconKey = String(input, "iconKey");
        if (!Digest(iconKey)) throw Invalid();
        return new { icon = await browser.GetIconAsync(packageId, version, iconKey, baseline, token) };
    }

    /// <summary>逐库tag只接受有界精确版本映射, 拒绝null、重复大小写身份及网页路径.</summary>
    private static Dictionary<string, string>? PackageVersions(JsonElement input)
    {
        if (!input.TryGetProperty("packageVersions", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (versions.Count >= 1024 || property.Value.ValueKind != JsonValueKind.String) throw Invalid();
            var version = property.Value.GetString()!;
            UdfValidation.PackageIdentity(property.Name, version);
            if (!versions.TryAdd(property.Name, version)) throw Invalid();
        }
        return versions;
    }

    /// <summary>可选目录见证允许省略或null, 一旦携带必须为完整小写SHA256摘要.</summary>
    private static string? OptionalDigest(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var digest = String(input, name);
        return Digest(digest) ? digest : throw Invalid();
    }

    /// <summary>摘要是宿主生成的固定身份, 不接受路径、URL或变长令牌.</summary>
    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
}
