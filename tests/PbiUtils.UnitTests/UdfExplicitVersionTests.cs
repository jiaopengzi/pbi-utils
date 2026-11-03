// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfExplicitVersionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证批量选择仍保留明确历史版本, 内容基线和同库依赖冲突不能被最新版本替代.

using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅解析私有函数定义, 不写模型或真实用户数据.</summary>
public sealed class UdfExplicitVersionTests
{
    /// <summary>同库选多个函数时仍使用用户明确指定的历史版本.</summary>
    [Fact]
    public async Task MultipleFunctionsKeepExplicitHistoricalVersion()
    {
        var provider = new Packages();
        var result = await new UdfDependencyResolver(provider, new Local()).ResolveAsync(
            [new("daxlib", "Example.One", "1.0.0", ["Example.One.First", "Example.One.Second"], Baseline: Packages.Hash('a'))], default);

        Assert.Equal("1.0.0", Assert.Single(result.Packages).Version);
        Assert.All(result.Functions, item => Assert.Equal("1.0.0", item.Version));
        Assert.DoesNotContain(provider.Reads, item => item == "Example.One@2.0.0");
    }

    /// <summary>加入独立本地函数不会把远端的明确历史 tag 改为最新版本.</summary>
    [Fact]
    public async Task LocalSelectionDoesNotUpgradeExplicitRemoteVersion()
    {
        var result = await new UdfDependencyResolver(new Packages(), new Local()).ResolveAsync(
            [new("daxlib", "Example.One", "1.0.0", ["Example.One.First"], Baseline: Packages.Hash('a')),
             new("local", "custom", "7.0.0", ["Local.Custom"], Baseline: Packages.Hash('c'))], default);

        Assert.Equal("1.0.0", Assert.Single(result.Packages).Version);
        Assert.Contains(result.Functions, item => item.Source == "local" && item.Version == "7.0.0");
    }

    /// <summary>即使同时选择多个函数, 错误精确包 hash 仍须在准备阶段拒绝.</summary>
    [Fact]
    public async Task MultipleFunctionsVerifyExplicitContentHash()
    {
        var error = await Assert.ThrowsAsync<AppException>(() => new UdfDependencyResolver(new Packages(), new Local()).ResolveAsync(
            [new("daxlib", "Example.One", "1.0.0", ["Example.One.First", "Example.One.Second"], Baseline: Packages.Hash('f'))], default));

        Assert.Equal("UDF_PACKAGE_CHANGED", error.Code);
    }

    /// <summary>同源同库的两条明确版本要求不一致时不能复用第一条或静默升版.</summary>
    [Fact]
    public async Task DifferentExplicitTagsForSamePackageConflict()
    {
        var error = await Assert.ThrowsAsync<AppException>(() => new UdfDependencyResolver(new Packages(), new Local()).ResolveAsync(
            [new("daxlib", "Example.One", "1.0.0", ["Example.One.First"], Baseline: Packages.Hash('a')),
             new("daxlib", "Example.One", "2.0.0", ["Example.One.Second"], Baseline: Packages.Hash('b'))], default));

        Assert.Equal("UDF_VERSION_CONFLICT", error.Code);
    }

    /// <summary>同库latest与较旧精确tag无论顺序都须冲突, 不能把第一条版本当作另一条latest.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestAndHistoricalTagForSamePackageConflict(bool latestFirst)
    {
        UdfSelection exact = new("daxlib", "Example.One", "1.0.0", ["Example.One.First"], Baseline: Packages.Hash('a'));
        UdfSelection latest = new("daxlib", "Example.One", null, ["Example.One.Second"]);
        var error = await Assert.ThrowsAsync<AppException>(() => new UdfDependencyResolver(new Packages(), new Local()).ResolveAsync(
            latestFirst ? [latest, exact] : [exact, latest], default));

        Assert.Equal("UDF_VERSION_CONFLICT", error.Code);
    }

    /// <summary>一个库的精确历史 tag 可与另一库默认最新解析并存.</summary>
    [Fact]
    public async Task ExplicitAndLatestSelectionsRemainIndependent()
    {
        var result = await new UdfDependencyResolver(new Packages(), new Local()).ResolveAsync(
            [new("daxlib", "Example.One", "1.0.0", ["Example.One.First"], Baseline: Packages.Hash('a')),
             new("daxlib", "Example.Two", null, ["Example.Two.Only"])], default);

        Assert.Contains(result.Packages, item => item.Id == "Example.One" && item.Version == "1.0.0");
        Assert.Contains(result.Packages, item => item.Id == "Example.Two" && item.Version == "3.0.0");
    }

    /// <summary>可预测的只读包替身只返回声明的精确包, 记录读取版本以发现隐式升级.</summary>
    private sealed class Packages : IUdfPackageProvider
    {
        internal List<string> Reads { get; } = [];

        /// <summary>生成固定合法摘要供内容基线断言使用.</summary>
        internal static string Hash(char character) => new(character, 64);

        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(packageId == "Example.One" ? ["1.0.0", "2.0.0"] : ["1.0.0", "3.0.0"]);

        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(packageId + "@" + version);
            UdfFunction[] functions = packageId == "Example.One"
                ? [new("Example.One.First", "() => 1", "", new Dictionary<string, string>()), new("Example.One.Second", "() => 2", "", new Dictionary<string, string>())]
                : [new("Example.Two.Only", "() => 3", "", new Dictionary<string, string>())];
            return Task.FromResult(new UdfPackage(packageId, version, Hash(version == "1.0.0" ? 'a' : 'b'), "", "", new Dictionary<string, string>(), [], functions, DateTimeOffset.UnixEpoch));
        }
    }

    /// <summary>独立本地函数不声明远端依赖, 其余 CRUD 非本测试行为.</summary>
    private sealed class Local : ILocalUdfStore
    {
        public Task<LocalUdfEntry> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(
            new LocalUdfEntry("custom", Packages.Hash('c'), new(1, "Local.Custom", "7.0.0", "() => 99", "", [])));
        public Task<IReadOnlyList<LocalUdfEntry>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocalUdfEntry> CreateAsync(LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
        public Task<LocalUdfEntry> UpdateAsync(string id, string baseline, LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
        public Task DeleteAsync(string id, string baseline, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
    }
}
