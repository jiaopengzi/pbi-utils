// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfDependencyResolver.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 冻结精确函数版本并计算可证明依赖闭包, 对未知引用失败关闭.

using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Application.Udf;

/// <summary>仅解析定义, 不安装函数、不执行 DAX, 不把 TOM 解析当作语义证明.</summary>
public sealed class UdfDependencyResolver(IUdfPackageProvider packages, ILocalUdfStore local, IUdfSourceProvider? sources = null)
{
    /// <summary>准备阶段解析最新稳定版与依赖; 返回可直接纳入计划的冻结内容.</summary>
    public async Task<UdfResolution> ResolveAsync(IReadOnlyList<UdfSelection> selections, CancellationToken cancellationToken)
    {
        using var context = new ResolutionContext(packages, local, sources, cancellationToken);
        return await context.ResolveAsync(selections).ConfigureAwait(false);
    }

    /// <summary>仅复核已冻结精确版本与全部来源, 不查询 latest 也不重算依赖选择.</summary>
    public async Task VerifyFrozenAsync(UdfResolution resolution, CancellationToken token)
    {
        foreach (var item in resolution.Functions.Where(item => item.Source == "local"))
        {
            var observed = await local.GetAsync(item.PackageId, token).ConfigureAwait(false);
            if (observed.Baseline != item.Baseline || observed.Definition.Version != item.Version)
                throw new AppException("UDF_BASELINE_CHANGED", "本地函数在确认期间已变化", 3);
        }
        using var readers = new SnapshotReaders();
        var pinned = new Dictionary<string, IUdfPackageProvider>(StringComparer.Ordinal);
        var liveCatalogs = new Dictionary<string, IUdfCatalogProvider>(StringComparer.Ordinal);
        var sourceBaselines = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in resolution.Packages)
        {
            if (sourceBaselines.TryGetValue(item.Source, out var bound) && bound != item.SourceBaseline)
                throw new AppException("UDF_SOURCE_CHANGED", "冻结函数包含不一致的来源配置基线", 3);
            sourceBaselines[item.Source] = item.SourceBaseline;
            if (!pinned.TryGetValue(item.Source, out var provider))
            {
                provider = item.Source == "daxlib" ? packages : await (sources ?? throw new AppException("UDF_SOURCE_INVALID", "函数来源不可用", 3)).GetProviderAsync(item.Source, item.SourceBaseline, token).ConfigureAwait(false);
                if (provider is IUdfCatalogProvider live) liveCatalogs.Add(item.Source, live);
                if (provider is IUdfCatalogSnapshotSource snapshotSource) provider = readers.Own(await snapshotSource.OpenSnapshotAsync(token).ConfigureAwait(false));
                pinned.Add(item.Source, provider);
            }
            var status = provider is IUdfCatalogSnapshotProvider snapshot ? snapshot.Status
                : provider is IUdfCatalogProvider catalog ? await catalog.GetStatusAsync(token).ConfigureAwait(false) : null;
            if (item.CatalogBaseline is not null && status?.Baseline != item.CatalogBaseline)
                throw new AppException("UDF_CATALOG_CHANGED", "已冻结的函数选择目录发生变化, 请重新预览", 3);
            var observed = await provider.GetAsync(item.Id, item.Version, token).ConfigureAwait(false);
            if (observed.Id != item.Id || observed.Version != item.Version || observed.Hash != item.Hash
                || observed.LicenseFiles.Count != item.LicenseFiles.Count || item.LicenseFiles.Any(pair => !observed.LicenseFiles.TryGetValue(pair.Key, out var value) || value != pair.Value)
                || item.Dependencies is { } declared && (observed.Dependencies.Count != declared.Length || declared.Any(dependency => !observed.Dependencies.Contains(dependency))))
                throw new AppException("UDF_PACKAGE_CHANGED", "精确函数包或许可在确认期间已变化", 3);
        }
        foreach (var item in resolution.Packages.Where(item => item.CatalogBaseline is not null))
            if (liveCatalogs.TryGetValue(item.Source, out var live) && (await live.GetStatusAsync(token).ConfigureAwait(false)).Baseline != item.CatalogBaseline)
                throw new AppException("UDF_CATALOG_CHANGED", "已冻结的函数选择目录在核验期间变化, 请重新预览", 3);
    }

    /// <summary>只拥有本次新打开的固定读者, 不释放外部注入的共享提供者.</summary>
    private sealed class SnapshotReaders : IDisposable
    {
        private readonly List<IUdfCatalogSnapshotProvider> owned = [];
        /// <summary>登记已成功打开的读者并原样返回, 后续失败也会释放该读者.</summary>
        internal IUdfCatalogSnapshotProvider Own(IUdfCatalogSnapshotProvider reader) { owned.Add(reader); return reader; }
        /// <summary>同步关闭读者句柄, 允许后续成功刷新回收退役目录.</summary>
        public void Dispose() { foreach (var reader in owned) reader.Dispose(); }
    }

    private sealed class ResolutionContext(IUdfPackageProvider packages, ILocalUdfStore local, IUdfSourceProvider? sources, CancellationToken cancellationToken) : IDisposable
    {
        private readonly SnapshotReaders readers = new();
        /// <summary>解析成功、失败及取消后都释放本次固定的全部来源读者.</summary>
        public void Dispose() => readers.Dispose();
        private readonly Dictionary<string, UdfPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _sourceBaselines = new(StringComparer.Ordinal);
        private readonly HashSet<string> _witnessBoundSources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IUdfCatalogProvider> _catalogs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _catalogBaselines = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IUdfPackageProvider> _providers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LocalUdfEntry> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Node> _functions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _explicit = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _visiting = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ResolvedUdfFunction> _resolved = new(StringComparer.OrdinalIgnoreCase);

        internal async Task<UdfResolution> ResolveAsync(IReadOnlyList<UdfSelection> selections)
        {
            if (selections is null || selections.Count > 128 || selections.Any(s => s.Functions is not { Length: > 0 and <= 128 })) throw Failure("UDF_SELECTION_INVALID", "函数选择为空或超出限制");
            var selectedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var latestVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selection.Source != "local" && UdfValidation.IsSourceId(selection.Source))
                {
                    var provider = await ProviderAsync(selection.Source, selection.SourceBaseline).ConfigureAwait(false);
                    // 版本由每个库的明确 tag 决定, 不因其他远端或本地选择增多而静默升级.
                    // latest 必须携带目录基线; 明确 tag 若携带浏览见证也须核验并冻结.
                    var resolvesLatest = string.IsNullOrEmpty(selection.Version) || selection.Version == "latest";
                    if (resolvesLatest || selection.CatalogBaseline is not null) _witnessBoundSources.Add(selection.Source);
                    if ((resolvesLatest || selection.CatalogBaseline is not null) && _catalogs.ContainsKey(selection.Source))
                    {
                        var status = provider is IUdfCatalogSnapshotProvider snapshot ? snapshot.Status
                            : await _catalogs[selection.Source].GetStatusAsync(cancellationToken).ConfigureAwait(false);
                        if (selection.CatalogBaseline is null || selection.CatalogBaseline != status.Baseline)
                            throw Failure("UDF_CATALOG_CHANGED", "函数目录已更新或尚未核验, 请重新核验所选函数");
                    }
                    var selectionKey = Key(selection.Source, selection.PackageId);
                    string version;
                    if (resolvesLatest)
                    {
                        if (!latestVersions.TryGetValue(selectionKey, out version!))
                        {
                            var versions = await provider.GetVersionsAsync(selection.PackageId, cancellationToken).ConfigureAwait(false);
                            // 仅有预发行版本时也必须由用户指定精确版本, latest 不自动取 beta.
                            version = UdfValidation.LatestStable(versions);
                            latestVersions.Add(selectionKey, version);
                        }
                    }
                    else version = selection.Version ?? throw Failure("UDF_SELECTION_INVALID", "精确函数版本不能为空");
                    if (selectedVersions.TryGetValue(selectionKey, out var selectedVersion) && selectedVersion != version)
                        throw Failure("UDF_VERSION_CONFLICT", "同一函数包存在不兼容的精确版本要求");
                    selectedVersions[selectionKey] = version;
                    await LoadPackageAsync(selection.Source, selection.PackageId, version, selection.SourceBaseline).ConfigureAwait(false);
                    if (selection.Baseline is not null && _packages[selectionKey].Hash != selection.Baseline)
                        throw Failure("UDF_PACKAGE_CHANGED", "所选精确函数包内容已变化, 请重新核验");
                    foreach (var name in selection.Functions)
                    {
                        if (!_functions.TryGetValue(name, out var node) || node.Source != selection.Source || !node.Id.Equals(selection.PackageId, StringComparison.OrdinalIgnoreCase)) throw Failure("UDF_FUNCTION_MISSING", "精确包中不存在所选函数");
                        _explicit.Add(name);
                    }
                }
                else if (selection.Source == "local")
                {
                    var entry = await LoadLocalAsync(selection.PackageId, selection.Baseline).ConfigureAwait(false);
                    if (selection.Version is not null && selection.Version != entry.Definition.Version || selection.Functions.Length != 1 || selection.Functions[0] != entry.Definition.Name)
                        throw Failure("UDF_BASELINE_CHANGED", "本地函数版本或名称与选择不一致");
                    _explicit.Add(entry.Definition.Name);
                }
                else throw Failure("UDF_SOURCE_INVALID", "函数来源无效");
            }
            foreach (var name in _explicit) Visit(name);
            var locks = _packages.Select(pair =>
            {
                var package = pair.Value; var source = pair.Key[..pair.Key.LastIndexOf('|')];
                var included = _resolved.Values.Where(f => f.Source == source && f.PackageId.Equals(package.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
                return new UdfPackageLock(package.Id, package.Version, package.Hash, included.Length != package.Functions.Count,
                    [.. included.Where(f => !f.IsDependency).Select(f => f.Function.Name)], [.. included.Where(f => f.IsDependency).Select(f => f.Function.Name)], package.LicenseFiles, [.. package.Dependencies], source, _sourceBaselines.GetValueOrDefault(source), _witnessBoundSources.Contains(source) ? _catalogBaselines.GetValueOrDefault(source) : null);
            }).ToArray();
            foreach (var (source, baseline) in _catalogBaselines)
            {
                var current = await _catalogs[source].GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (current.Baseline != baseline) throw Failure("UDF_CATALOG_CHANGED", "函数目录在解析期间已更新, 请重新核验所选函数");
            }
            return new(_resolved.Values.ToArray(), locks);
        }

        /// <summary>来源配置只接受同一快照, 每个声明依赖都留在其父库来源.</summary>
        private async Task<IUdfPackageProvider> ProviderAsync(string source, string? baseline)
        {
            if (_providers.TryGetValue(source, out var existing))
            {
                if (_sourceBaselines[source] != baseline) throw Failure("UDF_SOURCE_CHANGED", "同一函数来源存在不一致的配置基线");
                return existing;
            }
            var provider = source == "daxlib" ? packages : await (sources ?? throw Failure("UDF_SOURCE_INVALID", "未配置用户函数来源"))
                .GetProviderAsync(source, baseline, cancellationToken).ConfigureAwait(false);
            if (provider is IUdfCatalogProvider catalog)
            {
                _catalogs.Add(source, catalog);
                if (provider is IUdfCatalogSnapshotSource snapshotSource)
                    provider = readers.Own(await snapshotSource.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false));
                _catalogBaselines.Add(source, provider is IUdfCatalogSnapshotProvider snapshot ? snapshot.Status.Baseline
                    : (await catalog.GetStatusAsync(cancellationToken).ConfigureAwait(false)).Baseline);
            }
            _providers.Add(source, provider); _sourceBaselines.Add(source, baseline);
            return provider;
        }
        /// <summary>仅从指定来源读取精确版本并自动纳入其固定依赖, 预发行不另设许可; 缓存和冲突检查仍绑定来源及库.</summary>
        private async Task LoadPackageAsync(string source, string id, string version, string? sourceBaseline)
        {
            cancellationToken.ThrowIfCancellationRequested(); UdfValidation.PackageIdentity(id, version);
            var key = Key(source, id);
            if (_loading.Contains(key)) throw Failure("UDF_DEPENDENCY_CYCLE", "函数包依赖存在循环");
            if (_packages.TryGetValue(key, out var existing))
            {
                if (existing.Version != version) throw Failure("UDF_VERSION_CONFLICT", "同一函数包存在不兼容的精确版本要求");
                return;
            }
            if (_packages.Count >= 128) throw Failure("UDF_DEPENDENCY_LIMIT", "函数包依赖数量超出限制");
            _loading.Add(key);
            var package = await (await ProviderAsync(source, sourceBaseline).ConfigureAwait(false)).GetAsync(id, version, cancellationToken).ConfigureAwait(false);
            if (!package.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || package.Version != version) throw Failure("UDF_PACKAGE_INVALID", "函数提供者返回身份不一致");
            _packages.Add(key, package);
            foreach (var function in package.Functions) Add(new(function, source, id, version, package.Hash));
            foreach (var dependency in package.Dependencies) await LoadPackageAsync(source, dependency.Id, dependency.Version, sourceBaseline).ConfigureAwait(false);
            _loading.Remove(key);
        }
        /// <summary>读取绑定基线的本地定义并自动解析全部精确依赖, 基线变化、循环及错误依赖仍失败关闭.</summary>
        private async Task<LocalUdfEntry> LoadLocalAsync(string id, string? baseline)
        {
            if (string.IsNullOrEmpty(baseline)) throw Failure("UDF_BASELINE_REQUIRED", "本地函数选择必须绑定已读取基线");
            var key = "local:" + id;
            if (_loading.Contains(key)) throw Failure("UDF_DEPENDENCY_CYCLE", "本地函数依赖存在循环");
            if (_locals.TryGetValue(id, out var old))
            {
                if (old.Baseline != baseline) throw Failure("UDF_BASELINE_CHANGED", "本地函数依赖基线冲突");
                return old;
            }
            if (_locals.Count >= 128) throw Failure("UDF_DEPENDENCY_LIMIT", "本地函数依赖数量超出限制");
            _loading.Add(key);
            var entry = await local.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (entry.Baseline != baseline) throw Failure("UDF_BASELINE_CHANGED", "本地函数已改变, 请重新预览");
            UdfValidation.LocalDefinition(entry.Definition);
            _locals.Add(id, entry);
            Add(new(new(entry.Definition.Name, entry.Definition.Expression, entry.Definition.Description, new Dictionary<string, string>()), "local", id, entry.Definition.Version, entry.Baseline));
            foreach (var dependency in entry.Definition.Dependencies)
            {
                if (dependency.Source != "local") await LoadPackageAsync(dependency.Source, dependency.PackageId, dependency.Version, dependency.SourceBaseline).ConfigureAwait(false);
                else
                {
                    var nested = await LoadLocalAsync(dependency.PackageId, dependency.Baseline).ConfigureAwait(false);
                    if (nested.Definition.Version != dependency.Version || nested.Definition.Name != dependency.Function) throw Failure("UDF_BASELINE_CHANGED", "本地依赖名称或版本不一致");
                }
                if (!_functions.TryGetValue(dependency.Function, out var declared) || declared.Source != dependency.Source
                    || !declared.Id.Equals(dependency.PackageId, StringComparison.OrdinalIgnoreCase) || declared.Version != dependency.Version
                    || dependency.Source == "local" && declared.Baseline != dependency.Baseline
                    || dependency.Source != "local" && _sourceBaselines.GetValueOrDefault(dependency.Source) != dependency.SourceBaseline)
                    throw Failure("UDF_DEPENDENCY_UNKNOWN", "声明依赖函数在指定来源、函数库或精确版本中不存在");
            }
            _loading.Remove(key); return entry;
        }
        private void Add(Node node)
        {
            if (_functions.Count >= 8192 || !_functions.TryAdd(node.Function.Name, node)) throw Failure("UDF_NAME_CONFLICT", "函数名称冲突或数量超出限制");
        }
        private void Visit(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_resolved.ContainsKey(name)) return;
            if (_visiting.Count >= 128 || !_visiting.Add(name)) throw Failure("UDF_DEPENDENCY_CYCLE", "函数依赖存在循环或过深");
            var node = _functions[name];
            foreach (var reference in UdfReferenceScanner.References(node.Function.Expression))
            {
                if (!_functions.TryGetValue(reference, out var dependency) || !Allowed(node, dependency)) throw Failure("UDF_DEPENDENCY_UNKNOWN", "函数存在无法确认的引用: " + reference);
                Visit(dependency.Function.Name);
            }
            // 显式本地声明属于用户定义的一部分, 即使词法未出现也纳入闭包.
            if (node.Source == "local") foreach (var declaration in _locals[node.Id].Definition.Dependencies) Visit(declaration.Function);
            _visiting.Remove(name);
            _resolved.Add(name, new(node.Function, node.Source, node.Id, node.Version, node.Baseline, !_explicit.Contains(name), _sourceBaselines.GetValueOrDefault(node.Source)));
        }
        private bool Allowed(Node from, Node to)
        {
            if (from.Source == to.Source && from.Id.Equals(to.Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (from.Source != "local") return to.Source == from.Source && _packages[Key(from.Source, from.Id)].Dependencies.Any(d => d.Id.Equals(to.Id, StringComparison.OrdinalIgnoreCase) && d.Version == to.Version);
            return _locals[from.Id].Definition.Dependencies.Any(d => d.Source == to.Source && d.PackageId.Equals(to.Id, StringComparison.OrdinalIgnoreCase) && d.Function.Equals(to.Function.Name, StringComparison.OrdinalIgnoreCase) && d.Version == to.Version);
        }
        private sealed record Node(UdfFunction Function, string Source, string Id, string Version, string Baseline);
        /// <summary>来源 ID 和库 ID 均不接受分隔符, 因此组合键无歧义.</summary>
        private static string Key(string source, string id) => source + "|" + id;
    }
    private static AppException Failure(string code, string message) => new(code, message);
}

