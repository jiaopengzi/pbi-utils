// FilePath    : pbi-utils\src\PbiUtils.Application\TemplateMeasures\TemplateMeasureContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 创建、持续配置和偏好共用的模板度量完整字段契约.

namespace PbiUtils.Application.TemplateMeasures;

/// <summary>模板度量值的完整可编辑字段; Enabled 表示开启生成或保留既有对象并隐藏.</summary>
public sealed record TemplateMeasureDefinition(string Name, string Value, string DataCategory, bool Enabled, string Description, string DisplayFolder);
/// <summary>模型生成状态或有效公共配置文件的存储模式及六字段, 不携带磁盘路径.</summary>
public sealed record TemplateMeasureConfiguration(string Mode, TemplateMeasureDefinition[] Measures);
