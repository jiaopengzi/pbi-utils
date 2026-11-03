// FilePath    : pbi-utils\src\PbiUtils.Application\Models\ModelNames.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 模型对象名称的共享输入校验, 不推断查询标识符或绑定类型.

using PbiUtils.Core;

namespace PbiUtils.Application.Models;

/// <summary>模型编辑和生成流程共用的对象名称边界.</summary>
public static class ModelNames
{
    /// <summary>验证业务身份及长度, 避免控制字符或无边界输出.</summary>
    /// <param name="value">已知模型对象的业务名称, 不作为任意公式片段.</param>
    /// <returns>验证通过的原名称, 不规范化或更改绑定.</returns>
    /// <exception cref="AppException">名称为空、超过 200 个字符或包含控制字符.</exception>
    public static string Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new AppException("INVALID_OBJECT_NAME", "对象名称为空、过长或含控制字符");
        return value;
    }
}
