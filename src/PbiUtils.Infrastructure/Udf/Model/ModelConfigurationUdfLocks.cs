// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Model\ModelConfigurationUdfLocks.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 合并精确 UDF 安装锁、依赖与原始许可, 统一保存在完整模型注解.

using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

using PbiUtils.Application.Input;

namespace PbiUtils.Infrastructure.Udf.Model;

internal static partial class ModelConfigurationUdfLocks
{
    internal const string Annotation = "PbiUtils.Configuration.UdfLock.v1";
    private sealed record LockDocument(int SchemaVersion, IReadOnlyList<ResolvedUdfFunction> Functions, IReadOnlyList<UdfPackageLock> Packages);

    /// <summary>读取当前安装锁并要求显式依赖数组; 可选目录摘要存在时必须为完整指纹.</summary>
    internal static UdfResolution Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (keys.Length != 3 || keys.Distinct(StringComparer.Ordinal).Count() != 3 || keys.Any(key => key is not ("schemaVersion" or "functions" or "packages"))) throw new JsonException();
            var value = JsonSerializer.Deserialize<LockDocument>(json, JsonSerializerOptions.Web)!;
            if (value is null || value.SchemaVersion != 1 || value.Functions is null || value.Packages is null || value.Functions.Count > 8192 || value.Packages.Count > 128
                || value.Functions.Any(item => item is null || item.Function is null || item.Function.Annotations is null)
                || value.Packages.Any(item => item is null || item.LicenseFiles is null || item.SelectedFunctions is null || item.DependencyFunctions is null
                    || item.Dependencies is null || item.Dependencies.Any(dependency => dependency is null)
                    || item.CatalogBaseline is { } baseline && (baseline.Length != 64 || baseline.Any(character => !char.IsAsciiHexDigitLower(character))))) throw new JsonException();
            return new(value.Functions, value.Packages);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new AppException("CONFIGURATION_UDF_LOCK_INVALID", "既有 UDF 锁无效或版本未知, 原锁和许可保持", 3); }
    }

    /// <summary>合并已核验安装事实与本次精确闭包; 本地逻辑名换 tag 可更换内部 GUID, 不接管其他来源或未重解析的本地依赖.</summary>
    internal static UdfResolution Merge(UdfResolution previous, UdfResolution next)
    {
        var functions = previous.Functions.ToDictionary(item => item.Function.Name, StringComparer.OrdinalIgnoreCase);
        var localUpgrades = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var function in next.Functions)
        {
            if (functions.TryGetValue(function.Function.Name, out var previousFunction))
            {
                if ((previousFunction.Source != function.Source || !previousFunction.PackageId.Equals(function.PackageId, StringComparison.OrdinalIgnoreCase))
                    && !IsLocalTagChange(previousFunction, function))
                    throw new AppException("UDF_NAME_CONFLICT", "同名函数属于不同来源或函数库, 本次不迁移已有函数身份", 3);
                if (previousFunction.Source == "local" && function.Source == "local"
                    && (previousFunction.Version != function.Version || previousFunction.Baseline != function.Baseline))
                    localUpgrades.Add(function.Function.Name);
            }
            functions[function.Function.Name] = function;
        }
        // 既有锁证明已安装的精确内容; 历史目录代不约束新方案中未再次选择的库, 本轮选择会替换为新的核验基线.
        var packages = previous.Packages.ToDictionary(item => item.Source + "|" + item.Id, item => item with { CatalogBaseline = null }, StringComparer.OrdinalIgnoreCase);
        var upgrades = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in next.Packages)
        {
            var key = package.Source + "|" + package.Id;
            if (packages.TryGetValue(key, out var old))
            {
                if (old.SourceBaseline != package.SourceBaseline || old.Version == package.Version && old.Hash != package.Hash)
                    throw new AppException("UDF_VERSION_CONFLICT", "同版本函数库内容或来源配置已改变, 未覆盖已安装锁", 3);
                if (old.Version != package.Version)
                {
                    var complete = previous.Functions.Where(function => function.Source == package.Source && function.PackageId.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                        .All(function => next.Functions.Any(replacement => replacement.Source == package.Source && replacement.PackageId.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                            && replacement.Version == package.Version && replacement.Function.Name.Equals(function.Function.Name, StringComparison.OrdinalIgnoreCase)));
                    if (!complete) throw new AppException("UDF_VERSION_CONFLICT", "切换版本必须覆盖该库全部已安装函数, 未保留混合版本", 3);
                    upgrades.Add(key);
                }
                packages[key] = package with
                {
                    SelectedFunctions = [.. old.SelectedFunctions.Union(package.SelectedFunctions, StringComparer.OrdinalIgnoreCase)],
                    DependencyFunctions = [.. old.DependencyFunctions.Union(package.DependencyFunctions, StringComparer.OrdinalIgnoreCase).Except(old.SelectedFunctions.Union(package.SelectedFunctions, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)]
                };
            }
            else packages.Add(key, package);
        }
        // 本地精确声明不在安装锁中, 且可能没有词法引用; 远端/本地切 tag 或本地定义基线变化均须重新证明存活本地闭包.
        if ((upgrades.Count > 0 || localUpgrades.Count > 0) && previous.Functions.Any(function => function.Source == "local"
            && !next.Functions.Any(replacement => replacement.Source == "local"
                && replacement.Function.Name.Equals(function.Function.Name, StringComparison.OrdinalIgnoreCase)
                && (replacement.PackageId.Equals(function.PackageId, StringComparison.OrdinalIgnoreCase) || IsLocalTagChange(function, replacement)))))
            throw new AppException("UDF_VERSION_CONFLICT", "切换函数库版本前请在同批重新选择全部已安装本地函数以核实其精确依赖", 3);
        // 每个当前锁均携带显式声明依赖, 版本切换必须满足全部已安装库的约束.
        foreach (var pair in packages)
        {
            foreach (var dependency in pair.Value.Dependencies)
                if (!packages.TryGetValue(pair.Value.Source + "|" + dependency.Id, out var installed) || installed.Version != dependency.Version)
                    throw new AppException("UDF_VERSION_CONFLICT", "其他已安装函数库仍要求旧依赖版本, 本次不切换", 3);
        }
        return new(functions.Values.ToArray(), packages.Values.ToArray());
    }

    /// <summary>仅同逻辑名称的本地精确 tag 变更可替换版本 GUID; 两轮仍须携带真实存储身份和完整基线.</summary>
    private static bool IsLocalTagChange(ResolvedUdfFunction previous, ResolvedUdfFunction next) =>
        previous.Source == "local" && next.Source == "local" && previous.Version != next.Version
        && previous.Function.Name.Equals(next.Function.Name, StringComparison.OrdinalIgnoreCase)
        && Guid.TryParseExact(previous.PackageId, "N", out _) && Guid.TryParseExact(next.PackageId, "N", out _)
        && InputValidation.IsDigest(previous.Baseline)
        && InputValidation.IsDigest(next.Baseline);

    /// <summary>仅从完整模型读取安装事实, 不读取或恢复外部锁文件.</summary>
    /// <param name="model">宿主核验过的完整原模型或私有候选.</param>
    /// <returns>包含精确函数、版本、依赖与原始许可的安装锁; 未安装时为空.</returns>
    /// <exception cref="AppException">模型安装锁损坏或版本未知.</exception>
    internal static UdfResolution FromModel(TOM.Model model) => model.Annotations.Find(Annotation) is { } value ? Read(value.Value) : new([], []);

    /// <summary>安装锁只证明已安装内容, 不授予恢复已删除、改名或人工改写函数的权限.</summary>
    internal static void VerifyInstalled(TOM.Model model, UdfResolution previous)
    {
        foreach (var installed in previous.Functions)
        {
            var function = model.Functions.Find(installed.Function.Name)
                ?? throw new AppException("CONFIGURATION_OBJECT_CONFLICT", "已安装函数已被删除或改名, 请先处理已有锁与模型差异", 3);
            ModelConfigurationOwnership.Require(function, function.Annotations);
        }
    }

    /// <summary>将完整安装锁和逐包原始许可与函数一起写入调用方候选, 成功后清除旧 lint 删除标记.</summary>
    /// <param name="model">本轮独占的完整模型候选, 不直接访问权威模型.</param>
    /// <param name="resolution">已核验来源、内容和依赖的精确安装集合.</param>
    internal static void PersistModel(TOM.Model model, UdfResolution resolution)
    {
        if (model.Annotations.Find(LintRemovalAnnotation) is { } removal) model.Annotations.Remove(removal);
        if (model.Annotations.Find(Annotation) is { } old) model.Annotations.Remove(old);
        model.Annotations.Add(new TOM.Annotation { Name = Annotation, Value = Serialize(resolution) });
    }

    internal static string Serialize(UdfResolution resolution) => JsonSerializer.Serialize(new LockDocument(1, resolution.Functions, resolution.Packages), JsonSerializerOptions.Web);
}
