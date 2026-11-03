// FilePath    : pbi-utils\src\PbiUtils.Core\ModelExportLocation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义宿主绑定的最近导出目录显示状态, 路径仅供展示而非网页打开参数.

namespace PbiUtils.Core;

/// <summary>最近一次成功导出的本地目录, 打开必须提交记录身份并由宿主重新解析.</summary>
/// <param name="Id">宿主签发的随机记录身份, 无记录时为 null.</param>
/// <param name="DisplayPath">宿主记录的目录原文, 仅展示且不能作为打开权限.</param>
/// <param name="Available">目录当前存在且通过链接检查; 打开前仍须重新核验.</param>
/// <param name="Warning">持久记录或目录不可用的稳定提示, 不改变导出结果.</param>
/// <param name="IsSimulation">是否来自明确的浏览器模拟, 模拟不能宣称打开真实目录.</param>
public sealed record ModelExportLocation(string? Id, string? DisplayPath, bool Available, string? Warning = null, bool IsSimulation = false);
