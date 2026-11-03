// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\McpWriteConnectionsAdapters.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 只替代 MCP 连接测试的 Desktop、认证和网络适配器, 不替代共享业务和持久化.

using PbiUtils.Application.Models;
using PbiUtils.Application.Connections;
using PbiUtils.Application;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>受控内存模型边界, 记录精确目标和验证调用, 不宣称完成真实 Desktop 连接.</summary>
internal sealed class McpWriteConnectionsReader : IConnectedModelReader, ILocalModelFileOpener
{
    internal Dictionary<string, string> Targets { get; } = [];
    internal List<int> ProcessIds { get; } = [];
    internal List<string> OpenedFiles { get; } = [];
    internal List<string> ServiceModels { get; } = [];
    internal List<(string Server, string Database)> RemoteAddresses { get; } = [];
    internal int FileVerifications { get; private set; }
    internal int LocalVerifications { get; private set; }
    internal int Reads { get; private set; }
    internal int Disconnects { get; private set; }

    /// <summary>模拟系统文件打开, 记录宿主传入的原路径而不启动程序.</summary>
    public Task<LocalModelFileTarget> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.True(File.Exists(path));
        OpenedFiles.Add(path);
        return Task.FromResult(new LocalModelFileTarget(path, 701, DateTime.UnixEpoch));
    }

    /// <summary>验证共享连接服务确实回到文件身份核验边界.</summary>
    public Task VerifyAsync(LocalModelFileTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.Equal(OpenedFiles[^1], target.Path);
        FileVerifications++;
        return Task.CompletedTask;
    }

    /// <summary>只注册测试传入的进程标识, 返回独立模型句柄.</summary>
    public Task<string> RegisterLocalAsync(int processId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ProcessIds.Add(processId);
        return Task.FromResult(Register("desktop"));
    }

    /// <summary>核对本地注册仍存在, 记录共享服务的完整身份复核.</summary>
    public Task VerifyLocalAsync(string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.Equal("desktop", Targets[id]);
        LocalVerifications++;
        return Task.CompletedTask;
    }

    /// <summary>仅捕获非秘密服务器及数据库, 不发起网络请求.</summary>
    public string RegisterRemote(RemoteModelAddress address)
    {
        RemoteAddresses.Add((address.Server, address.Database));
        return Register("ssas");
    }

    /// <summary>仅记录当前后台目录句柄, 不读取令牌.</summary>
    public string RegisterService(string modelId)
    {
        ServiceModels.Add(modelId);
        return Register("powerbi-service");
    }

    /// <summary>给每次连接分配独立身份, 避免同名目标混淆.</summary>
    private string Register(string kind)
    {
        var id = "model://mcp-test-" + Guid.NewGuid().ToString("N");
        Targets.Add(id, kind);
        return id;
    }

    /// <summary>只返回已注册的隔离模型, 取消或缺失目标均失败.</summary>
    public Task<ProjectSnapshot> ReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Targets[path];
        Reads++;
        return Task.FromResult(new ProjectSnapshot(path, path, "fixture-baseline", "TMDL", "PBIR", "1", null,
            new("Isolated model", 1606, "en-US", [], [], [], 0, 0), [], [], [], new(kind, kind, InstanceId: kind == "desktop" ? new string('a', 64) : null)));
    }

    /// <summary>成功发布后只保留本次模型, 真实共享服务负责调用时机.</summary>
    public void Retain(string? id)
    {
        foreach (var key in Targets.Keys.Where(key => key != id).ToArray()) Targets.Remove(key);
    }

    /// <summary>失败清理只撤销本次目标.</summary>
    public void Forget(string id) => Targets.Remove(id);

    /// <summary>记录实际断开, 清空本夹具注册.</summary>
    public void Disconnect() { Disconnects++; Targets.Clear(); }
}

/// <summary>只模拟认证 SDK 和目录响应, 所有历史仍写入真实隔离存储.</summary>
internal sealed class McpWriteConnectionsCloud : IPowerBiServiceClient
{
    public PowerBiAccount? Account { get; private set; }
    public string? AccountId => Account is null ? null : "isolated-account-id";
    internal List<(string Email, string Environment)> SignIns { get; } = [];
    internal List<string> Discoveries { get; } = [];
    internal int SignOuts { get; private set; }
    internal Action? Committed { get; set; }

    /// <summary>返回固定可用云, 记录真实用例验证后的邮箱.</summary>
    public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Discoveries.Add(email);
        return Task.FromResult<IReadOnlyList<PowerBiEnvironment>>([new("GlobalCloud", "全球版")]);
    }

    /// <summary>遵守提交回调后建立测试账号, 不访问用户认证缓存.</summary>
    public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null)
    {
        token.ThrowIfCancellationRequested();
        committing?.Invoke();
        Committed?.Invoke();
        SignIns.Add((email, environmentName));
        var account = new PowerBiAccount(email, environmentName);
        Account = account;
        return Task.FromResult(account);
    }

    /// <summary>以稳定目录标识提供测试模型, 未登录必须失败.</summary>
    public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.NotNull(Account);
        return Task.FromResult<IReadOnlyList<PowerBiServiceModel>>([new("fixture-handle", "Fixture model", "Fixture workspace", null, "workspace-id", "model-id")]);
    }

    /// <summary>本测试不提供外部令牌解析, 意外调用立即失败.</summary>
    public Task<PowerBiServiceTarget> ResolveAsync(string modelId, CancellationToken token) => throw new NotSupportedException();

    /// <summary>确认提交边界后只清理测试账号, 不触碰真实登录.</summary>
    public Task SignOutAsync(CancellationToken token, Action? committing = null)
    {
        token.ThrowIfCancellationRequested();
        committing?.Invoke();
        Committed?.Invoke();
        SignOuts++;
        Account = null;
        return Task.CompletedTask;
    }
}
