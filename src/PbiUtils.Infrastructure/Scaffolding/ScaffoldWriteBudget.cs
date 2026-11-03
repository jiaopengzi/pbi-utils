// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldWriteBudget.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 同一次创建或配置的共享输出预算, 所有写入方复用同一实例.

using PbiUtils.Core;

using PbiUtils.Infrastructure.SampleData.Model;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>所有输出写入同一预算, 达上限在下一行落盘前失败.</summary>
internal sealed class ScaffoldWriteBudget(long maximumBytes)
{
    private long used;
    /// <summary>预留本次准确 UTF-8 字节, 不预分配内存或磁盘.</summary>
    /// <param name="bytes">本次即将写入的准确字节或保守上界, 必须与其他输出共享同一预算实例.</param>
    /// <exception cref="AppException">字节数为负或总量超过本轮上限.</exception>
    internal void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > maximumBytes - used) throw new AppException("SCAFFOLD_SIZE_LIMIT", "脚手架产物超过允许的总大小", 3);
        used += bytes;
    }
    /// <summary>覆盖已计量模板元数据前释放其原字节, 随后逐块重新计量.</summary>
    /// <param name="bytes">同一实例此前已登记且即将替换的完整内容字节.</param>
    /// <exception cref="InvalidOperationException">释放量为负或超过已用量, 不允许重置预算掩盖错误.</exception>
    internal void ReleaseReplaced(long bytes)
    {
        if (bytes < 0 || bytes > used) throw new InvalidOperationException("输出预算状态不一致");
        used -= bytes;
    }
}

