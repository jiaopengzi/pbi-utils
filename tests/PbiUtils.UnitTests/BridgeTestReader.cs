// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BridgeTestReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过真实扫描服务的可控发现边界验证桥接取消, 超时和关闭排空.

using PbiUtils.Application.Connections;
using PbiUtils.Application;

namespace PbiUtils.UnitTests;

/// <summary>仅限测试的实例发现适配器, 不访问本机进程或外部模型.</summary>
internal sealed class BridgeTestReader : IPowerBiReader
{
    /// <summary>是否等待测试显式释放或请求取消.</summary>
    public bool Wait { get; init; }
    /// <summary>发现调用已进入, 用于确定取消发生在实际业务期间.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>释放发现调用, 不以固定延时猜测请求进度.</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>记录传入业务令牌是否实际接受取消.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>等待可控门闩并传播取消, 完成后返回空的真实发现结果.</summary>
    /// <param name="cancellationToken">来自桥接或业务调用的取消令牌.</param>
    /// <returns>测试中没有可连接的本机实例.</returns>
    /// <exception cref="OperationCanceledException">请求被取消或超时.</exception>
    public async Task<IReadOnlyList<PowerBiEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Wait) await Gate.Task.WaitAsync(cancellationToken);
            return [];
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }
    }

    /// <summary>空发现不应触发读取; 误调用时立即暴露测试配置错误.</summary>
    /// <param name="endpoint">不应出现的候选实例.</param>
    /// <param name="cancellationToken">调用令牌.</param>
    /// <returns>本夹具不提供模型读取结果.</returns>
    /// <exception cref="InvalidOperationException">扫描错误地读取了不存在的候选.</exception>
    public Task<IReadOnlyList<PowerBiModel>> ReadAsync(PowerBiEndpoint endpoint, CancellationToken cancellationToken)
        => throw new InvalidOperationException("空发现不能读取模型");
}
