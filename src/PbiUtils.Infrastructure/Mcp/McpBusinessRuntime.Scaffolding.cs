// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpBusinessRuntime.Scaffolding.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为MCP脚手架预览绑定客户端归属, 不借用其他客户端或CLI的持久计划.

using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Mcp;

public sealed partial class McpBusinessRuntime
{
    private string? scaffoldDigest;

    /// <summary>只有本上下文实际完成预览才能采用其摘要; 工作区继续持有完整清单与期限.</summary>
    private async Task<ScaffoldPlan> PrepareScaffoldAsync(ScaffoldInput input, CancellationToken token)
    {
        RequirePath(input.Output);
        var plan = await scaffold.Service.PreviewAsync(input.Request, input.Output, null, token);
        scaffoldDigest = plan.Digest;
        return plan;
    }

    /// <summary>客户端归属检查先于计划采用, 完整参数、基线、批准与真实提交仍由共享用例核验.</summary>
    private async Task<ScaffoldResult> CreateScaffoldAsync(ScaffoldCreateInput input, CancellationToken token)
    {
        RequirePath(input.Output);
        if (scaffoldDigest is null || input.Approve != scaffoldDigest)
            throw new AppException("PLAN_NOT_FOUND", "此客户端未持有该脚手架预览, 请先在当前客户端预览并审阅", 3);
        return await scaffold.Service.CreateAsync(input.Approve, input.Request, input.Output, null, token, CommitLocalWrite);
    }

    /// <summary>只能丢弃当前客户端拥有的未发布计划; 非法、未知或他人摘要明确失败, 不读写其他上下文的计划记录.</summary>
    private async Task<bool> DiscardScaffoldAsync(string digest, CancellationToken token)
    {
        if (scaffoldDigest is null || digest != scaffoldDigest)
            throw new AppException("INVALID_SCAFFOLD_PLAN", "此客户端未持有有效的脚手架预览, 请先在当前客户端预览", 3);
        var discarded = await scaffold.Service.DiscardAsync(digest, token);
        scaffoldDigest = null;
        return discarded;
    }
}
