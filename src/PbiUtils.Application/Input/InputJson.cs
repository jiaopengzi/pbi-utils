// FilePath    : pbi-utils\src\PbiUtils.Application\Input\InputJson.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享严格 JSON 构造字段与未知属性边界, 不包含领域可省略字段.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PbiUtils.Application.Input;

/// <summary>各输入协议显式组合领域例外, 不放宽其他类型的构造参数.</summary>
public static class InputJson
{
    /// <summary>创建区分大小写、拒绝未知字段且要求非可选构造参数的独立选项.</summary>
    /// <param name="resolver">可选的领域元数据规则, 为 null 时不添加领域特例.</param>
    /// <returns>调用方独立拥有的 JSON 选项, 不读取外部环境.</returns>
    public static JsonSerializerOptions CreateStrict(IJsonTypeInfoResolver? resolver = null) => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = resolver ?? new DefaultJsonTypeInfoResolver()
    };
}
