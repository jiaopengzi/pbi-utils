// FilePath    : pbi-utils\src\PbiUtils.Application\Scaffolding\GitCapability.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义桌面和 CLI 共用的 Git 可用能力及安全原因.

namespace PbiUtils.Application.Scaffolding;

/// <summary>能力只描述当前探测事实, 不保证冻结后初始化仍会成功.</summary>
public sealed record GitCapability(bool Available, string? Reason = null);
