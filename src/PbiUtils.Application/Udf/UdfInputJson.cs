// FilePath    : pbi-utils\src\PbiUtils.Application\Udf\UdfInputJson.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : UDF 精确选择协议的可省略版本规则, 与创建协议独立组合.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Udf;

/// <summary>缺省 UDF 版本表示解析稳定版, 不放宽其他构造字段.</summary>
public static class UdfInputJson
{
    /// <summary>创建包含 UDF 版本特例的严格 JSON 选项, 不添加创建请求特例.</summary>
    /// <returns>由调用方独立拥有的选项.</returns>
    public static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(ConfigureSelection);
        return InputJson.CreateStrict(resolver);
    }

    /// <summary>只为 UdfSelection.version 解除构造必填, 供创建协议显式组合.</summary>
    /// <param name="type">序列化器当前解析的类型元数据.</param>
    public static void ConfigureSelection(JsonTypeInfo type)
    {
        if (type.Type != typeof(UdfSelection)) return;
        foreach (var property in type.Properties)
            if (property.Name == "version") property.IsRequired = false;
    }
}