/// <summary>保守词法扫描: 明确内置白名单以外的调用都必须由依赖定义证明.</summary>
internal static class UdfReferenceScanner
{
    /// <summary>读取表达式中的静态 UDF 引用; 标准 DAX 内置按完整名称排除, 未闭合词元或动态调用明确失败.</summary>
    /// <param name="expression">待核验的完整函数定义, 不执行 DAX.</param>
    /// <returns>不区分大小写的唯一引用名称; 字符串、列引用和注释不产生依赖.</returns>
    internal static IReadOnlyList<string> References(string expression)
    {
        if (expression.Length > 1024 * 1024) throw new AppException("UDF_EXPRESSION_LIMIT", "函数表达式过长");
        var tokens = new List<(string Text, bool Word)>();
        for (var i = 0; i < expression.Length;)
        {
            var c = expression[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (i + 1 < expression.Length && (expression.AsSpan(i, 2).SequenceEqual("//") || expression.AsSpan(i, 2).SequenceEqual("--")))
            { while (i < expression.Length && expression[i] != '\n') i++; continue; }
            if (i + 1 < expression.Length && expression.AsSpan(i, 2).SequenceEqual("/*"))
            { var end = expression.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) throw Invalid(); i = end + 2; continue; }
            if (c is '"' or '\'' or '[')
            {
                var closing = c == '[' ? ']' : c; var text = new StringBuilder(); i++; var closed = false;
                while (i < expression.Length)
                {
                    if (expression[i] == closing)
                    {
                        if (i + 1 < expression.Length && expression[i + 1] == closing) { text.Append(closing); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    text.Append(expression[i++]);
                }
                if (!closed) throw Invalid();
                tokens.Add(c == '\'' ? (text.ToString(), true) : ("literal", false)); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] is '_' or '.')) i++;
                tokens.Add((expression[start..i], true)); continue;
            }
            tokens.Add((c.ToString(), false)); i++;
        }
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Count; i++)
        {
            var (Text, Word) = tokens[i];
            if (Text == "(" && i > 0 && tokens[i - 1].Text is ")" or "literal")
                throw new AppException("UDF_DEPENDENCY_UNSUPPORTED", "函数包含无法静态确认的动态调用");
            if (!Word) continue;
            var call = i + 1 < tokens.Count && tokens[i + 1].Text == "(";
            if ((call || Text.Contains('.')) && !UdfDaxBuiltins.Contains(Text) && !Text.Equals("RETURN", StringComparison.OrdinalIgnoreCase)) references.Add(Text);
        }
        return references.ToArray();
    }
    private static AppException Invalid() => new("UDF_DEPENDENCY_UNSUPPORTED", "函数包含未闭合字符串、引用或注释, 无法确认依赖");
}
