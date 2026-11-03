// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Configuration\ModelConfiguration.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 不含网页路径的共享配置输入与宿主准备的冻结资产计划.

using PbiUtils.Application.Models;
using System.Text.Json.Serialization;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Application.Models.Configuration;

/// <summary>关闭段为 null, 表示本次不提交而非删除既有对象.</summary>
public sealed record ModelConfigurationRequest(TemplateMeasureDefinition[]? Measures = null, SampleDataOptions? Sample = null,
    ScaffoldCalendarOptions? Calendar = null, ScaffoldPeriodOptions? Periods = null, UdfSelection[]? Udfs = null,
    bool AllowCompatibilityUpgrade = false, string Locale = "zh-cn", string MeasuresMode = "tmdl", string? MeasureConfigurationRevision = null);

/// <summary>资产只来自受信编译器的私有目录, 路径不得通过网页参数构造.</summary>
public sealed record ModelConfigurationAsset(string RelativePath, string? SourceFile, string? BeforeHash, string? AfterHash, long Bytes);

/// <summary>只由宿主权威文件绑定签发的资产能力, 不接受网页路径或模型声称的任意根目录.</summary>
public sealed record ModelConfigurationAssetContext(string Root, IReadOnlyDictionary<string, string> Assets,
    Func<CancellationToken, Task> Verify, string? ConfigTableName = null, TemplateMeasureConfiguration? ExistingConfiguration = null,
    bool CanUseMeasureConfig = false, string? ConfigurationRevision = null, string? UnavailableReason = null);

/// <summary>从实际连接身份核实本地模型文件的配置资产能力, 不修改模型定义或执行数据刷新.</summary>
public interface IModelConfigurationAssetWorkspace
{
    /// <summary>返回宿主核实的工程根、完整资产归属和持续绑定校验; 无文件能力时返回 null.</summary>
    Task<ModelConfigurationAssetContext?> GetConfigurationAssetsAsync(ProjectSnapshot snapshot, CancellationToken token);
}

/// <summary>样例派生的日期范围也属于本次精确确认摘要, 保存时只重放冻结选项.</summary>
public sealed record ModelConfigurationCalendarChange(string TableName, int BeforeStartYear, int BeforeEndYear, ScaffoldCalendarOptions Options);

/// <summary>最终实际模型名、M源列、语义和本次新增事实, 不从编号偏好猜测已有列.</summary>
public sealed record ModelConfigurationCalendarColumn(string Name, string SourceColumn, string Field, bool Added);

/// <summary>一个日期表的完整最终输出映射进入冻结摘要.</summary>
public sealed record ModelConfigurationCalendarOutput(string TableName, IReadOnlyList<ModelConfigurationCalendarColumn> Columns,
    ModelConfigurationCalendarFunction? Function = null);

/// <summary>函数和调用的完整代码指纹进入冻结摘要, 不用列形状代替定义核验.</summary>
public sealed record ModelConfigurationCalendarFunction(string FunctionName, string DefinitionHash, string InvocationHash,
    string GeneratorVersion, string ResourceVersion, string Locale);

/// <summary>受信私有模型草稿提供只读编译, 不创建目录、不应用模型或保存.</summary>
public interface ICalendarProgramDraft
{
    /// <summary>从完整TOM定义核验已有函数与列映射后返回代码; 失效归属明确失败.</summary>
    Task<ScaffoldCalendarProgram> PreviewCalendarAsync(ScaffoldCalendarOptions options, string locale, CancellationToken token);
}

/// <summary>受信宿主先核模型级别, 不足时核实绑定 Desktop 产品版本和实际升级能力, 不接受网页版本声明.</summary>
public interface IModelCompatibilityUpgradeWorkspace
{
    /// <summary>只读复核原身份和完整基线; 模型已满足直接通过, 低级别再核真实 Desktop 版本和升级支持.</summary>
    /// <param name="baseline">宿主绑定的原始模型快照, 必须与实际连接完全一致.</param>
    /// <param name="compatibilityLevel">本次 UDF 保存要求的目标数据库兼容级别.</param>
    /// <param name="token">连接及读取前后生效的取消令牌, 同步 SDK 调用不能即时中断.</param>
    /// <returns>环境已核实的完成任务, 不代表用户已经同意升级.</returns>
    /// <exception cref="PbiUtils.Core.AppException">目标变化、远端只读、低模型的版本未知或不足、引擎不支持目标级别.</exception>
    Task VerifyCompatibilityUpgradeAsync(ProjectSnapshot baseline, int compatibilityLevel, CancellationToken token);
}

/// <summary>准备一次后只消费精确内容, 不在 TOM 修改或保存时重新解析 latest.</summary>
public sealed class ModelConfigurationPlan(string baselineId, string digest, ModelConfigurationRequest request, UdfResolution udfs,
    IReadOnlyList<ModelConfigurationAsset> assets, string? assetRoot, Action? release = null,
    IReadOnlyList<ModelConfigurationCalendarChange>? derivedCalendars = null,
    IReadOnlyList<ModelConfigurationCalendarOutput>? calendarOutputs = null, ModelConfigurationAssetContext? assetContext = null) : IDisposable
{
    public IReadOnlyList<ModelConfigurationCalendarOutput> CalendarOutputs { get; } = Array.AsReadOnly((calendarOutputs ?? [])
        .Select(output => output with { Columns = Array.AsReadOnly(output.Columns.ToArray()) }).ToArray());
    [JsonIgnore] public bool HasCalendarOutputPlan { get; } = calendarOutputs is not null;
    public string BaselineId { get; } = baselineId;
    public string Digest { get; } = digest;
    public ModelConfigurationRequest Request { get; } = request;
    public UdfResolution Udfs { get; } = udfs;
    public IReadOnlyList<ModelConfigurationAsset> Assets { get; } = assets;
    public IReadOnlyList<ModelConfigurationCalendarChange> DerivedCalendars { get; } = derivedCalendars ?? [];
    [JsonIgnore] public string? AssetRoot { get; } = assetRoot;
    [JsonIgnore] public ModelConfigurationAssetContext? AssetContext { get; } = assetContext;
    /// <summary>只释放编译器拥有的临时资产, 已发布项目不在本生命周期内.</summary>
    public void Dispose() => release?.Invoke();
}
