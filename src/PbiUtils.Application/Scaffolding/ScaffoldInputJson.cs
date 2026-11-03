// FilePath    : pbi-utils\src\PbiUtils.Application\Scaffolding\ScaffoldInputJson.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一脚手架输入的必需构造字段和精确可选段, 不放宽持久记录或其他桥接输入.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Scaffolding;

/// <summary>CLI 与桌面共用严格输入规则, 可为 null 不等于可省略.</summary>
public static class ScaffoldInputJson
{
    /// <summary>只为协议明确允许省略的字段解除必填, 其余非可选构造参数均须出现.</summary>
    /// <returns>独立严格选项, 仅组合创建请求可选段及 UDF 版本特例.</returns>
    public static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(UdfInputJson.ConfigureSelection);
        resolver.Modifiers.Add(type =>
        {
            foreach (var property in type.Properties)
            {
                if (type.Type == typeof(ScaffoldRequest) && property.Name is "measures" or "sample" or "calendar" or "periods" or "udfs") property.IsRequired = false;
            }
        });
        return InputJson.CreateStrict(resolver);
    }
}
