// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfPrereleaseResolutionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证精确预发行与本地传递依赖自动解析, 同时保持版本冲突及内容基线保护.

using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅在内存中解析受控包定义, 不读取公网、用户数据或模型.</summary>
public sealed class UdfPrereleaseResolutionTests
{
    /// <summary>共享预发行依赖可在任何显式选择顺序复用, 不需要逐选择审批.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPrereleaseDependencyIsIndependentOfSelectionOrder(bool dependencyFirst)
    {
        var dependency = Package("Shared", "1.0.0-beta.2", [], "Shared.One", "() => 2");
        var parent = Package("Parent", "1.0.0", [new("Shared", "1.0.0-beta.2")], "Parent.One", "() => Shared.One()");
        UdfSelection first = new("daxlib", "Parent", "1.0.0", ["Parent.One"]);
        UdfSelection second = new("daxlib", "Shared", "1.0.0-beta.2", ["Shared.One"]);
        var result = await new UdfDependencyResolver(new Packages(parent, dependency), new Local()).ResolveAsync(
            dependencyFirst ? [second, first] : [first, second], default);

        Assert.Equal(2, result.Functions.Count);
        Assert.All(result.Functions, item => Assert.False(item.IsDependency));
        Assert.Contains(result.Packages, item => item.Id == "Shared" && item.Version == "1.0.0-beta.2");
    }

    /// <summary>本地到本地再到预发行包的声明链自动解析且冻结每个精确版本.</summary>
    [Fact]
    public async Task NestedLocalDependenciesAutomaticallyIncludeExactRemotePrerelease()
    {
        var childId = new string('2', 32);
        var child = new LocalUdfEntry(childId, Hash('b'), new(1, "Local.Child", "1.0.0", "() => Remote.One()", "",
            [new("daxlib", "Remote", "2.0.0-rc.1", "Remote.One")]));
        var parent = new LocalUdfEntry(new string('1', 32), Hash('a'), new(1, "Local.Parent", "3.0.0-beta", "() => Local.Child()", "",
            [new("local", childId, "1.0.0", "Local.Child", child.Baseline)]));
        var remote = Package("Remote", "2.0.0-rc.1", [], "Remote.One", "() => 7");

        var result = await new UdfDependencyResolver(new Packages(remote), new Local(parent, child)).ResolveAsync(
            [new("local", parent.Id, parent.Definition.Version, [parent.Definition.Name], Baseline: parent.Baseline)], default);

        Assert.Equal(3, result.Functions.Count);
        Assert.Equal("2.0.0-rc.1", Assert.Single(result.Packages).Version);
        Assert.Contains(result.Functions, item => item.Function.Name == "Local.Parent" && !item.IsDependency);
        Assert.Contains(result.Functions, item => item.Function.Name == "Remote.One" && item.IsDependency);
    }

    /// <summary>预发行自动处理仍拒绝两个父包声明的不同精确依赖版本.</summary>
    [Fact]
    public async Task DifferentExactPrereleaseDependenciesRemainConflicts()
    {
        var first = Package("First", "1.0.0", [new("Shared", "2.0.0-beta.1")], "First.One", "() => Shared.One()");
        var second = Package("Second", "1.0.0", [new("Shared", "2.0.0-beta.2")], "Second.One", "() => Shared.One()");
        var old = Package("Shared", "2.0.0-beta.1", [], "Shared.One", "() => 1");
        var next = old with { Version = "2.0.0-beta.2" };

        var error = await Assert.ThrowsAsync<AppException>(() => new UdfDependencyResolver(new Packages(first, second, old, next), new Local()).ResolveAsync(
            [new("daxlib", "First", "1.0.0", ["First.One"]), new("daxlib", "Second", "1.0.0", ["Second.One"])], default));

        Assert.Equal("UDF_VERSION_CONFLICT", error.Code);
    }

    /// <summary>精确预发行内容变化仍拒绝旧选择, 不因免审批跳过基线核验.</summary>
    [Fact]
    public async Task ExactPrereleaseKeepsContentBaselineProtection()
    {
        var package = Package("Remote", "1.0.0-preview", [], "Remote.One", "() => 1");
        var error = await Assert.ThrowsAsync<AppException>(() => new UdfDependencyResolver(new Packages(package), new Local()).ResolveAsync(
            [new("daxlib", "Remote", package.Version, ["Remote.One"], Baseline: Hash('f'))], default));

        Assert.Equal("UDF_PACKAGE_CHANGED", error.Code);
    }

    /// <summary>建立具有固定许可、指纹及单函数的完整测试包.</summary>
    private static UdfPackage Package(string id, string version, UdfPackageDependency[] dependencies, string name, string expression)
        => new(id, version, Hash('c'), "", "", new Dictionary<string, string> { ["LICENSE"] = "test license" }, dependencies,
            [new(name, expression, "", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch);

    /// <summary>生成合法固定摘要, 不根据被测代码计算预期值.</summary>
    private static string Hash(char value) => new(value, 64);

    /// <summary>只返回明确声明的精确版本, 对未声明读取直接失败.</summary>
    private sealed class Packages(params UdfPackage[] values) : IUdfPackageProvider
    {
        /// <summary>本组测试不触发目录搜索.</summary>
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => throw new NotSupportedException();
        /// <summary>返回测试包版本, 取消不会被受控数据吞掉.</summary>
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(values.Where(item => item.Id == packageId).Select(item => item.Version).ToArray());
        }
        /// <summary>返回给定 ID 和版本的包, 找不到或重复时失败.</summary>
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(values.Single(item => item.Id == packageId && item.Version == version));
        }
    }

    /// <summary>只提供本组声明的本地定义, 不实现无关 CRUD.</summary>
    private sealed class Local(params LocalUdfEntry[] values) : ILocalUdfStore
    {
        /// <summary>读取精确本地 ID, 保留请求取消.</summary>
        public Task<LocalUdfEntry> GetAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(values.Single(item => item.Id == id));
        }
        public Task<IReadOnlyList<LocalUdfEntry>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocalUdfEntry> CreateAsync(LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
        public Task<LocalUdfEntry> UpdateAsync(string id, string baseline, LocalUdfDefinition definition, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
        public Task DeleteAsync(string id, string baseline, CancellationToken cancellationToken, Action? committing = null) => throw new NotSupportedException();
    }
}
