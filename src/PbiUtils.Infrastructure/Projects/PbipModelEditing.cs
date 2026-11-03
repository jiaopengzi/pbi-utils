// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Projects\PbipModelEditing.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : PBIP 内存草稿创建和批量事务保存, 复用原子替换及失败恢复.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Projects;

public sealed partial class ChangeWorkspace
{
    /// <summary>读取已验证定义到内存, 在解析前后复核快照; 不创建临时副本或写文件.</summary>
    public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) => Task.Run<IModelDraft>(async () =>
    {
        if (ModelAccessPolicy.IsReadOnly(baseline) || baseline.ProjectPath.StartsWith("model://", StringComparison.Ordinal)) throw new AppException("READ_ONLY_MODEL", "当前模型只读", 3);
        if (!File.Exists(baseline.ProjectPath) || !Path.GetExtension(baseline.ProjectPath).Equals(".pbip", StringComparison.OrdinalIgnoreCase)) throw new AppException("PBIP_REQUIRED", "编辑需要具体 PBIP 入口", 3);
        await AssertBaselineAsync(baseline, token);
        var definition = baseline.Files.Where(f => Path.GetFileName(f.Path) == "definition.pbism").Single();
        var folder = Path.GetDirectoryName(Path.Combine(Path.GetDirectoryName(baseline.ProjectPath)!, definition.Path))!;
        var db = baseline.ModelFormat == "TMDL" ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(folder, "definition"))
            : TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(folder, "model.bim")));
        try
        {
            await AssertBaselineAsync(baseline, token);
            return new MemoryModelDraft(baseline, db, (edits, ct, committing) => SaveDraftAsync(baseline, edits, committing, ct));
        }
        catch { db.Dispose(); throw; }
    }, token);

    /// <summary>一次保存全部编辑, 保存阶段才创建私有 staging, 原工程通过本轮临时回滚材料逐文件替换.</summary>
    /// <param name="baseline">保留 PBIP 父目录相对文件清单的原始快照.</param>
    /// <param name="edits">已在内存克隆验证的完整编辑批次.</param>
    /// <param name="committing">进入文件替换边界时通知宿主, 此后排空而非取消.</param>
    /// <param name="token">准备及提交前复核阶段的取消令牌.</param>
    /// <returns>完整项目根发布后的 PBIP 快照, 文件清单语义保持不变.</returns>
    /// <exception cref="AppException">完整基线变化或事务发布失败.</exception>
    private Task<ProjectSnapshot> SaveDraftAsync(ProjectSnapshot baseline, IReadOnlyList<ModelEditRequest> edits, Action? committing, CancellationToken token) => Task.Run(async () =>
    {
        await AssertBaselineAsync(baseline, token);
        var first = edits[0];
        var request = new ChangeRequest(baseline.ProjectPath, PbipProjectLayout.ProjectRoot(baseline.ProjectPath), first.Operation, first.ObjectName, first.Property, first.Value, first.Table, first.PageId, true, first.Configuration);
        var plan = await PrepareCoreAsync(request, token, edits);
        try
        {
            if (plan.BaselineId != baseline.BaselineId) throw new AppException("STALE_BASELINE", "工程在编辑期间发生变化, 未覆盖; 请放弃修改后重新读取", 3);
            var preparedItem = prepared[plan.Id];
            var expected = await projects.InspectAsync(Path.Combine(preparedItem.Staging, preparedItem.Entry), token);
            var result = await PublishAsync(plan, token, committing);
            // Publish 内部已经回读并复核全部文件, 失败会恢复; 提交后不再增加可能抛错的第二次读取.
            return expected with
            {
                ProjectPath = baseline.ProjectPath,
                ProjectId = baseline.ProjectId,
                BaselineId = result.BaselineId,
                Connection = baseline.Connection,
                Warnings = [.. expected.Warnings, "当前 PBIP 文件已保存"]
            };
        }
        finally { Discard(plan.Id); }
    }, token);

    /// <summary>外部修改或未应用的 Power Query 更改不能被草稿覆盖.</summary>
    private async Task AssertBaselineAsync(ProjectSnapshot baseline, CancellationToken token)
    {
        var fresh = await projects.InspectAsync(baseline.ProjectPath, token);
        if (fresh.BaselineId != baseline.BaselineId) throw new AppException("STALE_BASELINE", "工程在编辑期间发生变化, 未覆盖; 请重新读取", 3);
        if (fresh.Warnings.Any(w => w.Contains("未应用", StringComparison.Ordinal))) throw new AppException("UNAPPLIED_CHANGES", "请先在 Desktop 处理未应用的 Power Query 更改", 3);
    }
}
