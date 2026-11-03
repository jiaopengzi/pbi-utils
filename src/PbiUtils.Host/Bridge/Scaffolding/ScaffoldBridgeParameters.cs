// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Scaffolding\ScaffoldBridgeParameters.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义脚手架受限请求 DTO 和严格解析, 不持有草稿或宿主目录授权.

using System.Text.Json;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Host.Bridge.Scaffolding;

/// <summary>解析错误使用原有固定错误码, 未知字段和类型继续拒绝.</summary>
internal static class ScaffoldBridgeParameters
{
    internal sealed record BaseInput(string TemplateId, string TemplateBaseline, string ProjectName, string Locale, bool GitInitialize = false);
    internal sealed record DraftInput(string Section, JsonElement Value, string? DraftId = null, int? Revision = null);
    internal sealed record DraftIdentity(string DraftId, int Revision);
    internal sealed record CreateInput(string DraftId, int Revision, string Digest);
    internal sealed record DigestInput(string Digest);
    internal sealed record TemplateInput(string TemplateId, string TemplateBaseline);
    internal sealed record MeasureInput(int Index, TemplateMeasureDefinition? Item);
    internal sealed record WorkdayInput(string Date, bool IsWorkday, string Name, bool Remove = false);
    internal sealed record UdfFunctionInput(string Source, string PackageId, string FunctionName, bool Selected, string? Version = null, string? Baseline = null, string? SourceBaseline = null, string? CatalogBaseline = null);

}
