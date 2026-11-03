// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Udf\UdfManagementBridge.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 处理 UDF 来源和本地函数存储动作; 使用原脚手架门闩、代际与提交回调.

using System.Text.Json;
using PbiUtils.Application.Udf;
using static PbiUtils.Host.Bridge.Protocol.BridgeBusinessInput;

namespace PbiUtils.Host.Bridge.Udf;

/// <summary>只持有原来源和本地存储适配器, 不另建队列、代际、锁或关闭状态.</summary>
internal sealed class UdfManagementBridge(ILocalUdfStore? local, IUdfSourceProvider? sources)
{
    internal sealed record SourceSave(string Name, string BaseUrl, string? Id = null, string? Baseline = null, string Protocol = "git", string? GitRef = null);
    internal sealed record SourceDelete(string Id, string Baseline, bool Confirm);
    internal sealed record LocalIdentity(string Id);
    internal sealed record LocalSave(LocalUdfDefinition Definition, string? Id = null, string? Baseline = null);
    internal sealed record LocalDelete(string Id, string Baseline, bool Confirm);

    /// <summary>在原独占门闩内处理来源和本地函数动作; 删除确认、精确基线与提交时机保持.</summary>
    /// <param name="method">已受理的固定来源或本地函数方法.</param>
    /// <param name="parameters">原协议已验证的 JSON 参数.</param>
    /// <param name="committing">唯一 owner 核验当前代次后通知原 flight 的提交回调.</param>
    /// <param name="token">原请求的提交前取消令牌.</param>
    /// <returns>原存储结果; 无适配器、非法输入、冲突与取消沿原错误路径返回.</returns>
    internal async Task<object> HandleAsync(string method, JsonElement parameters, Action committing, CancellationToken token)
    {
        switch (method)
        {
            case "udf.sources":
                Empty(parameters); return new { items = await Sources().ListAsync(token) };
            case "udf.sourceSave":
                var sourceSave = Read<SourceSave>(parameters);
                if ((sourceSave.Id is null) != (sourceSave.Baseline is null)) throw Invalid();
                return await Sources().SaveAsync(sourceSave.Name, sourceSave.BaseUrl, sourceSave.Id, sourceSave.Baseline, token, committing, sourceSave.Protocol, sourceSave.GitRef);
            case "udf.sourceDelete":
                var sourceDelete = Read<SourceDelete>(parameters);
                if (!sourceDelete.Confirm) throw Invalid();
                await Sources().DeleteAsync(sourceDelete.Id, sourceDelete.Baseline, token, committing);
                return new { deleted = true };
            case "udf.localList":
                Empty(parameters); return new { items = await Local().ListAsync(token) };
            case "udf.localGet":
                return await Local().GetAsync(Read<LocalIdentity>(parameters).Id, token);
            case "udf.localSave":
                var save = Read<LocalSave>(parameters);
                return save.Id is null ? await Local().CreateAsync(save.Definition, token, committing)
                    : await Local().UpdateAsync(save.Id, save.Baseline ?? throw Invalid(), save.Definition, token, committing);
            case "udf.localDelete":
                var delete = Read<LocalDelete>(parameters);
                if (!delete.Confirm) throw Invalid();
                await Local().DeleteAsync(delete.Id, delete.Baseline, token, committing);
                return new { deleted = true };
            default: throw Unsupported();
        }
    }

    /// <summary>无本地函数适配时明确不可用.</summary>
    private ILocalUdfStore Local() => local ?? throw Unsupported();
    /// <summary>来源管理必须由宿主提供持久注册表.</summary>
    private IUdfSourceRegistry Sources() => sources?.Sources ?? throw Unsupported();
}
