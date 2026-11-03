// FilePath    : pbi-utils\src\PbiUtils.Application\Input\InputValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享摘要身份和有界文本规则, 不授予文件或模型权限.

using System.Text;

namespace PbiUtils.Application.Input;

/// <summary>创建、配置和 UDF 协议使用的无副作用输入叶子规则.</summary>
public static class InputValidation
{
    /// <summary>判断规范小写 SHA256; 输入只表示摘要身份, 不构成路径授权.</summary>
    /// <param name="value">待核对摘要或 null.</param>
    /// <returns>恰好为 64 个小写十六进制字符时为 true.</returns>
    public static bool IsDigest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>限制 UTF-8 字节数, 保留合法换行和制表符但拒绝其他控制字符.</summary>
    /// <param name="value">待核对业务文本, null 无效.</param>
    /// <param name="maximum">最大 UTF-8 字节数.</param>
    /// <returns>文本处于长度和字符边界内时为 true.</returns>
    public static bool Text(string? value, int maximum) => value is not null && Encoding.UTF8.GetByteCount(value) <= maximum
        && !value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));
}
