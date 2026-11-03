// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Model\ModelConfigurationUdfLintHistory.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 记录 lint 明确删除的安装历史, 绑定原锁摘要以避免后续导入复活或错误迁移已删除函数.

using System.Text;
using System.Text.Json;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;

namespace PbiUtils.Infrastructure.Udf.Model;

internal static partial class ModelConfigurationUdfLocks
{
    private const string LintRemovalAnnotation = "PbiUtils.Configuration.UdfLintRemoved.v1";
    private sealed record LintRemoval(string LockDigest, string[] Names);

    /// <summary>只有明确 lint 删除时记录准确旧锁摘要和名称; 既有锁与许可文件保持原样, 不把外部缺失自动追认为删除授权.</summary>
    internal static void MarkLintRemoved(TOM.Model model, IReadOnlyList<string> names)
    {
        var installed = FromModel(model);
        var managed = names.Where(name => installed.Functions.Any(item => item.Function.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (managed.Length == 0) return;
        var digest = ModelConfigurationOwnership.Hash(Encoding.UTF8.GetBytes(Serialize(installed)));
        var previous = ReadLintRemoval(model);
        if (previous is not null && previous.LockDigest != digest) throw InvalidLintHistory();
        var value = new LintRemoval(digest, [.. (previous?.Names ?? []).Union(managed, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]);
        if (model.Annotations.Find(LintRemovalAnnotation) is { } old) model.Annotations.Remove(old);
        model.Annotations.Add(new TOM.Annotation { Name = LintRemovalAnnotation, Value = JsonSerializer.Serialize(value, JsonSerializerOptions.Web) });
    }

    /// <summary>仅对相同完整旧锁排除已明确删除且仍缺失的函数; 其余缺失和修改仍由 VerifyInstalled 拒绝.</summary>
    internal static UdfResolution ExcludeLintRemoved(TOM.Model model, UdfResolution installed)
    {
        var history = ReadLintRemoval(model);
        if (history is null) return installed;
        if (history.LockDigest != ModelConfigurationOwnership.Hash(Encoding.UTF8.GetBytes(Serialize(installed)))) throw InvalidLintHistory();
        var removed = history.Names.Where(name => model.Functions.Find(name) is null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var functions = installed.Functions.Where(item => !removed.Contains(item.Function.Name)).ToArray();
        var packages = RetainedPackages(installed.Packages, functions);
        return new(functions, packages.Select(package => package with
        {
            SelectedFunctions = [.. package.SelectedFunctions.Where(name => !removed.Contains(name))],
            DependencyFunctions = [.. package.DependencyFunctions.Where(name => !removed.Contains(name))]
        }).ToArray());
    }

    /// <summary>只保留仍有函数的包及当前锁明确声明的依赖闭包, 防止已清空的包继续占用版本锁.</summary>
    private static IEnumerable<UdfPackageLock> RetainedPackages(IReadOnlyList<UdfPackageLock> packages, IReadOnlyList<ResolvedUdfFunction> functions)
    {
        var indexed = packages.ToDictionary(package => package.Source + "|" + package.Id, StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(functions.Where(function => function.Source != "local").Select(function => function.Source + "|" + function.PackageId));
        while (pending.TryPop(out var key))
        {
            if (!retained.Add(key) || !indexed.TryGetValue(key, out var package)) continue;
            foreach (var dependency in package.Dependencies) pending.Push(package.Source + "|" + dependency.Id);
        }
        return packages.Where(package => retained.Contains(package.Source + "|" + package.Id));
    }

    /// <summary>删除历史只接受严格 schema 和有界唯一名称, 损坏或未知版本不降级为自动修锁.</summary>
    private static LintRemoval? ReadLintRemoval(TOM.Model model)
    {
        if (model.Annotations.Find(LintRemovalAnnotation) is not { } annotation) return null;
        try
        {
            using var document = JsonDocument.Parse(annotation.Value);
            var keys = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
            var value = JsonSerializer.Deserialize<LintRemoval>(annotation.Value, JsonSerializerOptions.Web);
            if (keys.Length != 2 || !keys.Contains("lockDigest") || !keys.Contains("names") || value is null
                || value.LockDigest is not { Length: 64 } || value.LockDigest.Any(c => !char.IsAsciiHexDigitLower(c))
                || value.Names is not { Length: > 0 and <= 8192 } || value.Names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 200)
                || value.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Names.Length) throw InvalidLintHistory();
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw InvalidLintHistory(); }
    }
    private static AppException InvalidLintHistory() => new("CONFIGURATION_UDF_LOCK_INVALID", "UDF 清理历史与安装锁不一致, 原锁和许可保持", 3);
}
