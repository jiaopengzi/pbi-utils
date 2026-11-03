// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Execution\BusinessOutcome.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按共享业务事实区分成功、确定失败与待核验, 保留已经提交的模型和工程事实.

using PbiUtils.Application.Projects;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Updates;

namespace PbiUtils.Infrastructure.Execution;

/// <summary>业务返回值的终态, 不将非异常返回或已创建工程推定为全部成功.</summary>
public sealed record BusinessOutcome(string? ErrorCode, bool NeedsVerification)
{
    /// <summary>确定失败和未知结果均为业务错误, 原始业务结果仍须完整返回.</summary>
    public bool IsError => ErrorCode is not null;

    /// <summary>仅识别共享用例的明确返回类型, 不扫描任意模型或用户 JSON 字段.</summary>
    /// <param name="result">实际业务返回值, 不含客户端自报的终态.</param>
    /// <returns>适用于所有客户端回执与响应的独立分类.</returns>
    public static BusinessOutcome From(object? result) => result switch
    {
        ProductUpdateCheckResponse { Check: { Status: "failed" } check } => new(check.ErrorCode ?? "UPDATE_CHECK_FAILED", false),
        ScaffoldResult created => FromScaffold(created),
        ProjectSnapshot project => FromProject(project),
        ChangeResult { SaveResult: { } save } change when !save.IsCompleteFor(change.ConnectionKind)
            => new(save.ErrorCode ?? "EXECUTION_NEEDS_VERIFICATION", true),
        _ => new(null, false)
    };

    /// <summary>实际回读或保存阶段未完成时保留待核验; Desktop 文件未保存遵循其原业务完成判定.</summary>
    private static BusinessOutcome FromProject(ProjectSnapshot project)
        => project.HasPendingChanges || project.SaveResult is { } saved && !saved.IsCompleteFor(project.Connection?.Kind)
            ? new(project.SaveResult?.ErrorCode ?? "EXECUTION_NEEDS_VERIFICATION", true) : new(null, false);

    /// <summary>仅已创建且已回读的确定 Git 故障可解除阻塞; 未知状态和回执失败不能降级.</summary>
    private static BusinessOutcome FromScaffold(ScaffoldResult created)
    {
        if (created.ProjectCreated && created.Verified && created.State == "published")
        {
            if (created.ErrorCode is null) return new(null, false);
            if (created.ErrorCode is "GIT_INIT_FAILED" or "GIT_NOT_FOUND" or "GIT_PROBE_FAILED" or "GIT_PROBE_TIMEOUT")
                return new(created.ErrorCode, false);
        }
        return new(created.ErrorCode ?? "EXECUTION_NEEDS_VERIFICATION", true);
    }

    /// <summary>先可靠保存业务分类, 再允许协议总结; 已知失败不伪装为未提交或未知提交.</summary>
    /// <param name="execution">当前持锁执行租约; Desktop 客户端没有重复租约.</param>
    /// <exception cref="Core.AppException">持久化失败保持原阶段供核验, 不输出成功.</exception>
    public void Finish(ExecutionJournal.Execution? execution)
    {
        if (execution is null) return;
        if (!IsError) execution.Finish(true);
        else if (NeedsVerification) execution.FinishNeedsVerification(ErrorCode!);
        else execution.FinishKnownFailure(ErrorCode!);
    }
}
