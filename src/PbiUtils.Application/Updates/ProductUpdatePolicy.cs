// FilePath    : pbi-utils\src\PbiUtils.Application\Updates\ProductUpdatePolicy.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定仓库更新版本、附件和安全链接的共享选择策略.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.Updates;

/// <summary>只接受当前 desktop 正式发布, 不混用旧产品或其他版本附件.</summary>
public static class ProductUpdatePolicy
{
    /// <summary>正式软件更新仅分发不含 .NET 的 ZIP 包, 内含安装器和 SHA256, 运行时由安装器按需补齐.</summary>
    public static IReadOnlyList<string> Modes { get; } = Array.AsReadOnly(["without-dotnet"]);

    /// <summary>拒绝未知来源, 不允许自由主机或地址.</summary>
    public static void ValidateSource(string source)
    {
        if (source is not ("github" or "gitee")) throw new AppException("INVALID_UPDATE_SOURCE", "更新来源无效");
    }

    /// <summary>保存只接受至少一个明确修改字段, 可选来源仍执行固定白名单.</summary>
    public static void ValidatePatch(ProductUpdateSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.AutomaticCheck is null && patch.Source is null) throw new AppException("VALIDATION_ERROR", "至少提供一项更新设置");
        if (patch.Source is not null) ValidateSource(patch.Source);
    }

    /// <summary>解析无前导零的三段或四段稳定版本, 以四段零补齐比较.</summary>
    public static bool TryVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null || value.Length > 43) return false;
        var parts = value.Split('.');
        if (parts.Length is not (3 or 4)) return false;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || (part.Length > 1 && part[0] == '0') || part.Any(character => character is < '0' or > '9')
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])) return false;
        }
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>根据固定来源、版本和目标构建唯一发布页或 ZIP 包地址; 非法版本/目标抛参数错误.</summary>
    public static string Link(string source, string version, string target)
    {
        ValidateSource(source);
        if (!TryVersion(version, out _) || (target != "release" && !Modes.Contains(target, StringComparer.Ordinal)))
            throw new AppException("INVALID_UPDATE_TARGET", "更新目标无效");
        var prefix = $"https://{source}.com/jiaopengzi/pbi-utils/releases/";
        return target == "release" ? prefix + "tag/desktop-v" + version
            : prefix + $"download/desktop-v{version}/pbi-utils-{version}-win-x64-{target}.zip";
    }

    /// <summary>从完整发布列表选择最高正式版本和同源同版本唯一 ZIP; 正文保留原文本, 仅限制显示长度.</summary>
    public static ProductUpdateCheck Select(string source, string currentVersion, IEnumerable<ProductRelease> releases, DateTimeOffset checkedAt)
    {
        ValidateSource(source);
        if (!TryVersion(currentVersion, out var current)) throw new AppException("INVALID_UPDATE_VERSION", "当前产品版本无效");
        var latest = releases.Where(release => !release.Draft && !release.Prerelease && release.Tag.StartsWith("desktop-v", StringComparison.Ordinal)
                && TryVersion(release.Tag[9..], out _))
            .OrderByDescending(release => { TryVersion(release.Tag[9..], out var version); return version; })
            .ThenBy(release => release.Tag, StringComparer.Ordinal).FirstOrDefault();
        if (latest is null) return new(source, currentVersion, "noRelease", checkedAt, null, null, "", []);
        var value = latest.Tag[9..];
        TryVersion(value, out var parsed);
        var assets = new List<ProductUpdateAsset>();
        foreach (var mode in Modes)
        {
            var name = $"pbi-utils-{value}-win-x64-{mode}.zip";
            var url = Link(source, value, mode);
            var candidates = latest.Assets.Where(asset => asset.Name == name && asset.Url == url && (asset.Size is null or >= 0)).ToArray();
            if (candidates.Length == 1) assets.Add(new(mode, name, url, candidates[0].Size));
        }
        var length = Math.Min(latest.Notes.Length, 65536);
        if (length < latest.Notes.Length && length > 0 && char.IsHighSurrogate(latest.Notes[length - 1])) length--;
        var notes = latest.Notes[..length];
        return new(source, currentVersion, parsed > current ? "available" : "current", checkedAt, value, Link(source, value, "release"), notes, [.. assets]);
    }
}
