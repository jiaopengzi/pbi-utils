// FilePath    : pbi-utils\src\PbiUtils.Application\Code\Dax\DaxFormattingStyle.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义共享本地 DAX 格式化的两种 SQLBI 参数布局.

namespace PbiUtils.Application.Code.Dax;

/// <summary>Long 为默认零值并在可读宽度内内联, Short 展开参数; 两者均保留词元与模型绑定.</summary>
public enum DaxFormattingStyle
{
    Long = 0,
    Short = 1
}
