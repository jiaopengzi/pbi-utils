// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopUdfCompatibilityTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Desktop UDF 实际能力与冻结配置独立确认边界, 不连接真实用户模型.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopUdfCompatibilityTests
{
    private const string StoreProductVersion = "2.158.1304.0 (26.09)+a43ace043425e30778f23721b943ff885c2214e5";
    private static readonly int[] UdfLevel = [1702];
    private static readonly int[] CurrentAndUdfLevels = [1606, 1702];

    /// <summary>真实 Store 资源的数值 ProductParts 全零也不能否定有效 ProductVersion, 显示后缀不影响数值比较.</summary>
    [Fact]
    public void StoreProductVersionWorksWhenNumericProductPartsAreZero()
    {
        var numericParts = new Version(0, 0, 0, 0);
        Assert.True(numericParts < new Version(2, 155, 756, 0));
        var actual = DesktopUdfCompatibility.ParseDesktopProductVersion(StoreProductVersion);
        Assert.Equal(new Version(2, 158, 1304, 0), actual);
        DesktopUdfCompatibility.RequireSupportedEnvironment(1606, () => actual, () => UdfLevel);
    }

    /// <summary>仅接受开头四段数值和已知正式显示后缀, 不把后缀当成版本的一部分.</summary>
    /// <param name="productVersion">受信主模块 ProductVersion 的常见正式格式.</param>
    [Theory]
    [InlineData("2.158.1304.0")]
    [InlineData(StoreProductVersion)]
    [InlineData("2.158.1304.0+a43ace043425e30778f23721b943ff885c2214e5")]
    public void ProductVersionParsesStrictFourPartPrefix(string productVersion) =>
        Assert.Equal(new Version(2, 158, 1304, 0), DesktopUdfCompatibility.ParseDesktopProductVersion(productVersion));

    /// <summary>损坏、未知、少段、中间版本或未识别后缀都保持未知, 不从任意位置搜索数字.</summary>
    /// <param name="productVersion">不能证明实际四段产品版本的资源字符串.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("2.158.1304")]
    [InlineData("2.158.1304.0.1")]
    [InlineData("Product 2.158.1304.0")]
    [InlineData("v2.158.1304.0")]
    [InlineData(" 2.158.1304.0")]
    [InlineData("2.158.1304.0 ")]
    [InlineData("2.158.1304.0 malformed")]
    [InlineData("2.158.1304.0+not-a-hash")]
    [InlineData("2.158.1304.0 (26.13)+a43ace043425e30778f23721b943ff885c2214e5")]
    [InlineData("2.158.1304.0 (26.09) arbitrary 2.999.0.0")]
    [InlineData("2147483648.158.1304.0")]
    public void UnknownProductVersionIsRejected(string? productVersion) =>
        Assert.Null(DesktopUdfCompatibility.ParseDesktopProductVersion(productVersion));

    /// <summary>主模块版本资源也有严格长度预算, 不对无界内容执行版本识别.</summary>
    [Fact]
    public void OversizedProductVersionIsRejected() =>
        Assert.Null(DesktopUdfCompatibility.ParseDesktopProductVersion("2.158.1304.0+" + new string('a', 257)));

    /// <summary>模型已达到 UDF 级别时直接通过, 即使版本或能力读取不可用也不触发这些外部读取.</summary>
    /// <param name="compatibilityLevel">实际权威模型级别, 1702 和未来更高级别均不检查 Desktop 版本.</param>
    [Theory]
    [InlineData(1702)]
    [InlineData(1703)]
    public void CompatibleModelShortCircuitsDesktopVersionAndEngineSupport(int compatibilityLevel)
    {
        DesktopUdfCompatibility.RequireSupportedEnvironment(compatibilityLevel,
            () => throw new InvalidOperationException("Desktop version must not be read"),
            () => throw new InvalidOperationException("Engine levels must not be read"));
    }

    /// <summary>低级别模型先核实际 Desktop 产品版本, June 2026 GA 的精确门槛和后续版本均继续核引擎升级支持.</summary>
    /// <param name="productVersion">测试提供的受信产品版本, 产品运行时由原 Desktop 进程资源取得.</param>
    [Theory]
    [InlineData("2.155.756.0")]
    [InlineData("2.158.1304.0")]
    public void LowModelRequiresGaDesktopVersionBeforeSupportedUpgradeLevel(string productVersion)
    {
        List<string> events = [];
        DesktopUdfCompatibility.RequireSupportedEnvironment(1606,
            () => { events.Add("version"); return Version.Parse(productVersion); },
            () => { events.Add("supported-levels"); return CurrentAndUdfLevels; });
        Assert.Equal(["version", "supported-levels"], events);
    }

    /// <summary>未知或低于 GA 的版本拒绝时不再调用引擎支持查询, 不用可升级集合弥补版本不足.</summary>
    /// <param name="productVersion">原 Desktop 产品版本或未知值 null.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("2.154.1260.0")]
    [InlineData("2.155.755.999")]
    public void LowModelRejectsUnknownOrPreGaVersionBeforeEngineQuery(string? productVersion)
    {
        var levelReads = 0;
        var error = Assert.Throws<AppException>(() => DesktopUdfCompatibility.RequireSupportedEnvironment(1606,
            () => productVersion is null ? null : Version.Parse(productVersion),
            () => { levelReads++; return UdfLevel; }));
        Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", error.Code);
        Assert.Equal(0, levelReads);
    }

    /// <summary>低级别模型即使 Desktop 已达 GA, 仍必须证明本次 1702 升级确由实际引擎支持.</summary>
    [Fact]
    public void LowModelWithGaDesktopStillRejectsMissingExactUpgradeSupport()
    {
        foreach (var levels in new int[][] { [], [1606], [1703] })
        {
            var error = Assert.Throws<AppException>(() => DesktopUdfCompatibility.RequireSupportedEnvironment(1606,
                () => new Version(2, 158, 1304, 0), () => levels));
            Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", error.Code);
        }
    }

    /// <summary>服务端支持集合必须明确包含 1702, 更高的其他值不能用来猜测兼容.</summary>
    [Fact]
    public void ServerMustDeclareExactUdfCompatibilityLevel()
    {
        DesktopUdfCompatibility.RequireSupportedLevel([1606, 1702, 1703], 1702);
        foreach (var levels in new int[][] { [], [1606], [1703] })
            Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", Assert.Throws<AppException>(() => DesktopUdfCompatibility.RequireSupportedLevel(levels, 1702)).Code);
        Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", Assert.Throws<AppException>(() => DesktopUdfCompatibility.RequireSupportedLevel([1703], 1703)).Code);
    }

    /// <summary>只有冻结计划同时具备真实 UDF、configuration.apply 操作和本次独立同意才允许升级.</summary>
    /// <param name="operation">当前实际业务操作.</param>
    /// <param name="approved">冻结配置是否包含本次独立批准.</param>
    /// <param name="hasFunctions">冻结解析结果是否实际包含 UDF.</param>
    /// <param name="allowed">本次候选是否应获准升至 1702.</param>
    [Theory]
    [InlineData("configuration.apply", true, true, true)]
    [InlineData("configuration.apply", false, true, false)]
    [InlineData("configuration.apply", true, false, false)]
    [InlineData("udf.set", true, true, false)]
    public void UpgradeRequiresActualApprovedConfiguration(string operation, bool approved, bool hasFunctions, bool allowed)
    {
        ResolvedUdfFunction[] functions = hasFunctions
            ? [new(new("Test.Answer", "() => 42", "", new Dictionary<string, string>()), "local", "fixture", "1.0.0", "baseline", false)] : [];
        using var plan = new ModelConfigurationPlan("baseline", "digest", new(AllowCompatibilityUpgrade: approved), new(functions, []), [], null);
        ModelEditRequest[] edits = [new(operation, "configuration", "apply", "", Configuration: plan)];
        if (allowed) Assert.True(DesktopUdfCompatibility.GetUpgradeApproval(1606, 1702, edits));
        else Assert.Equal("UDF_MODEL_WRITE_UNSUPPORTED", Assert.Throws<AppException>(() => DesktopUdfCompatibility.GetUpgradeApproval(1606, 1702, edits)).Code);
        Assert.False(DesktopUdfCompatibility.GetUpgradeApproval(1702, 1702, edits));
    }

    /// <summary>函数直接修改和删除均属于 UDF 写入, 普通表编辑不误触发按原模型级别选择的环境核验.</summary>
    [Fact]
    public void AllDirectUdfWritesAreRecognizedForConditionalEnvironmentCheck()
    {
        Assert.True(DesktopUdfCompatibility.HasUdfWrites([new("udf.set", "Test.Answer", "expression", "() => 42")]));
        Assert.True(DesktopUdfCompatibility.HasUdfWrites([new("udf.deleteUnused", "UDF", "items", "[]")]));
        Assert.False(DesktopUdfCompatibility.HasUdfWrites([new("table.set", "T", "description", "changed")]));
    }
}
