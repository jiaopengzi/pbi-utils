// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Editing\ToolWriteAuthorization.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 绑定人类确认的精确提案, 一次性权限独立于保存偏好和第三方工具文本.

using PbiUtils.Application.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Application.Models.Editing;

/// <summary>授权界面展示的真实属性变化, 空值表示对象或属性不存在.</summary>
public sealed record ToolPropertyChange(string ObjectName, string Property, string? Before, string? After);
/// <summary>后端实际校验暂存后的精确提案, 只供应用授权界面确认, 不发往提供方.</summary>
public sealed record ToolWriteProposal(string RunId, string WorkspaceId, string ConnectionKind, string BaselineId,
    ModelEditRequest Request, IReadOnlyList<ToolPropertyChange> Changes, string Digest)
{
    /// <summary>对规范化操作字段生成指纹, 对象、属性和原始值均参与绑定.</summary>
    public static string RequestDigest(ModelEditRequest request) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request with { Table = string.IsNullOrEmpty(request.Table) ? null : request.Table, PageId = string.IsNullOrEmpty(request.PageId) ? null : request.PageId }, JsonSerializerOptions.Web))));

    /// <summary>比较宿主实际操作前后的属性值, 提案不能仅使用模型自报的变更描述.</summary>
    public static ToolWriteProposal Create(string runId, string workspace, ProjectSnapshot before, ProjectSnapshot after, ModelEditRequest request)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 100 || workspace != before.ProjectId)
            throw new AppException("WRITE_AUTHORIZATION_INVALID", "修改提案与当前运行或工作区不一致", 3);
        List<ToolPropertyChange> changes = [];
        void Add(string name, string property, string? oldValue, string? newValue)
        { if (oldValue != newValue) changes.Add(new(name, property, oldValue, newValue)); }
        foreach (var key in before.Model.Measures.Select(m => (m.Table, m.Name)).Union(after.Model.Measures.Select(m => (m.Table, m.Name))))
        {
            var oldValue = before.Model.Measures.SingleOrDefault(m => (m.Table, m.Name) == key);
            var newValue = after.Model.Measures.SingleOrDefault(m => (m.Table, m.Name) == key);
            var name = $"{key.Table}[{key.Name}]";
            if (oldValue is null || newValue is null) Add(name, "对象", oldValue is null ? null : JsonSerializer.Serialize(oldValue), newValue is null ? null : JsonSerializer.Serialize(newValue));
            else
            {
                Add(name, "expression", oldValue.Expression, newValue.Expression);
                Add(name, "description", oldValue.Description, newValue.Description);
                Add(name, "formatString", oldValue.FormatString, newValue.FormatString);
                Add(name, "displayFolder", oldValue.DisplayFolder, newValue.DisplayFolder);
                Add(name, "isHidden", oldValue.IsHidden.ToString(), newValue.IsHidden.ToString());
            }
        }
        foreach (var name in before.Model.Tables.Select(t => t.Name).Union(after.Model.Tables.Select(t => t.Name)))
        {
            var oldValue = before.Model.Tables.SingleOrDefault(t => t.Name == name);
            var newValue = after.Model.Tables.SingleOrDefault(t => t.Name == name);
            if (oldValue is null || newValue is null) Add(name, "对象", oldValue is null ? null : JsonSerializer.Serialize(oldValue), newValue is null ? null : JsonSerializer.Serialize(newValue));
            else Add(name, "description", oldValue.Description, newValue.Description);
        }
        foreach (var name in before.Model.Functions.Select(f => f.Name).Union(after.Model.Functions.Select(f => f.Name)))
            Add(name, "expression", before.Model.Functions.SingleOrDefault(f => f.Name == name)?.Expression, after.Model.Functions.SingleOrDefault(f => f.Name == name)?.Expression);
        foreach (var page in after.Pages)
            foreach (var visual in page.Visuals)
            {
                var oldValue = before.Pages.SingleOrDefault(p => p.Id == page.Id)?.Visuals.SingleOrDefault(v => v.Id == visual.Id);
                Add($"{page.Id}/{visual.Id}", "position", JsonSerializer.Serialize(oldValue), JsonSerializer.Serialize(visual));
            }
        // 模板配置同时展示, 保留日期范围等快照不包含的实际生成参数.
        if (request.Property == "config") changes.Insert(0, new(request.ObjectName, "config", null, request.Value));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { runId, workspace, before.BaselineId, requestDigest = RequestDigest(request), changes }, JsonSerializerOptions.Web))));
        return new(runId, workspace, before.Connection?.Kind ?? "pbip", before.BaselineId, request, changes.AsReadOnly(), digest);
    }
}

/// <summary>构造只能由应用确认策略执行, 不能从 JSON、模型或网页批准字段反序列化取得.</summary>
public sealed class ToolWriteGrant
{
    private readonly ToolWriteProposal proposal;
    private readonly DateTimeOffset expires;
    private readonly TimeProvider clock;
    private int consumed;
    internal ToolWriteGrant(ToolWriteProposal proposal, TimeProvider clock) { this.proposal = proposal; this.clock = clock; expires = clock.GetUtcNow().AddMinutes(3); }
    /// <summary>消费一次授权; 来源、基线、运行和精确参数变化均明确拒绝.</summary>
    public void Consume(string runId, string workspace, ProjectSnapshot snapshot, ModelEditRequest request)
    {
        if (ModelAccessPolicy.IsReadOnly(snapshot)) throw new AppException("READ_ONLY_MODEL", "当前远端模型只读", 3);
        if (clock.GetUtcNow() >= expires || proposal.RunId != runId || proposal.WorkspaceId != workspace || snapshot.ProjectId != workspace
            || proposal.ConnectionKind != (snapshot.Connection?.Kind ?? "pbip") || proposal.BaselineId != snapshot.BaselineId
            || ToolWriteProposal.RequestDigest(proposal.Request) != ToolWriteProposal.RequestDigest(request))
            throw new AppException("WRITE_AUTHORIZATION_INVALID", "修改授权已失效, 请重新检查提案", 3);
        if (Interlocked.Exchange(ref consumed, 1) != 0) throw new AppException("WRITE_AUTHORIZATION_USED", "此修改授权已经使用, 不得重复提交", 3);
    }
}
