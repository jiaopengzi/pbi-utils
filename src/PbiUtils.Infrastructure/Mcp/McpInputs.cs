// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpInputs.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : MCP 核心工具的精确输入契约, 缺必填或未知字段由统一边界拒绝.

using PbiUtils.Application.SampleData;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;

using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Mcp;

/// <summary>不接受任何工具参数.</summary>
internal sealed record EmptyInput;
/// <summary>仅固定来源的软件更新查询, 不接受仓库地址或下载/执行参数.</summary>
internal sealed record UpdateCheckInput(string Source = "github");
/// <summary>具体本机 PBIP 入口.</summary>
internal sealed record ProjectInput(string Project);
/// <summary>不执行的本地代码格式化输入.</summary>
internal sealed record FormatInput(string Language, string Text);
/// <summary>实际模板目录中的身份与完整基线.</summary>
internal sealed record TemplateInput(string TemplateId, string TemplateBaseline);
/// <summary>完整日期选项与生成语言.</summary>
internal sealed record CalendarInput(ScaffoldCalendarOptions Options, string Locale = "zh-cn");
/// <summary>显式期间生成角色与须避开的已有表名.</summary>
internal sealed record PeriodInput(string BaseMeasure, ScaffoldPeriodOptions Options, string[]? ReservedTableNames = null);
/// <summary>新工程请求与绝对输出路径.</summary>
internal sealed record ScaffoldInput(ScaffoldRequest Request, string Output);
/// <summary>与预览完全一致的新工程输入及精确批准摘要.</summary>
internal sealed record ScaffoldCreateInput(ScaffoldRequest Request, string Output, string Approve);
/// <summary>预览生成的完整摘要.</summary>
internal sealed record DigestInput(string Digest);
/// <summary>已有离线工程的完整基线和业务配置.</summary>
internal sealed record ConfigurationInput(string Project, string Baseline, ModelConfigurationRequest Request);
/// <summary>一次应用只能指定已有计划身份及精确摘要, 不再接受可替换的业务参数.</summary>
internal sealed record PlanApplyInput(string PlanId, string Approve);
/// <summary>由宿主返回的精确对象或计划身份.</summary>
internal sealed record IdInput(string Id);
/// <summary>固定示例业务的输出目录.</summary>
internal sealed record OutputInput(string Output);
/// <summary>全量样例请求, 覆盖必须明确选择.</summary>
internal sealed record SampleInput(string Output, SampleDataOptions Options, bool Overwrite = false);
/// <summary>增量样例请求, 不存在隐含覆盖.</summary>
internal sealed record SampleAppendInput(string Output, SampleDataOptions Options);
/// <summary>样例清理的独立删除确认.</summary>
internal sealed record SampleClearInput(string Output, bool Confirm = false);
/// <summary>已登记 UDF 来源身份及其当前基线.</summary>
internal sealed record UdfSourceInput(string Source = "daxlib", string? SourceBaseline = null);
/// <summary>本地完整目录搜索参数, 不触发网络.</summary>
internal sealed record UdfSearchInput(string Query = "", bool IncludePrerelease = false, string Source = "daxlib", string? SourceBaseline = null);
/// <summary>固定 UDF 包身份与来源.</summary>
internal sealed record UdfPackageInput(string PackageId, string Source = "daxlib", string? SourceBaseline = null);
/// <summary>固定 UDF 包版本与来源, 不解析隐含 latest.</summary>
internal sealed record UdfVersionInput(string PackageId, string Version, string Source = "daxlib", string? SourceBaseline = null);
/// <summary>本地自定义函数内容, 不安装到模型.</summary>
internal sealed record UdfCreateInput(LocalUdfDefinition Definition);
/// <summary>精确函数 ID、存储基线和新版内容.</summary>
internal sealed record UdfUpdateInput(string Id, string Baseline, LocalUdfDefinition Definition);
/// <summary>带基线和独立确认的本地函数删除.</summary>
internal sealed record UdfDeleteInput(string Id, string Baseline, bool Confirm = false);
/// <summary>来源登记或更新只保存配置, 不在登记时联网.</summary>
internal sealed record UdfSourceSaveInput(string Name, string BaseUrl, string? Id = null, string? Baseline = null, string? GitRef = null);
