// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\DomainInputValidationTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证独立领域校验保持创建与持续配置的输入边界和 JSON 特例范围.

using System.Text.Json;
using PbiUtils.Application.Input;
using PbiUtils.Application.Models;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>只使用纯输入校验, 不构造环境、模型或配置资产.</summary>
public sealed class DomainInputValidationTests
{
    /// <summary>创建与持续配置都按 UTF-8 字节拒绝超限度量, 而非按字符计数放宽.</summary>
    [Fact]
    public void SharedMeasureValidationKeepsUtf8BudgetAndErrors()
    {
        var definition = new TemplateMeasureDefinition("中文度量", new string('中', 10922), "Uncategorized", true, "", "");
        TemplateMeasureValidation.Validate([definition]);
        ModelConfigurationValidation.Validate(new(Measures: [definition]));
        ScaffoldValidation.Validate(Create() with { Measures = [definition] });
        var overLimit = definition with { Value = definition.Value + "中" };
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => TemplateMeasureValidation.Validate([overLimit])).Code);
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => ModelConfigurationValidation.Validate(new(Measures: [overLimit]))).Code);
        Assert.Equal("INVALID_SCAFFOLD_REQUEST", Assert.Throws<AppException>(() => ScaffoldValidation.Validate(Create() with { Measures = [overLimit] })).Code);
    }

    /// <summary>通用模型名称仍保留原值和独立错误分类, 不由日期或创建入口改变.</summary>
    [Fact]
    public void ModelNamesKeepOriginalNamesAndErrorCategory()
    {
        var name = " 模型 '对象] ";
        Assert.Same(name, ModelNames.Validate(name));
        Assert.Equal("INVALID_OBJECT_NAME", Assert.Throws<AppException>(() => TemplateMeasureValidation.Validate([new("bad\nname", "", "Uncategorized", true, "", "")])).Code);
    }

    /// <summary>UDF.version 可省略只来自显式 UDF 协议, 不放宽创建字段或其他记录.</summary>
    [Fact]
    public void JsonExceptionsStayWithinTheirOwnProtocols()
    {
        const string udf = """{"source":"daxlib","packageId":"package","functions":["Fn"]}""";
        Assert.Null(JsonSerializer.Deserialize<UdfSelection>(udf, UdfInputJson.Create())!.Version);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UdfSelection>(udf, InputJson.CreateStrict()));
        var create = JsonSerializer.SerializeToElement(Create(), JsonSerializerOptions.Web);
        var fields = create.EnumerateObject().Where(property => property.Name is not ("measures" or "sample" or "calendar" or "periods" or "udfs"))
            .ToDictionary(property => property.Name, property => property.Value);
        var omitted = JsonSerializer.Serialize(fields);
        Assert.NotNull(JsonSerializer.Deserialize<ScaffoldRequest>(omitted, ScaffoldInputJson.Create()));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScaffoldRequest>(omitted, UdfInputJson.Create()));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TemplateMeasureDefinition>("""{"name":"missing fields"}""", ScaffoldInputJson.Create()));
    }

    /// <summary>抽出的嵌入 JSON 解析器继续拒绝大小写重复字段和原字符上限.</summary>
    [Fact]
    public void StructuredInputKeepsDuplicateAndSizeBoundaries()
    {
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => StructuredInput.Parse<Dictionary<string, string>>("""{"value":"one","Value":"two"}""")).Code);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => StructuredInput.Parse<string>(new string(' ', 30001))).Code);
    }

    /// <summary>构造无外部路径的有效最小创建请求.</summary>
    /// <returns>只包含当前必需创建身份的新请求.</returns>
    private static ScaffoldRequest Create() => new(1, "builtin:blank", new string('a', 64), "共享校验", "zh-cn", null, null, null, null, null);
}
