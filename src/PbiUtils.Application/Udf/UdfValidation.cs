// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 函数包及本地定义的共享有界参数校验.

using System.Text;
using System.Text.RegularExpressions;
using PbiUtils.Core;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Udf;

/// <summary>共享安全参数约束, 不接受 URL、路径或版本范围.</summary>
public static partial class UdfValidation
{
    /// <summary>包 ID 仅接受官方常见限定名, 版本必须精确 SemVer.</summary>
    public static void PackageIdentity(string id, string version)
    {
        if (id is null || id.Length > 128 || !Identifier().IsMatch(id) || version is null || version.Length > 100 || !SemVer().IsMatch(version))
            throw new AppException("UDF_IDENTITY_INVALID", "函数包标识或精确版本无效");
    }
    /// <summary>仅接受固定来源或宿主生成的自定义 ID, 不把 URL 或显示名称作为来源权限.</summary>
    public static bool IsSourceId(string? source) => source is "daxlib" or "local" || source is { Length: 39 } && source.StartsWith("source:", StringComparison.Ordinal)
        && Guid.TryParseExact(source.AsSpan(7), "N", out _) && source[7..].All(char.IsAsciiHexDigitLower);
    /// <summary>本地函数不允许超长、空定义或未知存储版本.</summary>
    public static void LocalDefinition(LocalUdfDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        PackageIdentity(definition.Name, definition.Version);
        if (definition.SchemaVersion != 1 || string.IsNullOrWhiteSpace(definition.Expression) || Encoding.UTF8.GetByteCount(definition.Expression) > 32768 ||
            definition.Description is null || definition.Description.Length > 8192 || definition.Dependencies is null || definition.Dependencies.Length > 128)
            throw new AppException("UDF_DEFINITION_INVALID", "本地函数定义格式或大小无效");
        foreach (var dependency in definition.Dependencies)
        {
            if (!IsSourceId(dependency.Source) || dependency.Source.StartsWith("source:", StringComparison.Ordinal) && !InputValidation.IsDigest(dependency.SourceBaseline)
                || string.IsNullOrWhiteSpace(dependency.Function) || dependency.Function.Length > 128 ||
                dependency.Baseline is { } baseline && (baseline.Length != 64 || baseline.Any(c => !char.IsAsciiHexDigitLower(c)))) throw new AppException("UDF_DEPENDENCY_INVALID", "函数依赖声明无效");
            if (dependency.Source == "local" && !Guid.TryParseExact(dependency.PackageId, "N", out _)) throw new AppException("UDF_DEPENDENCY_INVALID", "本地依赖标识无效");
            PackageIdentity(dependency.Source == "local" ? "local" : dependency.PackageId, dependency.Version);
        }
    }
    /// <summary>不将预发行版本默认为稳定版.</summary>
    public static bool IsPrerelease(string version) => version.Split('+')[0].Contains('-');
    /// <summary>按数值 SemVer 选择稳定版本, 不依赖 HTTP 顺序或字符串顺序.</summary>
    public static string LatestStable(IEnumerable<string> versions) => versions.Where(v => !IsPrerelease(v)).OrderByDescending(v => Version.Parse(v.Split('+')[0])).FirstOrDefault()
        ?? throw new AppException("UDF_STABLE_NOT_FOUND", "此函数包尚无稳定版本, 请明确选择预发行版本");
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)] private static partial Regex SemVer();
}
