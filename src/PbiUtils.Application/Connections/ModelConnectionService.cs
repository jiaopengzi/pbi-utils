// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\ModelConnectionService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享模型连接选择与只读快照契约.

using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Serialization;
using PbiUtils.Core;
namespace PbiUtils.Application.Connections;

/// <summary>宿主校验并绑定的目标; 临时凭据不参与序列化, 连接方法不接受网页直接传入地址或密码.</summary>
public sealed record RemoteModelAddress(string Server, string Database, [property: JsonIgnore] RemoteWindowsCredential? Credential = null) : IDisposable
{
    /// <summary>释放选择窗口交出的临时凭据; 成功目标另持独立副本.</summary>
    public void Dispose() => Credential?.Dispose();
}
/// <summary>在线只读模型适配器, 连接身份由后端持有.</summary>
public interface IConnectedModelReader : IProjectReader
{
    /// <summary>绑定经过发现的本机实例, 返回不含地址的后端身份.</summary>
    Task<string> RegisterLocalAsync(int processId, CancellationToken token);
    /// <summary>仅注册实际发现且数据库匹配的外部工具启动目标, 不支持的平台明确拒绝.</summary>
    Task<string> RegisterExternalToolAsync(ExternalToolLaunchTarget target, CancellationToken token)
        => throw new AppException("UNSUPPORTED", "当前宿主不支持外部工具启动连接", 3);
    /// <summary>复核已注册本机引擎身份, 平台适配器应拒绝父窗口不变但引擎已经重启的目标.</summary>
    Task VerifyLocalAsync(string id, CancellationToken token) => Task.CompletedTask;
    /// <summary>注册宿主校验并绑定的远端目标, 此步骤不联网.</summary>
    string RegisterRemote(RemoteModelAddress address);
    /// <summary>服务模型仅接受当前账号的后端句柄, 默认适配器明确拒绝.</summary>
    string RegisterService(string modelId) => throw new AppException("UNSUPPORTED", "当前宿主不支持 Power BI Service", 3);
    /// <summary>成功切换后仅保留当前目标; 文件工程则清除所有在线目标.</summary>
    void Retain(string? id) { }
    /// <summary>撤销失败或取消的注册, 不影响其他已选目标.</summary>
    void Forget(string id);
    /// <summary>撤销当前窗口的在线目标身份.</summary>
    void Disconnect();
}
/// <summary>所有入口复用连接流程; 失败时撤销本次注册.</summary>
public sealed class ModelConnectionService(IConnectedModelReader reader, ILogger<ModelConnectionService>? logger = null, ILocalModelFileOpener? localFiles = null)
{
    private readonly ILogger log = logger ?? NullLogger<ModelConnectionService>.Instance;

    /// <summary>按启动目标连接已核验 Desktop, 失败或取消撤销本次注册, 不回退到其他模型.</summary>
    /// <param name="target">宿主持有且仅消费一次的本机启动目标.</param>
    /// <param name="token">读取和最终身份复核的取消令牌.</param>
    /// <returns>身份与数据库均匹配的权威模型快照.</returns>
    /// <exception cref="AppException">目标失效、数据库不匹配或平台不支持.</exception>
    public Task<ProjectSnapshot> ExternalToolAsync(ExternalToolLaunchTarget target, CancellationToken token)
        => BusinessLog.RunAsync<ProjectSnapshot>(log, "ModelConnection.ExternalTool", async () =>
        {
            target.Validate();
            var snapshot = await ReadAsync(await reader.RegisterExternalToolAsync(target, token), token);
            try
            {
                await reader.VerifyLocalAsync(snapshot.ProjectPath, token);
                token.ThrowIfCancellationRequested();
                return snapshot;
            }
            catch { reader.Forget(snapshot.ProjectPath); throw; }
        });

