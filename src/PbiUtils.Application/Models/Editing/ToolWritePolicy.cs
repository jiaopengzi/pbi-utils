// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Editing\ToolWritePolicy.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 经可信宿主确认精确工具修改提案并签发一次授权, 不包含手工保存偏好.

using PbiUtils.Core;

namespace PbiUtils.Application.Models.Editing;

/// <summary>外部工具修改专用授权策略, 手工操作不经过此策略, 提案本身不能授予写权限.</summary>
/// <param name="confirmTool">可信宿主展示实际前后差异并取得用户确认的回调, 缺失时拒绝写入.</param>
/// <param name="grantTimeProvider">授权有效期时钟, 默认使用系统时间.</param>
public sealed class ToolWritePolicy(Func<ToolWriteProposal, CancellationToken, Task<bool>>? confirmTool = null, TimeProvider? grantTimeProvider = null)
{
    /// <summary>确认精确提案并签发绑定本轮运行、工作区、来源和基线的一次授权.</summary>
    /// <param name="proposal">共享编辑用例根据真实暂存前后状态生成的提案.</param>
    /// <param name="token">确认前后的取消令牌, 取消时不签发授权.</param>
    /// <returns>仅能消费一次且有时限的精确写入授权.</returns>
    /// <exception cref="AppException">宿主不支持授权或用户拒绝时明确失败.</exception>
    /// <exception cref="OperationCanceledException">用户取消或宿主关闭.</exception>
    public async Task<ToolWriteGrant> AuthorizeAsync(ToolWriteProposal proposal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (confirmTool is null) throw new AppException("WRITE_AUTHORIZATION_UNAVAILABLE", "当前宿主不支持修改提案确认, 未写入模型", 3);
        if (!await confirmTool(proposal, token)) throw new AppException("CANCELLED", "已取消修改授权, 未写入模型", 130);
        token.ThrowIfCancellationRequested();
        return new(proposal, grantTimeProvider ?? TimeProvider.System);
    }
}
