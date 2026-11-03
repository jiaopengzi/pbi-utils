// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UdfReferenceScannerTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证标准内置调用不会成为伪 UDF 依赖, 未知名称与真实依赖仍失败关闭.

using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>通过真实扫描器与包依赖解析器复现批量选择, 不执行 DAX 或写入模型.</summary>
public sealed class UdfReferenceScannerTests
{
    /// <summary>微软明确列出的 INFO.VIEW 和相邻元数据函数均按完整名称识别, 大小写不影响结果.</summary>
    [Theory]
    [InlineData("INFO.VIEW.MEASURES")]
    [InlineData("info.view.columns")]
    [InlineData("INFO.VIEW.TABLES")]
    [InlineData("INFO.VIEW.RELATIONSHIPS")]
    [InlineData("INFO.ANNOTATIONS")]
    [InlineData("INFO.CALCDEPENDENCY")]
    [InlineData("INFO.CALCULATIONGROUPS")]
    [InlineData("INFO.CALCULATIONITEMS")]
    [InlineData("INFO.FUNCTIONS")]
    [InlineData("INFO.USERDEFINEDFUNCTIONS")]
    public void MetadataBuiltinsDoNotBecomeUdfDependencies(string name) =>
        Assert.Empty(UdfReferenceScanner.References("() => COUNTROWS(" + name + "())"));

    /// <summary>同一白名单遗漏还影响 DAXLib 使用的标准标量、表和点分统计调用, 不能逐个报错后才修补.</summary>
    [Theory]
    [InlineData("BITAND")]
    [InlineData("BITLSHIFT")]
    [InlineData("BITOR")]
    [InlineData("BITRSHIFT")]
    [InlineData("COLUMNSTATISTICS")]
    [InlineData("CONTAINSSTRINGEXACT")]
    [InlineData("CONVERT")]
    [InlineData("CURRENTGROUP")]
    [InlineData("DATATABLE")]
    [InlineData("DATEADD")]
    [InlineData("DISTINCTCOUNTNOBLANK")]
    [InlineData("GEOMEANX")]
    [InlineData("GROUPBY")]
    [InlineData("INDEX")]
    [InlineData("ISCROSSFILTERED")]
    [InlineData("ISDATETIME")]
    [InlineData("ISNUMERIC")]
    [InlineData("ISODD")]
    [InlineData("NAMEOF")]
    [InlineData("LASTNONBLANK")]
    [InlineData("NORM.DIST")]
    [InlineData("NORM.S.DIST")]
    [InlineData("ORDERBY")]
    [InlineData("PERCENTILE.INC")]
    [InlineData("PERCENTILEX.EXC")]
    [InlineData("PERCENTILEX.INC")]
    [InlineData("RANK")]
    [InlineData("ROWNUMBER")]
    [InlineData("STDEV.S")]
    [InlineData("STDEVX.P")]
    [InlineData("STDEVX.S")]
    [InlineData("T.INV.2T")]
    [InlineData("TABLEOF")]
    [InlineData("TOCSV")]
    [InlineData("VAR.S")]
    [InlineData("WINDOW")]
    public void OtherDocumentedBuiltinsDoNotBecomeUdfDependencies(string name) =>
        Assert.Empty(UdfReferenceScanner.References("() => " + name + "(1)"));

    /// <summary>未知的 INFO 和点分名称继续作为必须证明的依赖, 不按命名空间或内置名称前缀放行.</summary>
    [Theory]
    [InlineData("INFO.VIEW.MEASUREZ")]
    [InlineData("INFO.NOTAREALFUNCTION")]
    [InlineData("NORM.Custom")]
    [InlineData("ISUNKNOWN")]
    [InlineData("Example.Library.Helper")]
    public void UnknownNamesRemainDependencies(string name) =>
        Assert.Equal([name], UdfReferenceScanner.References("() => " + name + "()"));

    /// <summary>字符串、转义引用、行注释及块注释均不能制造未知依赖.</summary>
    [Fact]
    public void IgnoresNamesInLiteralsAndComments() =>
        Assert.Equal(["Example.Helper"], UdfReferenceScanner.References(
            "() => Example.Helper() + LEN(\"INFO.Unknown()\") + [Not.A.Function] // Unknown.Line()\n /* Unknown.Block() */"));

    /// <summary>批量选择同一包的多个元数据函数时, 原始定义保留且不生成伪依赖锁.</summary>
    [Fact]
    public async Task BatchSelectionResolvesMetadataFunctions()
    {
        var package = new UdfPackage("Example.Metadata", "1.0.0", new string('a', 64), "", "", new Dictionary<string, string>(), [],
            [new("Example.Metadata.Measures", "() => INFO.VIEW.MEASURES()", "", new Dictionary<string, string>()),
             new("Example.Metadata.Tables", "() => INFO.VIEW.TABLES()", "", new Dictionary<string, string>())], DateTimeOffset.UnixEpoch);
        var root = Path.Combine(Path.GetTempPath(), "pbi-udf-scanner-" + Guid.NewGuid().ToString("N"));
        var result = await new UdfDependencyResolver(new PackageProvider(package), new LocalUdfStore(root)).ResolveAsync(
            [new("daxlib", package.Id, package.Version, [.. package.Functions.Select(item => item.Name)])], default);
        Assert.Equal(package.Functions.Select(item => item.Expression), result.Functions.Select(item => item.Function.Expression));
        Assert.All(result.Functions, item => Assert.False(item.IsDependency));
        Assert.Empty(Assert.Single(result.Packages).DependencyFunctions);
        Assert.False(Directory.Exists(root));
    }

    /// <summary>只返回绑定的精确包, 未声明的读取和目录查询显式失败.</summary>
    private sealed class PackageProvider(UdfPackage package) : IUdfPackageProvider
    {
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packageId != package.Id || version != package.Version) throw new AppException("UDF_PACKAGE_MISSING", "测试包不存在");
            return Task.FromResult(package);
        }
    }
}