    /// <summary>本地文件统一通过 Desktop 打开后连接, 读取后再次复核文件身份; 失败清理新注册.</summary>
    /// <param name="path">宿主选择或最近列表绑定的 PBIP/PBIX 文件, 不接受网页路径.</param>
    /// <param name="token">打开、等待和读取过程的取消令牌.</param>
    /// <returns>本地 Desktop 的权威模型快照.</returns>
    /// <exception cref="AppException">宿主不支持打开、Desktop 不可用或文件身份变化.</exception>
    public Task<ProjectSnapshot> FileAsync(string path, CancellationToken token) => BusinessLog.RunAsync<ProjectSnapshot>(log, "ModelConnection.File", async () =>
    {
        if (localFiles is null) throw new AppException("DESKTOP_OPEN_UNSUPPORTED", "当前宿主不支持通过 Power BI Desktop 打开文件", 3);
        var target = await localFiles.OpenAsync(path, token);
        var snapshot = await LocalAsync(target.ProcessId, token);
        try
        {
            await localFiles.VerifyAsync(target, token);
            await reader.VerifyLocalAsync(snapshot.ProjectPath, token);
            token.ThrowIfCancellationRequested();
            return snapshot;
        }
        catch { reader.Forget(snapshot.ProjectPath); throw; }
    });

    /// <summary>只允许选择实际发现的当前会话实例, 可绑定调用者已观察的完整引擎身份.</summary>
    /// <param name="processId">实际发现的 Desktop 进程.</param>
    /// <param name="token">取消读取和最终身份复核.</param>
    /// <param name="expectedInstanceId">MCP 必须提供的已发现实例摘要; 文件打开流程由完整文件票据另行约束.</param>
    /// <returns>身份匹配且再次复核后的权威快照.</returns>
    /// <exception cref="AppException">实例无效、已重启或身份不匹配, 失败撤销新注册.</exception>
    public async Task<ProjectSnapshot> LocalAsync(int processId, CancellationToken token, string? expectedInstanceId = null) => await BusinessLog.RunAsync<ProjectSnapshot>(log, "ModelConnection.Local", async () =>
    {
        if (processId <= 0) throw new AppException("INVALID_INSTANCE", "请选择有效实例");
        var snapshot = await ReadAsync(await reader.RegisterLocalAsync(processId, token), token);
        try
        {
            if (expectedInstanceId is not null && !string.Equals(snapshot.Connection?.InstanceId, expectedInstanceId, StringComparison.Ordinal))
                throw new AppException("POWER_BI_INSTANCE_CHANGED", "实例已变化, 请重新发现并选择准确实例", 3);
            await reader.VerifyLocalAsync(snapshot.ProjectPath, token);
            token.ThrowIfCancellationRequested();
            return snapshot;
        }
        catch { reader.Forget(snapshot.ProjectPath); throw; }
    });
    /// <summary>宿主授权的远端模型使用当前或显式 Windows 身份只读连接.</summary>
    public Task<ProjectSnapshot> RemoteAsync(RemoteModelAddress address, CancellationToken token) => BusinessLog.RunAsync<ProjectSnapshot>(log, "ModelConnection.Remote", () => ReadAsync(reader.RegisterRemote(address), token));
    /// <summary>服务句柄由后端模型列表发布, 不允许网页指定地址或令牌.</summary>
    public Task<ProjectSnapshot> ServiceAsync(string modelId, CancellationToken token) => BusinessLog.RunAsync<ProjectSnapshot>(log, "ModelConnection.Service", () =>
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 64) throw new AppException("INVALID_SERVICE_MODEL", "请选择有效的服务模型");
        return ReadAsync(reader.RegisterService(modelId), token);
    });
    /// <summary>宿主提交成功快照后撤销上一个模型的注册.</summary>
    public void Retain(string? id) => reader.Retain(id);
    /// <summary>宿主在提交前取消时撤销尚未生效的新目标.</summary>
    public void Forget(string id) => reader.Forget(id);
    /// <summary>首次读取失败或取消立即清理注册, 不积累失败目标.</summary>
    private async Task<ProjectSnapshot> ReadAsync(string id, CancellationToken token)
    {
        try { return await reader.ReadAsync(id, token); }
        catch { reader.Forget(id); throw; }
    }
    /// <summary>释放所有当前窗口的在线目标身份.</summary>
    public void Disconnect() => BusinessLog.Run(log, "ModelConnection.Disconnect", () => { reader.Disconnect(); });
}
