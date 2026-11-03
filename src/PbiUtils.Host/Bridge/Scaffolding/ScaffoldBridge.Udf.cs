// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Scaffolding\ScaffoldBridge.Udf.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立受理只读目录与显式刷新, 不占工程写门闩, 由宿主关闭统一排空.

using System.Text.Json;
using PbiUtils.Application.Scaffolding;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Scaffolding;

public sealed partial class ScaffoldBridge
{
    /// <summary>读者可在来源更新时继续读取完整快照, 同来源更新互斥由共享存储负责; 停止接收后排空再释放来源资源.</summary>
    private async Task<object> HandleUdfCatalogAsync(string method, JsonElement input, IProgress<ScaffoldProgress>? progress, Action? committing, CancellationToken token)
    {
        int epoch;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed || stopping, this);
            epoch = generation;
            if (udfPending++ == 0) udfDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            if (method == "udf.catalogRefresh")
                return await udfCatalog.RefreshAsync(input, progress, token, () => { EnsureCurrent(epoch); committing?.Invoke(); });
            var result = await udfCatalog.ReadAsync(input, token);
            token.ThrowIfCancellationRequested();
            EnsureCurrent(epoch);
            return result;
        }
        finally
        {
            lock (sync) if (--udfPending == 0) udfDrained.TrySetResult();
        }
    }
}
