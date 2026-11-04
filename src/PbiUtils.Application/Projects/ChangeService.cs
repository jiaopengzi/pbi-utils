// FilePath    : pbi-utils\src\PbiUtils.Application\Projects\ChangeService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享不可变副本变更计划, 校验白名单、精确批准、期限与防重复应用.

using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using PbiUtils.Core;
using System.Text.Json.Serialization;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Application.Projects;

/// <summary>窄域变更请求, 输出只能是新目录副本.</summary>
public sealed record ChangeRequest(string ProjectPath, string OutputPath, string Operation, string ObjectName, string Property, string Value, string? Table = null, bool InPlace = false,
    [property: JsonIgnore] ModelConfigurationPlan? Configuration = null);
/// <summary>实际文件差异; null 表示新增或删除, 二进制不输出内容.</summary>
public sealed record FileChange(string Path, string? BeforeHash, string? AfterHash, string? Before, string? After);
/// <summary>绑定源清单、目标与实际差异的固定计划.</summary>
public sealed record ChangePlan(string Id, string Digest, string ProjectId, string BaselineId, string OutputPath,
    ChangeRequest Request, IReadOnlyList<FileChange> Changes, IReadOnlyList<string> Warnings, string Stage = "planned");
/// <summary>副本文件应用结果, 不声称 Desktop 或引擎已验证.</summary>
public sealed record ChangeResult(string PlanId, string Stage, string ProjectPath, string BaselineId, IReadOnlyList<string> Warnings,
    ModelSaveResult? SaveResult = null, string? ConnectionKind = null);
/// <summary>外部文件准备和原子发布适配器, 不含批准策略.</summary>
public interface IChangeWorkspace : IDisposable
{
    /// <summary>在独立私有 staging 准备完整副本并读取真实差异, 不改源文件.</summary>
    Task<ChangePlan> PrepareAsync(ChangeRequest request, CancellationToken token);
    /// <summary>重新检查源与 staging 基线, 原子发布到无冲突的新目录.</summary>
    Task<ChangeResult> PublishAsync(ChangePlan plan, CancellationToken token, Action? committing = null);
    /// <summary>放弃本适配器拥有的未发布计划, 不操作原工程或最终输出.</summary>
    void Discard(string planId);
}
/// <summary>普通 UI/CLI 执行的共用授权边界; MCP 只允许生成计划.</summary>
public sealed class ChangeService(IChangeWorkspace workspace, TimeProvider clock, ILogger<ChangeService>? logger = null) : IDisposable
{
    private readonly ILogger log = logger ?? NullLogger<ChangeService>.Instance;

    private readonly ConcurrentDictionary<string, (ChangePlan Plan, DateTimeOffset Expires)> plans = new();
    private readonly Lock sync = new();
    private bool busy;
    private bool disposed;
    private static readonly string[] MeasureProperties = ["description", "displayFolder", "formatString", "expression"];

    /// <summary>验证操作和长度后生成计划, 每份计划仅有效 15 分钟.</summary>
    public async Task<ChangePlan> PrepareAsync(ChangeRequest request, CancellationToken token) => await BusinessLog.RunAsync<ChangePlan>(log, "Change.Prepare", async () =>
    {
        Enter();
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.Operation is not ("measure.set" or "table.set" or "udf.set" or "calendar.create" or "period.generate"))
                throw new AppException("UNSUPPORTED_OPERATION", "当前仅支持度量值、表说明、UDF、日期表或期间指标的副本修改", 3);
            if (request.Table?.Length > 200 || string.IsNullOrWhiteSpace(request.ObjectName) || request.ObjectName.Length > 200 || request.Value is null || request.Value.Length > 30000
                || string.IsNullOrWhiteSpace(request.ProjectPath) || request.ProjectPath.Length > 2048
                || string.IsNullOrWhiteSpace(request.OutputPath) || request.OutputPath.Length > 2048)
                throw new AppException("VALIDATION_ERROR", "对象名或变更内容不合法");
            var allowed = request.Operation switch
            {
                "measure.set" => MeasureProperties,
                "table.set" => ["description"],
                "udf.set" => ["expression"],
                "calendar.create" or "period.generate" => ["config"],
                _ => []
            };
            if (!allowed.Contains(request.Property)) throw new AppException("UNSUPPORTED_PROPERTY", "此属性不在可修改白名单中", 3);
            ModelEditValueLimits.Validate(request.Operation, request.Property, request.Value);
            foreach (var expired in plans.Where(p => p.Value.Expires < clock.GetUtcNow()).Select(p => p.Key).ToArray())
                if (plans.TryRemove(expired, out _)) workspace.Discard(expired);
            if (plans.Count >= 16) throw new AppException("PLAN_LIMIT", "待处理计划过多, 请先完成或等待过期", 3);
            var plan = await workspace.PrepareAsync(request, token);
            if (token.IsCancellationRequested)
            {
                workspace.Discard(plan.Id);
                token.ThrowIfCancellationRequested();
            }
            plans[plan.Id] = (plan, clock.GetUtcNow().AddMinutes(15));
            return plan;
        }
        finally { Leave(); }
    });

    /// <summary>精确批准摘要并一次性消费; 失败后必须重新预览, 不自动重放.</summary>
    public async Task<ChangeResult> ApplyAsync(string id, string approvedDigest, CancellationToken token, Action? committing = null) => await BusinessLog.RunAsync<ChangeResult>(log, "Change.Apply", async () =>
    {
        Enter();
        try
        {
            token.ThrowIfCancellationRequested();
            if (!plans.TryGetValue(id, out var stored)) throw new AppException("PLAN_NOT_FOUND", "计划不存在、已使用或属于另一会话", 3);
            if (stored.Expires < clock.GetUtcNow())
            {
                plans.TryRemove(id, out _); workspace.Discard(id);
                throw new AppException("PLAN_EXPIRED", "计划已过期, 请重新预览", 3);
            }
            if (!string.Equals(stored.Plan.Digest, approvedDigest, StringComparison.Ordinal)) throw new AppException("APPROVAL_MISMATCH", "批准与计划内容不一致", 3);
            if (!plans.TryRemove(id, out _)) throw new AppException("PLAN_USED", "计划已提交, 禁止重复应用", 3);
            return await workspace.PublishAsync(stored.Plan, token, committing);
        }
        finally { Leave(); }
    });

    /// <summary>取消未提交预览, 不撤销已发布的副本.</summary>
    public void Discard(string id) => BusinessLog.Run(log, "Change.Discard", () =>
    {
        Enter();
        try { if (plans.TryRemove(id, out _)) workspace.Discard(id); }
        finally { Leave(); }
    });

    /// <summary>拒绝并发操作堆积, 令容量、批准和清理在同一串行边界内.</summary>
    private void Enter()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (busy) throw new AppException("BUSY", "正在准备或应用计划", 3);
            busy = true;
        }
    }

    /// <summary>在途调用结束后完成延迟释放, 不在准备中删除 staging.</summary>
    private void Leave()
    {
        lock (sync)
        {
            busy = false;
            if (disposed) { plans.Clear(); workspace.Dispose(); }
        }
    }

    /// <summary>关闭后禁止新请求, 已进入的调用在退出边界清理未发布目录.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (!busy) { plans.Clear(); workspace.Dispose(); }
        }
    }
}
