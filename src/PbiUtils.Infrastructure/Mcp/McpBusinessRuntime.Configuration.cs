// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpBusinessRuntime.Configuration.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 离线 PBIP 精确配置预览和一次应用, 复用完整基线、生成归属与文件事务.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Infrastructure.Mcp;

public sealed partial class McpBusinessRuntime
{
    private readonly Dictionary<string, ConfigurationPlan> configurationPlans = new(StringComparer.Ordinal);

    /// <summary>固定当前 PBIP 及配置, 在只读内存草稿核验真实生成结果后保存有时限的进程内计划.</summary>
    /// <returns>精确计划摘要及实际候选模型, 不代表文件或引擎提交.</returns>
    private async Task<object> PrepareConfigurationAsync(ConfigurationInput input, CancellationToken token)
    {
        foreach (var expired in configurationPlans.Where(pair => pair.Value.ExpiresAt < DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) DiscardConfiguration(expired);
        if (configurationPlans.Count >= 8) throw new AppException("PLAN_LIMIT", "已有 8 个配置预览, 请先应用或放弃", 3);
        var snapshot = await InspectAsync(input.Project, token);
        if (snapshot.BaselineId != input.Baseline) throw new AppException("STALE_BASELINE", "工程基线已变化, 请重新读取", 3);
        var prepared = await scaffold.Configuration.PrepareAsync(input.Request, snapshot, token);
        try
        {
            using var workspace = new ChangeWorkspace(logger: logs.CreateLogger<ChangeWorkspace>());
            await using var draft = await workspace.CreateDraftAsync(snapshot, token);
            await draft.ApplyAsync(prepared, token);
            await scaffold.Configuration.VerifyAsync(prepared, token);
            token.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            var expires = DateTimeOffset.UtcNow.AddMinutes(15);
            configurationPlans.Add(id, new ConfigurationPlan(snapshot, prepared, expires));
            return new { planId = id, digest = prepared.Configuration!.Digest, expiresAt = expires, snapshot.ProjectId, snapshot.BaselineId, snapshot.ProjectPath, request = prepared.Configuration.Request, model = draft.Snapshot.Model, warnings = draft.Snapshot.Warnings };
        }
        catch { prepared.Configuration?.Dispose(); throw; }
    }

    /// <summary>摘要匹配后先消费计划, 再通过共享编辑服务提交; 任何失败不得重放同一计划.</summary>
    private async Task<object> ApplyConfigurationAsync(PlanApplyInput input, CancellationToken token)
    {
        if (!configurationPlans.TryGetValue(input.PlanId, out var plan)) throw new AppException("PLAN_NOT_FOUND", "配置计划不存在、已消费或属于另一 MCP 进程", 3);
        if (plan.ExpiresAt < DateTimeOffset.UtcNow) { DiscardConfiguration(input.PlanId); throw new AppException("PLAN_EXPIRED", "配置计划已过期", 3); }
        if (!string.Equals(input.Approve, plan.Request.Configuration!.Digest, StringComparison.Ordinal)) throw new AppException("APPROVAL_MISMATCH", "批准摘要与配置计划不一致", 3);
        token.ThrowIfCancellationRequested();
        configurationPlans.Remove(input.PlanId);
        try
        {
            using var workspace = new ChangeWorkspace(logger: logs.CreateLogger<ChangeWorkspace>());
            await using var edits = new ModelEditService(new ProjectReader(), workspace, logs.CreateLogger<ModelEditService>());
            edits.Select(plan.Snapshot);
            var saved = await edits.EditConfigurationAndSaveAsync(plan.Request, token, CommitLocalWrite,
                beforeCommit: ct => scaffold.Configuration.VerifyAsync(plan.Request, ct));
            return new McpToolValue(new { input.PlanId, saved.ProjectId, saved.BaselineId, saved.ProjectPath, saved.SaveResult, saved.Warnings }, BusinessOutcome.From(saved));
        }
        finally { plan.Request.Configuration.Dispose(); }
    }

    /// <summary>只释放本进程实际拥有的配置资产; 已消费或未知身份返回 false.</summary>
    private bool DiscardConfiguration(string id)
    {
        if (!configurationPlans.Remove(id, out var plan)) return false;
        plan.Request.Configuration?.Dispose();
        return true;
    }

    /// <summary>身份、完整原基线和冻结生成结果共同绑定一次应用.</summary>
    private sealed record ConfigurationPlan(ProjectSnapshot Snapshot, ModelEditRequest Request, DateTimeOffset ExpiresAt);
}
