// FilePath    : pbi-utils\src\PbiUtils.Application\Models\ProjectService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义离线工程快照和共享只读用例, 不依赖 TOM 或文件系统.

using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;
using PbiUtils.Application.Calendar;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Application.Models;

/// <summary>已读取文件的相对路径和内容指纹, 不含缓存或凭据.</summary>
public sealed record ProjectFile(string Path, string Sha256);
/// <summary>度量值完整表达式与展示属性.</summary>
public sealed record MeasureInfo(string Table, string Name, string Expression, string? Description, string? FormatString, string? DisplayFolder, bool IsHidden);
/// <summary>列的实际类型与显示属性.</summary>
public sealed record ColumnInfo(string Name, string Kind, string DataType, bool IsHidden, string? Description);
/// <summary>表结构, 分区只返回类型, 不暴露连接信息.</summary>
public sealed record TableInfo(string Name, string? Description, bool IsHidden, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<string> PartitionKinds,
    ScaffoldCalendarCapability? CalendarCapability = null);
/// <summary>DAX UDF 完整定义.</summary>
public sealed record FunctionInfo(string Name, string Expression, string? Description);
/// <summary>模型元数据快照, 不包含数据源字符串或角色表达式.</summary>
public sealed record ModelInfo(string Name, int CompatibilityLevel, string Culture, IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<MeasureInfo> Measures, IReadOnlyList<FunctionInfo> Functions, int RelationshipCount, int RoleCount,
    ScaffoldPeriodOptions? Periods = null, TemplateMeasureConfiguration? TemplateMeasureConfiguration = null);
/// <summary>报表视觉对象, 坐标缺失时保持 null.</summary>
public sealed record VisualInfo(string Id, string Type, double? X, double? Y, double? Width, double? Height);
/// <summary>PBIR 页面及对象清单.</summary>
public sealed record PageInfo(string Id, string DisplayName, double? Width, double? Height, IReadOnlyList<VisualInfo> Visuals);
/// <summary>真实磁盘工程快照, 基线只用于对应已读取文件的身份判断.</summary>
public sealed record ProjectSnapshot(string ProjectPath, string ProjectId, string BaselineId, string ModelFormat,
    string ReportFormat, string ModelVersion, string? ReportVersion, ModelInfo Model,
    IReadOnlyList<PageInfo> Pages, IReadOnlyList<ProjectFile> Files, IReadOnlyList<string> Warnings, ModelConnectionInfo? Connection = null, bool HasPendingChanges = false, ModelSaveResult? SaveResult = null)
{
    /// <summary>从当前权威快照计算统一展示摘要, 不使用扫描或目录缓存补齐未知项.</summary>
    /// <value>当前实际模型计数及未提供能力.</value>
    public ModelSummary Summary => ModelSummaryFactory.FromSnapshot(this);
}
/// <summary>分别记录模型、文件和 Desktop 同步事实; 文件先提交时允许 FileSaved=true/ModelSaved=false, 不推定完整成功.</summary>
public sealed record ModelSaveResult(bool ModelSaved, bool FileSaved, bool DesktopSynchronized, string Message, string? ErrorCode = null)
{
    /// <summary>当前连接模型完成提交后的统一事实提示, 文件仍由用户在 Desktop 保存.</summary>
    public const string ConnectedDesktopMessage = "当前连接模型已更新；请在 Power BI Desktop 保存报表。";
    /// <summary>连接模型以权威引擎回读完成提交, 文件由用户在 Desktop 保存; 离线文件目标仍须证明文件已保存.</summary>
    /// <param name="connectionKind">宿主绑定的真实来源, 不接受网页声明授予写入能力.</param>
    /// <returns>当前保存目标的全部适用阶段是否已完成, 不改变各阶段原始事实.</returns>
    public bool IsCompleteFor(string? connectionKind) => ErrorCode is null && ModelSaved
        && (connectionKind == "desktop" ? DesktopSynchronized : FileSaved);
}
/// <summary>当前模型来源, 离线文件快照的空值按 PBIP 处理; 展示身份不作为连接授权.</summary>
/// <param name="Kind">已绑定的模型来源.</param>
/// <param name="Label">来源的固定展示名称.</param>
/// <param name="IsLimited">是否为仅提供部分元数据的架构连接.</param>
/// <param name="InstanceId">已绑定 Desktop 引擎的完整身份; 其他来源为 null.</param>
/// <param name="ModelId">已绑定 Service 目录条目的后端句柄; 其他来源为 null, 不按模型名称匹配.</param>
/// <param name="FilePath">官方 Desktop Bridge 核验的当前文件路径; 不可核验时为空.</param>
/// <param name="FileIdentityStatus">verified、unsaved 或 unavailable; 非 Desktop 来源为空.</param>
/// <param name="FileIdentityErrorCode">文件身份不可核验时的稳定错误, 不影响只读模型连接.</param>
public sealed record ModelConnectionInfo(string Kind, string Label, bool IsLimited = false, string? InstanceId = null, string? ModelId = null,
    string? FilePath = null, string? FileIdentityStatus = null, string? FileIdentityErrorCode = null)
{
    /// <summary>比较稳定连接身份, 文件保存位置和 Bridge 可用性仅属观测信息, 不改变原引擎授权.</summary>
    /// <param name="left">原快照来源; null 表示离线来源.</param>
    /// <param name="right">重新读取的来源.</param>
    /// <returns>原来源、能力、实例与服务模型绑定是否相同; 不替代完整模型基线或平台身份核验.</returns>
    public static bool SameTarget(ModelConnectionInfo? left, ModelConnectionInfo? right)
        => left is null ? right is null : right is not null && left.Kind == right.Kind && left.Label == right.Label
            && left.IsLimited == right.IsLimited && left.InstanceId == right.InstanceId && left.ModelId == right.ModelId;
}
/// <summary>离线外部适配契约.</summary>
public interface IProjectReader
{
    /// <summary>在给定授权根内读取唯一工程, 失败不返回空成功.</summary>
    Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken);
}
/// <summary>由 CLI、桌面和 MCP 共用的只读入口.</summary>
public sealed class ProjectService(IProjectReader reader, ILogger<ProjectService>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<ProjectService>.Instance;

    /// <summary>验证用户选择的路径并读取; 不进行网络访问或模型写入.</summary>
    public Task<ProjectSnapshot> InspectAsync(string projectPath, CancellationToken cancellationToken) => BusinessLog.RunAsync<ProjectSnapshot>(log, "Project.Inspect", () =>
    {
        if (string.IsNullOrWhiteSpace(projectPath) || projectPath.Length > 2048 || projectPath.Contains('\0'))
            throw new AppException("INVALID_PROJECT_PATH", "请选择有效的本地 PBIP 文件或工程目录");
        cancellationToken.ThrowIfCancellationRequested();
        return reader.ReadAsync(projectPath, cancellationToken);
    });
}
