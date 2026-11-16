// FilePath    : pbi-utils\src\PbiUtils.Application\Updates\ProductUpdatePolicy.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定仓库正式/预发布列表、版本排序和安全发布页地址的共享策略.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.Updates;

/// <summary>筛选当前产品的公开发布并按版本及预发布阶段排序.</summary>
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

    /// <summary>解析无前导零的三段数字版本, 任一段超过 65534 时返回 false.</summary>
    private static bool TryVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (value is null || value.Length > 17) return false;
        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || (part.Length > 1 && part[0] == '0') || part.Any(character => character is < '0' or > '9')
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] > 65534) return false;
        }
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>将固定来源映射为 releases 根页; 不依赖更新状态, 非法来源抛参数错误.</summary>
    public static string ReleasesLink(string source)
    {
        ValidateSource(source);
        return $"https://{source}.com/jiaopengzi/pbi-utils/releases";
    }

    /// <summary>解析完整发布标签, 仅接受精确版本和预发布编号格式.</summary>
    /// <param name="tag">从固定来源读取的原始 Git 标签.</param>
    /// <param name="version">用于比较的三段数字版本.</param>
    /// <param name="revision">正整数预发布编号, 正式版为 0.</param>
    /// <returns>标签语法和版本范围符合发布规则时返回 true.</returns>
    public static bool TryReleaseTag(string? tag, out Version version, out int revision)
        => TryReleaseTag(tag, out version, out revision, out _);

    /// <summary>解析标签并返回同一数字版本内的预发布阶段顺序; 格式无效时返回 false.</summary>
    /// <param name="tag">完整 Git 发布标签.</param>
    /// <param name="version">用于比较的三段数字版本.</param>
    /// <param name="revision">正整数预发布编号, 正式版为 0.</param>
    /// <param name="stage">按 alpha、beta、rc 顺序递增, 正式标签为 0.</param>
    private static bool TryReleaseTag(string? tag, out Version version, out int revision, out int stage)
    {
        version = new Version(0, 0, 0);
        revision = 0;
        stage = 0;
        if (tag is null || tag.Length > 64 || !tag.StartsWith('v')) return false;
        var value = tag[1..];
        var separator = value.IndexOf('-');
        var numeric = separator < 0 ? value : value[..separator];
        if (!TryVersion(numeric, out version)) return false;
        if (separator >= 0)
        {
            var suffix = value[(separator + 1)..];
            var (prefixLength, suffixStage) = suffix switch
            {
                _ when suffix.StartsWith("alpha", StringComparison.Ordinal) => (5, 1),
                _ when suffix.StartsWith("beta", StringComparison.Ordinal) => (4, 2),
                _ when suffix.StartsWith("rc", StringComparison.Ordinal) => (2, 3),
                _ => (0, 0)
            };
            if (prefixLength == 0) return false;
            var number = suffix[prefixLength..];
            if (number.Length == 0 || number[0] == '0' || number.Any(character => character is < '0' or > '9')
                || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision == 0) return false;
            stage = suffixStage;
        }
        return true;
    }

    /// <summary>选择全部公开发布, 按数字版本、正式优先、预发布阶段和编号排序; 超量明确失败.</summary>
    public static ProductUpdateCheck Select(string source, string currentVersion, IEnumerable<ProductRelease> releases, DateTimeOffset checkedAt)
    {
        ValidateSource(source);
        if (!TryReleaseTag(currentVersion, out var current, out _)) throw new AppException("INVALID_UPDATE_VERSION", "当前产品发布标签无效");
        var candidates = releases.Where(release => !release.Draft)
            .Select(release =>
            {
                var valid = TryReleaseTag(release.Tag, out var version, out var revision, out var stage);
                return (Release: release, Valid: valid, Version: version, Revision: revision, Stage: stage, Preview: release.Prerelease || revision > 0);
            })
            .Where(item => item.Valid)
            .OrderByDescending(item => item.Version).ThenBy(item => item.Preview)
            .ThenByDescending(item => item.Stage)
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
            return new ProductUpdateRelease(item.Release.Tag, item.Release.Tag, item.Preview, text[..length], length < text.Length);
        }).ToArray();
        var status = candidates.Length == 0 ? "noRelease" : candidates[0].Version > current ? "available" : "current";
        return new(source, currentVersion, status, checkedAt, entries.FirstOrDefault()?.Version, entries);
    }
}
