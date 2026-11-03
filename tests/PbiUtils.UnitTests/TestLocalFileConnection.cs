// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\TestLocalFileConnection.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将已有离线模型夹具接入统一文件连接测试, 不启动或模拟真实 Desktop 保存成功.

using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Application;

namespace PbiUtils.UnitTests;

/// <summary>仅供已有草稿/保存安全测试使用的假文件适配器, 继续使用原有内存或临时 PBIP 模型.</summary>
internal sealed class TestLocalFileConnection(IProjectReader reader) : IConnectedModelReader, ILocalModelFileOpener
{
    private string? selected;

    /// <summary>创建隔离连接服务, 不触及真实 Desktop 或用户文件.</summary>
    public ModelConnectionService Service() => new(this, localFiles: this);

    /// <summary>记录测试授权入口, 返回固定假进程, 不做实际文件打开.</summary>
    public Task<LocalModelFileTarget> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); selected = path;
        return Task.FromResult(new LocalModelFileTarget(path, 100, DateTime.UnixEpoch));
    }

    /// <summary>假进程不发生外部切换, 保留取消边界.</summary>
    public Task VerifyAsync(LocalModelFileTarget target, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    /// <summary>以原有夹具入口读取模型, 不提供真实 TOM 注册.</summary>
    public Task<string> RegisterLocalAsync(int processId, CancellationToken token) => Task.FromResult(selected ?? throw new InvalidOperationException());

    /// <summary>转发原有测试模型读取, 保持既有模型草稿验证范围.</summary>
    public Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => reader.ReadAsync(projectPath, cancellationToken);

    /// <summary>测试文件适配器不提供远端入口.</summary>
    public string RegisterRemote(RemoteModelAddress address) => throw new NotSupportedException();

    /// <summary>测试适配器没有真实连接资源.</summary>
    public void Forget(string id) { }

    /// <summary>释放测试授权路径.</summary>
    public void Disconnect() => selected = null;
}
