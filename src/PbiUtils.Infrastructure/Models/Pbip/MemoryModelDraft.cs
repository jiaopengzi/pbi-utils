// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Pbip\MemoryModelDraft.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 离线 PBIP 文件事务的 TOM 内存草稿, 校验成功后暂存编辑列表, 不提交在线引擎.

using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Calendar;

using PbiUtils.Infrastructure.Models;

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Models.Configuration;

namespace PbiUtils.Infrastructure.Models.Pbip;

/// <summary>离线 PBIP 编辑仅持有文件定义和参数, 文件写入只发生在编辑列表保存委托中.</summary>
/// <param name="baseline">原始权威目标与完整基线.</param>
/// <param name="database">由草稿独占并负责释放的完整私有模型.</param>
/// <param name="save">既有共享编辑列表保存入口.</param>
public sealed class MemoryModelDraft(ProjectSnapshot baseline, TOM.Database database,
    Func<IReadOnlyList<ModelEditRequest>, CancellationToken, Action?, Task<ProjectSnapshot>> save) : IModelDraft, IUdfLintDraft,
    PbiUtils.Application.Models.Configuration.ICalendarProgramDraft
{
    private TOM.Database model = database;
    private readonly List<ModelEditRequest> edits = [];
    public ProjectSnapshot Snapshot => snapshot ?? baseline;
    private ProjectSnapshot? snapshot;

    /// <summary>分析当前私有完整 TOM, 返回基线绑定结果, 不更改任何草稿状态.</summary>
    public Task<UdfLintResult> AnalyzeUdfsAsync(CancellationToken token) => Task.Run(() => UdfModelLint.Analyze(model, baseline, token), token);

    /// <summary>只读编译使用当前私有完整模型, 不改变草稿或追加任何编辑.</summary>
    public Task<PbiUtils.Application.Calendar.ScaffoldCalendarProgram> PreviewCalendarAsync(
        PbiUtils.Application.Calendar.ScaffoldCalendarOptions options, string locale, CancellationToken token) =>
        Task.Run(() => CalendarProgramPlanner.Preview(model, options, locale, token), token);

    /// <summary>克隆后原子暂存有效变更并清除历史成功回执; 失败、取消与无变化保留前态, 错误回执不清除.</summary>
    public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (edits.Count >= 128) throw new AppException("EDIT_LIMIT", "待保存修改已达上限, 请先保存", 3);
        // PBIP 的 database 可无名称; JSON 序列化产生 name:null, SDK 反序列化却要求字符串.
        // 使用 SDK 深克隆保留真实元数据, 不为绕过异常伪造数据库名称.
        var clone = model.Clone();
        try
        {
            var pages = Snapshot.Pages;
            var before = request.Operation == "configuration.apply" ? TOM.JsonSerializer.SerializeDatabase(model) : null;
            if (request.Operation == "visual.set") pages = PatchPages(pages, request);
            else ModelMutation.Apply(clone, new(baseline.ProjectPath, "memory", request.Operation, request.ObjectName, request.Property, request.Value, request.Table, request.PageId, Configuration: request.Configuration));
            if (before is not null && before == TOM.JsonSerializer.SerializeDatabase(clone)
                && request.Configuration!.Assets.All(asset => asset.BeforeHash == asset.AfterHash)) { clone.Dispose(); return; }
            var info = ModelSnapshotMapper.Map(clone, baseline.Model.Name);
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { info, pages })));
            token.ThrowIfCancellationRequested();
            var old = model; model = clone; old.Dispose();
            edits.Add(request);
            // 已完成回执只证明上一轮基线, 不能描述新草稿; 部分保存或未知提交的错误事实必须保留.
            snapshot = baseline with { Model = info, Pages = pages, BaselineId = fingerprint, HasPendingChanges = true, SaveResult = baseline.SaveResult?.ErrorCode is null ? null : baseline.SaveResult };
        }
        catch { clone.Dispose(); throw; }
    }, token);

    /// <summary>传递离线编辑列表到共享文件事务, 原始基线、备份和发布一致性由保存适配器再次复核.</summary>
    public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token) => save(edits.ToArray(), token, committing);

    /// <summary>拒绝未知页面、对象及非有限坐标; 内存更新保留其他页面和字段.</summary>
    private static PageInfo[] PatchPages(IReadOnlyList<PageInfo> pages, ModelEditRequest request)
    {
        if (!double.TryParse(request.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)
            || value < 0 || value > 10000 || (request.Property is "width" or "height" && value == 0)) throw new AppException("VALIDATION_ERROR", "坐标需为 0–10000, 宽高必须大于零");
        var page = pages.SingleOrDefault(p => p.Id == request.PageId) ?? throw new AppException("OBJECT_NOT_FOUND", "页面不存在", 3);
        var visual = page.Visuals.SingleOrDefault(v => v.Id == request.ObjectName) ?? throw new AppException("OBJECT_NOT_FOUND", "视觉对象不存在", 3);
        var changed = request.Property switch
        {
            "x" => visual with { X = value },
            "y" => visual with { Y = value },
            "width" => visual with { Width = value },
            "height" => visual with { Height = value },
            _ => throw new AppException("UNSUPPORTED_PROPERTY", "坐标属性不可修改", 3)
        };
        return [.. pages.Select(p => p == page ? p with { Visuals = p.Visuals.Select(v => v == visual ? changed : v).ToArray() } : p)];
    }
    /// <summary>释放私有模型, 关闭不保存.</summary>
    public void Dispose() { model.Dispose(); foreach (var edit in edits) edit.Configuration?.Dispose(); }
}
