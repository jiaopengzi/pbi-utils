// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldValidationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证脚手架业务参数边界和当前六字段模板配置.

using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只校验业务输入, 不读写目录或模型.</summary>
public sealed class ScaffoldValidationTests
{
    /// <summary>跳过全部可选步骤仍是有效脚手架请求.</summary>
    [Fact]
    public void AcceptsTemplateOnlyRequest() => ScaffoldValidation.Validate(Request());

    /// <summary>工程名称只能是单个普通目录名称, 拒绝 Windows 保留名和尾部空白.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("a\\b")]
    [InlineData("CON")]
    [InlineData("LPT1.txt")]
    [InlineData("project.")]
    [InlineData("project ")]
    public void RejectsUnsafeProjectName(string name) => Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { ProjectName = name }));

    /// <summary>协议和语言不允许静默降级或忽略.</summary>
    [Fact]
    public void RejectsUnsupportedVersionAndLocale()
    {
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { SchemaVersion = 2 }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Locale = "fr-fr" }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { TemplateBaseline = "not-a-hash" }));
    }

    private static readonly string[] expected = ["refresh_time", "version", "author", "weburl_text", "weburl", "weburl_tip"];

    /// <summary>当前默认配置包含六个固定名称, 使用受支持类别且每次调用返回独立集合.</summary>
    [Fact]
    public void ProvidesSixCurrentTemplateDefinitions()
    {
        var definitions = TemplateMeasureValidation.DefaultMeasures();
        Assert.Equal(expected, definitions.Select(item => item.Name));
        ScaffoldValidation.Validate(Request() with { Measures = definitions });
        Assert.Equal("Uncategorized", definitions[0].DataCategory);
        Assert.Contains(definitions, item => item.DataCategory == "WebUrl");
        definitions[0] = definitions[0] with { Name = "changed" };
        Assert.Equal("refresh_time", TemplateMeasureValidation.DefaultMeasures()[0].Name);
    }

    /// <summary>大小写重复指标或不支持的数据类别在准备文件前失败.</summary>
    [Fact]
    public void RejectsConflictingMeasures()
    {
        var item = TemplateMeasureValidation.DefaultMeasures()[0];
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Measures = [item, item with { Name = item.Name.ToUpperInvariant() }] }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Measures = [item with { DataCategory = "Executable" }] }));
    }

    /// <summary>复杂日历不得超出离线资源范围, 覆盖日期必须唯一且属于选择年份.</summary>
    [Fact]
    public void RejectsInvalidCalendarAndOverrides()
    {
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Calendar = new("full", 1999, 2024) }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Calendar = new("full", 2024, 2100) }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Calendar = new("simple", 2025, 2024) }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Calendar = new("full", 2024, 2024, Overrides: [new("2024-01-01", false, "a"), new("2024-01-01", true, "b")]) }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Calendar = new("full", 2024, 2024, Overrides: [new("2025-01-01", false, "a")]) }));
    }

    /// <summary>函数来源、函数名和重复包选择必须可确定, 不能凭网页 URL 下载.</summary>
    [Fact]
    public void RejectsInvalidUdfSelection()
    {
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Udfs = [new("https://example.com", "package", null, ["Fn"])] }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Udfs = [new("daxlib", "package", null, [])] }));
        Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Request() with { Udfs = [new("daxlib", "package", null, ["Fn"]), new("daxlib", "PACKAGE", "1.0.0", ["Other"])] }));
    }

    /// <summary>创建不含外部路径的最小请求.</summary>
    private static ScaffoldRequest Request() => new(1, "builtin:blank", new string('a', 64), "我的工程", "zh-cn", null, null, null, null, null);
}
