// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\ConnectedModelReader.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 读取 Desktop 中 PBIX 的完整模型结构和用户授权的远端模型.

using PbiUtils.Windows.Models.Statistics;
using PbiUtils.Windows.Desktop.Discovery;
using PbiUtils.Windows.Desktop;
using PbiUtils.Windows.Connections.Ssas;
using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using PbiUtils.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Microsoft.AnalysisServices.Tabular;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
namespace PbiUtils.Windows.Models.Connected;

/// <summary>以独立 TOM 连接读取模型, 本机 Desktop 编辑由共享写入通道处理, 远端保持只读.</summary>
/// <param name="files">离线工程读取能力.</param>
/// <param name="cloud">受信 Power BI Service 客户端.</param>
/// <param name="schemaReader">只读远端结构适配.</param>
/// <param name="logger">现有安全业务日志.</param>
/// <param name="fileEdits">本地工程共享编辑工作区.</param>
/// <param name="beginDesktopSync">可选宿主可见性作用域, 在提交前建立并覆盖 Desktop 同步及保存尾部, CLI 不提供.</param>
/// <param name="serviceTableData">共享容量的固定单表数据会话工厂; 仅平台测试注入, 默认使用真实 ADOMD.</param>
/// <param name="tableStatisticsSession">固定统计元数据会话工厂; 仅平台测试注入, 默认绑定当前可信目标.</param>
public sealed partial class ConnectedModelReader(IProjectReader files, IPowerBiServiceClient? cloud = null, PowerBiSchemaReader? schemaReader = null, ILogger<ConnectedModelReader>? logger = null, IModelEditWorkspace? fileEdits = null,
    Func<DesktopProcessTarget, Task<IAsyncDisposable>>? beginDesktopSync = null, Func<IPowerBiTableDataSession>? serviceTableData = null, Func<IPowerBiTableStatisticsSession>? tableStatisticsSession = null) : IConnectedModelReader, IModelEditWorkspace, IModelSaveCompletion,
    PbiUtils.Application.Models.Configuration.IModelCompatibilityUpgradeWorkspace, IModelTableDataReader, IModelTableStatisticsReader, IModelTableRowCountReader
{
    private readonly ILogger log = logger ?? NullLogger<ConnectedModelReader>.Instance;

    private sealed record Target(string Server, string Database, PowerBiEndpoint? Endpoint, string? ServiceModelId = null, RemoteWindowsCredential? Credential = null,
        bool ExternalToolDatabaseBound = false);
    private readonly ConcurrentDictionary<string, Target> targets = new();
    private readonly LocalPowerBiReader discovery = new();
    /// <summary>绑定 PID、引擎启动时间和端口, 区分已就绪无模型与仍在加载, 拒绝失效或伪造进程.</summary>
    /// <param name="processId">当前会话发现的 Desktop 进程标识.</param>
    /// <param name="token">取消发现和 Bridge 等待的令牌.</param>
    /// <returns>独立后端连接句柄; 不可用、歧义和无本地模型以安全应用错误返回.</returns>
    public async Task<string> RegisterLocalAsync(int processId, CancellationToken token)
    {
        var endpoints = await discovery.DiscoverAsync(token);
        var candidates = endpoints.Where(e => e.ProcessId == processId && e.Port is not null).ToArray();
        if (candidates.Length == 0 && endpoints.Any(e => e.ProcessId == processId && e.ErrorCode is null))
        {
            // 加载中的 Bridge 仍返回原就绪错误; 已打开文件却无本地引擎时给出可操作说明.
            try { await DesktopBridgeClient.InspectAsync(processId, token); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                token.ThrowIfCancellationRequested();
                throw new AppException("INSTANCE_UNAVAILABLE", "实例未就绪或端口不唯一, 请刷新后选择", 3);
            }
            candidates = [.. (await discovery.DiscoverAsync(token)).Where(e => e.ProcessId == processId && e.Port is not null)];
            if (candidates.Length == 0) throw DesktopModelAvailability.Error();
        }
        if (candidates.Length != 1) throw new AppException("INSTANCE_UNAVAILABLE", "实例未就绪或端口不唯一, 请刷新后选择", 3);
        return Register(new("localhost:" + candidates[0].Port, "", candidates[0]));
    }
    /// <summary>读取完成后重新确认已注册引擎的 PID、启动时间和端口, 不能以父窗口仍打开替代模型身份.</summary>
    /// <param name="id">本次连接的后端模型句柄.</param>
    /// <param name="token">切换当前模型前的取消令牌.</param>
    /// <exception cref="AppException">注册已释放或对应引擎已关闭、重启或改变监听端口.</exception>
    public async Task VerifyLocalAsync(string id, CancellationToken token)
    {
        if (!targets.TryGetValue(id, out var target) || target.Endpoint is not { } endpoint)
            throw new AppException("MODEL_DISCONNECTED", "模型连接已断开", 3);
        if (!(await discovery.DiscoverAsync(token)).Any(candidate => SameInstance(candidate, endpoint)))
            throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭或重启, 请重新连接", 3);
        if (target.Database.Length > 0)
        {
            await VerifyExternalToolDatabaseAsync(target, token);
            if (!(await discovery.DiscoverAsync(token)).Any(candidate => SameInstance(candidate, endpoint)))
                throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭或重启, 请重新连接", 3);
        }
    }
    /// <summary>允许原生授权的 SSAS 身份或官方 XMLA URL; 不接受连接串属性.</summary>
    public string RegisterRemote(RemoteModelAddress address)
    {
        ValidateRemoteAddress(address);
        var credential = address.Credential?.Copy();
        try { return Register(new(address.Server.Trim(), address.Database.Trim(), null, Credential: credential)); }
        catch { credential?.Dispose(); throw; }
    }
    /// <summary>原生窗口与适配器共用输入校验, 此步骤不联网或记录输入.</summary>
    /// <exception cref="AppException">地址、端口或身份模式不合法.</exception>
    public static void ValidateRemoteAddress(RemoteModelAddress address)
        => RemoteModelAddressPolicy.Validate(address);
    /// <summary>只注册服务目录的后端句柄, 读取时再次复核当前账号和模型权限.</summary>
    public string RegisterService(string modelId)
    {
        if (cloud is null) throw new AppException("UNSUPPORTED", "当前宿主不支持 Power BI Service", 3);
        if (modelId.Length != 32 || !Guid.TryParseExact(modelId, "N", out _)) throw new AppException("INVALID_SERVICE_MODEL", "请选择服务目录中的模型");
        return Register(new("", "", null, modelId));
    }
    /// <summary>切换成功后清除旧注册, 保证一个窗口只有一个当前模型.</summary>
    public void Retain(string? id)
    {
        foreach (var key in targets.Keys.Where(key => key != id)) Forget(key);
        foreach (var key in pendingFileSaves.Keys.Where(key => key != id).ToArray()) pendingFileSaves.Remove(key);
    }
    /// <summary>生成不含地址或凭据的宿主句柄; 有界保存本窗口目标.</summary>
    private string Register(Target target)
    {
        if (targets.Count >= 64) throw new AppException("CONNECTION_LIMIT", "连接目标过多, 请断开后重试", 3);
        var id = "model://" + Guid.NewGuid().ToString("N"); targets[id] = target; return id;
    }
    /// <summary>取消或失败后撤销本次后端目标.</summary>
    public void Forget(string id)
    {
        if (targets.TryRemove(id, out var target)) target.Credential?.Dispose();
        pendingFileSaves.Remove(id);
    }
    /// <summary>文件交给原适配器; 在线只读连接在同步 SDK 边界检查取消.</summary>
    public async Task<ProjectSnapshot> ReadAsync(string projectPath, CancellationToken cancellationToken) => await BusinessLog.RunAsync<ProjectSnapshot>(log, "ConnectedModelReader.Read", async () =>
    {
        if (!projectPath.StartsWith("model://", StringComparison.Ordinal)) return await files.ReadAsync(projectPath, cancellationToken);
        if (!targets.TryGetValue(projectPath, out var target)) throw new AppException("MODEL_DISCONNECTED", "模型连接已断开", 3);
        if (target.Endpoint is { } endpoint && !(await discovery.DiscoverAsync(cancellationToken)).Any(e => SameInstance(e, endpoint)))
            throw new AppException("POWER_BI_INSTANCE_CLOSED", "实例已关闭或重启, 请重新连接", 3);
        var service = target.ServiceModelId is not null ? await cloud!.ResolveAsync(target.ServiceModelId, cancellationToken) : null;
        var ssas = service is null && target.Endpoint is null && !target.Server.Contains("://", StringComparison.Ordinal);
        var endpointKind = service is not null ? "PowerBiService" : target.Endpoint is not null ? "Desktop" : ssas ? "Ssas" : "PowerBiXmla";
        var authentication = service is not null ? "ServiceToken" : target.Credential is not null ? "WindowsExplicit" : ssas || target.Endpoint is not null ? "WindowsCurrent" : "OrganizationCurrent";
        BusinessLog.ModelConnectionSelected(log, endpointKind, authentication);
        if (ssas) await SsasConnection.ProbeAsync(target.Server, log, cancellationToken);
        var snapshot = await Task.Run(() => WindowsNetworkIdentity.Run(ssas ? target.Credential : null, log, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // pbiazure 仅支持共享容量的架构连接, 不能交给 TOM 的完整模型读取.
            if (service?.Server.StartsWith("pbiazure://", StringComparison.Ordinal) == true)
                return (schemaReader ?? new PowerBiSchemaReader()).Read(projectPath, service, cancellationToken, target.ServiceModelId);
            using var server = new Server();
            var stage = ssas ? "SSAS 身份验证与连接" : "XMLA 连接";
            using var operation = BusinessLog.Begin(log, "PowerBi.XmlaRead");
            try
            {
                BusinessLog.ModelStage(log, stage);
                var connection = new DbConnectionStringBuilder { ["Data Source"] = service?.Server ?? target.Server, ["Connect Timeout"] = 5, ["Timeout"] = 10, ["Application Name"] = "pbi-utils", ["Persist Security Info"] = false };
                var catalog = service?.Database ?? target.Database;
                // Desktop 启动候选先连接已核验引擎, 再显式核对数据库, 不把未知 catalog 交给 SDK 猜测.
                if (catalog.Length > 0 && !ssas && target.Endpoint is null) connection["Initial Catalog"] = catalog;
                if (service is not null)
                {
                    connection["Integrated Security"] = "ClaimsToken";
                    connection["Identity Provider"] = service.IdentityProvider;
                    server.AccessToken = new Microsoft.AnalysisServices.AccessToken(service.AccessToken, service.ExpiresOn, null);
                }
                server.Connect(ssas ? SsasConnection.ConnectionString(new(target.Server, target.Database, target.Credential)) : connection.ConnectionString);
                cancellationToken.ThrowIfCancellationRequested();
                BusinessLog.ModelConnected(log, endpointKind);
                stage = "模型定位";
                BusinessLog.ModelStage(log, stage);
                if (target.Endpoint is not null && target.Database.Length > 0)
                {
                    var launchDatabase = RequireExternalToolDatabase(server.Databases.Cast<Database>(), target.Database, target.ExternalToolDatabaseBound);
                    // 固定真实 ID, 后续读取不因另一个数据库沿用相同名称而改绑.
                    var bound = target with { Database = launchDatabase.ID, ExternalToolDatabaseBound = true };
                    if (!targets.TryUpdate(projectPath, bound, target)) throw new AppException("MODEL_DISCONNECTED", "模型连接已断开", 3);
                    target = bound;
                    catalog = launchDatabase.ID;
                }
                var databases = server.Databases.Cast<Database>().Where(d => service?.DatabaseId is { Length: > 0 } databaseId ? d.ID == databaseId
                    : catalog.Length == 0 || d.ID == catalog || d.Name == catalog).ToArray();
                if (databases.Length == 0 && target.Endpoint is not null) throw DesktopModelAvailability.Error();
                if (databases.Length != 1)
                {
                    BusinessLog.ModelLookup(log, databases.Length);
                    throw new AppException("MODEL_NOT_UNIQUE", "连接已建立, 但未找到唯一可读模型, 请确认数据库名称和模型读取权限", 3);
                }
                stage = "模型元数据";
                BusinessLog.ModelStage(log, stage);
                var db = databases[0]; var m = db.Model;
                BusinessLog.ModelResolved(log, service is not null || db.ID == catalog);
                var model = ModelSnapshotMapper.Map(db, target.Endpoint is { } local ? DesktopModelName(local.Title) : service?.ModelName ?? db.Name);
                cancellationToken.ThrowIfCancellationRequested();
                BusinessLog.ModelStage(log, "MetadataCompleted");
                var accountIdentity = service?.AccountId ?? target.Credential?.Use((name, _) => name.ToUpperInvariant());
                var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((service?.Server ?? target.Server) + "|" + db.ID + "|" + target.Endpoint?.EngineStarted + "|" + accountIdentity)));
                var baseline = target.Endpoint is not null ? DesktopDefinitionFingerprint(db)
                    : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(model)));
                var kind = service is not null ? "powerbi-service" : target.Endpoint is null ? "ssas-xmla" : "desktop";
                var label = service is not null ? "Power BI Service" : target.Endpoint is null ? "SSAS / XMLA" : "Power BI Desktop";
                operation.Complete();
                return new ProjectSnapshot(projectPath, identity, baseline, label, "在线模型不含报表布局", server.Version, null, model, [], [],
                    [target.Endpoint is null ? "远端模型只读. TOM 不提供报表布局" : "本地模型修改直接提交当前连接模型, 不自动备份; 请在 Power BI Desktop 保存报表文件"], new(kind, label, InstanceId: target.Endpoint?.InstanceId, ModelId: target.ServiceModelId));
            }
            catch (AppException e) { operation.Fail(e); throw; }
            catch (OperationCanceledException e) { operation.Fail(e); throw; }
            catch (Exception e)
            {
                operation.Fail(e);
                SsasConnection.LogFailure(log, stage, e);
                cancellationToken.ThrowIfCancellationRequested();
                if (service is not null) throw PowerBiSchemaReader.Failure(stage, e, service);
                throw SsasConnection.Failure(stage, e);
            }
        }), cancellationToken);
        if (target.Endpoint is { } fileEndpoint)
        {
            var file = await discovery.ReadFileIdentityAsync(fileEndpoint, cancellationToken);
            snapshot = snapshot with { Connection = snapshot.Connection! with { FilePath = file.Path, FileIdentityStatus = file.Status, FileIdentityErrorCode = file.ErrorCode } };
        }
        return target.Endpoint is null ? snapshot : await RetryCompletedConfigurationCleanupAsync(snapshot, target, cancellationToken);
    });
    /// <summary>清除后端目标; 每次读取的 TOM 连接已由 using 释放.</summary>
    public void Disconnect()
    {
        foreach (var key in targets.Keys) Forget(key);
        pendingFileSaves.Clear();
    }
}
