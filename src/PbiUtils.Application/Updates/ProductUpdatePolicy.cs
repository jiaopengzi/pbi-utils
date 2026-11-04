// FilePath    : pbi-utils\src\PbiUtils.Application\Updates\ProductUpdatePolicy.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定仓库正式/预发布列表、版本排序和安全发布页地址的共享策略.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.Updates;

/// <summary>只接受当前 desktop 公开发布, 同时展示正式和预发布, 不混用旧产品版本.</summary>
public static class ProductUpdatePolicy
{
    public const int MaximumReleases = 1000;
    public const int MaximumNotesLength = 65536;

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

    /// <summary>将固定来源映射为 releases 根页; 不依赖更新状态, 非法来源抛参数错误.</summary>
    public static string ReleasesLink(string source)
    {
        ValidateSource(source);
        return $"https://{source}.com/jiaopengzi/pbi-utils/releases";
    }

    /// <summary>解析当前发布流水线的精确 desktop 标签及 v2 轮次; 非法标签返回 false.</summary>
    public static bool TryReleaseTag(string? tag, out Version version, out int revision)
    {
        version = new Version(0, 0, 0, 0);
        revision = 0;
        if (tag is null || tag.Length > 64 || !tag.StartsWith("desktop-v", StringComparison.Ordinal)) return false;
        var value = tag[9..];
        var separator = value.IndexOf("-v2.", StringComparison.Ordinal);
        if (separator < 0) return TryVersion(value, out version);
        var number = value[(separator + 4)..];
        return number.Length > 0 && number[0] != '0' && number.All(character => character is >= '0' and <= '9')
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision > 0
            && TryVersion(value[..separator], out version);
    }

    /// <summary>选择全部公开 desktop 发布, 按数字版本、正式优先和预发布轮次排序; 超量明确失败.</summary>
    public static ProductUpdateCheck Select(string source, string currentVersion, IEnumerable<ProductRelease> releases, DateTimeOffset checkedAt)
    {
        ValidateSource(source);
        if (!TryVersion(currentVersion, out var current)) throw new AppException("INVALID_UPDATE_VERSION", "当前产品版本无效");
        var candidates = releases.Where(release => !release.Draft && TryReleaseTag(release.Tag, out _, out _))
            .Select(release =>
            {
                TryReleaseTag(release.Tag, out var version, out var revision);
                return (Release: release, Version: version, Revision: revision, Preview: release.Prerelease || revision > 0);
            })
            .OrderByDescending(item => item.Version).ThenBy(item => item.Preview)
            .ThenByDescending(item => item.Revision).ThenBy(item => item.Release.Tag, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length > MaximumReleases || candidates.Select(item => item.Release.Tag).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
            throw new AppException("UPDATE_INVALID_RESPONSE", "发布列表过大或包含重复标签", 4);
        var remaining = MaximumNotesLength;
        var entries = candidates.Select(item =>
        {
            var text = item.Release.Notes;
            var length = Math.Min(text.Length, Math.Min(4096, remaining));
            if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            remaining -= length;
            return new ProductUpdateRelease(item.Release.Tag, item.Release.Tag[9..], item.Preview, text[..length], length < text.Length);
        }).ToArray();
        var status = candidates.Length == 0 ? "noRelease" : candidates[0].Version > current ? "available" : "current";
        return new(source, currentVersion, status, checkedAt, entries.FirstOrDefault()?.Version, entries);
    }
}
