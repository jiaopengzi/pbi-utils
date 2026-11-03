// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\LocalModelFile.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以宿主授权文件和进程身份统一本地 Desktop 连接, 不泄漏平台实现.

namespace PbiUtils.Application.Connections;

/// <summary>宿主授权的本地入口及精确实例身份, 不作为网页参数或 工具 工具参数.</summary>
public sealed record LocalModelFileTarget(string Path, int ProcessId, DateTime Started, string? ReportPath = null);

/// <summary>由平台适配器打开或复用本地文件, 共享用例不依赖进程、窗口或文件系统.</summary>
public interface ILocalModelFileOpener
{
    /// <summary>通过 Desktop 打开 PBIP/PBIX 或复用同文件实例; 失败或取消不关闭用户窗口.</summary>
    /// <param name="path">原生选择或最近记录授权的文件入口.</param>
    /// <param name="token">等待就绪的取消令牌.</param>
    /// <returns>已经就绪且已验证的实例身份.</returns>
    Task<LocalModelFileTarget> OpenAsync(string path, CancellationToken token);

    /// <summary>模型读取后重新核验完整文件和进程身份, 防止等待期间切换报表或重启.</summary>
    /// <param name="target">本次打开得到的身份.</param>
    /// <param name="token">提交当前工作区前的取消令牌.</param>
    Task VerifyAsync(LocalModelFileTarget target, CancellationToken token);
}
