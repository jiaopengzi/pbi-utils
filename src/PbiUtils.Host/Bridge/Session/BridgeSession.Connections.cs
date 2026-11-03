// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Connections.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 分发本机、云和远端模型连接与最近记录, 保持当前工作区唯一身份.

using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>分发本机、云和远端模型连接与最近记录, 保持当前工作区唯一身份.</summary>
    /// <param name="method">属于当前业务的固定方法名.</param>
    /// <param name="parameters">来源和重复字段已经核验的参数对象.</param>
    /// <param name="id">当前请求身份, 用于阶段及事件关联.</param>
    /// <param name="flight">同一会话持有的取消、计时和提交状态.</param>
    /// <returns>业务结果; 所有错误保持原码并交回协议完成路径.</returns>
    private async Task<object?> DispatchConnectionsAsync(string method, JsonElement parameters, string id, Flight flight)
    {
        object? result = null;
        switch (method)
        {
            case "powerbi.instances":
                RequireEmpty(parameters);
                if (powerBi is null) throw new AppException("POWER_BI_UNSUPPORTED", "当前宿主不支持本机 Power BI 发现", 3);
                result = await powerBi.ExecuteAsync(flight.Cancellation.Token);
                break;
            case "powerbi.serviceStatus":
                RequireEmpty(parameters);
                if (cloudConnection is null) throw new AppException("UNSUPPORTED", "当前宿主不支持 Power BI Service", 3);
                var serviceStatus = await cloudConnection.StatusAsync(flight.Cancellation.Token);
                result = new { account = serviceStatus.Account, recentLogins = serviceStatus.RecentLogins, storageWarning = serviceStatus.StorageWarning, isSimulation = false };
                break;
            case "powerbi.serviceSignIn":
            case "powerbi.serviceDeleteLogin":
            case "powerbi.serviceModels":
            case "powerbi.serviceRecentModels":
            case "powerbi.serviceDeleteRecentModel":
            case "powerbi.serviceSignOut":
                var loginRequest = method == "powerbi.serviceSignIn" ? ServiceLoginRequest(parameters) : null;
                var loginId = method == "powerbi.serviceDeleteLogin" ? Strings(parameters, ["id"], 64)["id"] : null;
                var deleteRecentModel = method == "powerbi.serviceDeleteRecentModel" ? RecentDeletionRequest(parameters) : null;
                if (loginRequest is null && loginId is null && deleteRecentModel is null) RequireEmpty(parameters);
                if (cloudConnection is null) throw new AppException("UNSUPPORTED", "当前宿主不支持 Power BI Service", 3);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "连接操作正在执行", 3);
                try
                {
                    if (method == "powerbi.serviceSignIn")
                    {
                        if (cloudConnection.Account is not null) throw new AppException("POWER_BI_SIGN_OUT_REQUIRED", "请先退出当前账号, 再选择邮箱和登录环境", 3);
                        var loginResult = await cloudConnection.SignInAsync(loginRequest!, flight.Cancellation.Token, () => BeginCommit(flight),
                            phase => Send(new { protocolVersion = 1, requestPhase = new { requestId = id, phase } }));
                        result = new { account = loginResult.Account, environments = loginResult.Environments, recentLogins = loginResult.RecentLogins, isSimulation = false };
                    }
                    else if (method == "powerbi.serviceDeleteLogin")
                    {
                        BeginCommit(flight);
                        await cloudConnection.DeleteLoginAsync(loginId!, flight.Cancellation.Token);
                        var currentStatus = await cloudConnection.StatusAsync(flight.Cancellation.Token);
                        result = new { account = currentStatus.Account, recentLogins = currentStatus.RecentLogins, storageWarning = currentStatus.StorageWarning, isSimulation = false };
                    }
                    else if (method == "powerbi.serviceModels") result = await cloudConnection.ModelsAsync(flight.Cancellation.Token);
                    else if (method == "powerbi.serviceRecentModels") result = await cloudConnection.RecentModelsAsync(flight.Cancellation.Token);
                    else if (method == "powerbi.serviceDeleteRecentModel") result = await cloudConnection.DeleteRecentModelAsync(deleteRecentModel!.Id, flight.Cancellation.Token, () => BeginCommit(flight));
                    else
                    {
                        await cloudConnection.SignOutAsync(flight.Cancellation.Token, () => BeginCommit(flight));
                        if (connectionKind == "powerbi-service") ClearProject(flight);
                        var signedOut = await cloudConnection.StatusAsync(CancellationToken.None);
                        result = new { account = signedOut.Account, recentLogins = signedOut.RecentLogins, storageWarning = signedOut.StorageWarning, isSimulation = false };
                    }
                }
                finally { projectGate.Release(); }
                break;
            case "project.disconnect":
                RequireEmpty(parameters);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "工程操作正在执行", 3);
                try { ClearProject(flight); result = new { disconnected = true }; }
                finally { projectGate.Release(); }
                break;
            case "remote.form":
                RequireEmpty(parameters);
                result = (remoteForm ?? throw new AppException("UNSUPPORTED", "当前宿主不支持远端连接表单", 3)).Read(flight.Cancellation.Token);
                break;
            case "remote.mutate":
                var mutation = Strings(parameters, ["formId", "action", "recordId"], 64);
                // 与关闭按相同锁顺序裁决, 取消先到不改存储, 提交先到排空后报告实际结果.
                lock (sync)
                    result = (remoteForm ?? throw new AppException("UNSUPPORTED", "当前宿主不支持远端连接表单", 3))
                        .Mutate(mutation["formId"], mutation["action"], mutation["recordId"], () => BeginCommit(flight), flight.Cancellation.Token);
                break;
            case "project.connectExternalTool":
                RequireEmpty(parameters);
                RequireSavedModel();
                if (flight.IsMcp) throw new AppException("GUI_ONLY", "外部工具启动连接仅允许所属桌面窗口", 3);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "工程操作正在执行", 3);
                try
                {
                    // 消费发生在任何连接尝试之前; 失败、取消或重载不得重放原启动目标.
                    var launch = Interlocked.Exchange(ref pendingExternalToolTarget, null)
                        ?? throw new AppException("EXTERNAL_TOOL_TARGET_UNAVAILABLE", "外部工具启动目标已失效, 请从 Power BI Desktop 重新打开", 3);
                    if (connections is null) throw new AppException("UNSUPPORTED", "当前宿主不支持模型连接", 3);
                    var launched = await connections.ExternalToolAsync(launch, flight.Cancellation.Token);
                    try { PublishProject(launched, flight); result = launched; }
                    catch { connections.Forget(launched.ProjectPath); throw; }
                }
                finally { projectGate.Release(); }
                break;
            case "project.connectLocal":
            case "project.connectRemote":
            case "project.connectService":
                RequireSavedModel();
                if (connections is null) throw new AppException("UNSUPPORTED", "当前宿主不支持模型连接", 3);
                var pid = 0;
                string? expectedInstanceId = null;
                if (method == "project.connectLocal")
                {
                    if (parameters.EnumerateObject().Any(property => property.Name is not ("processId" or "instanceId"))
                        || !parameters.TryGetProperty("processId", out var processId) || processId.ValueKind != JsonValueKind.String
                        || processId.GetString()!.Length > 12 || !int.TryParse(processId.GetString(), out pid) || pid <= 0)
                        throw new AppException("INVALID_INSTANCE", "实例 ID 无效");
                    if (parameters.TryGetProperty("instanceId", out var instanceId))
                    {
                        if (instanceId.ValueKind != JsonValueKind.String || instanceId.GetString() is not { Length: 64 } value
                            || value.Any(character => !char.IsAsciiHexDigitLower(character)))
                            throw new AppException("INVALID_INSTANCE", "实例身份无效, 请重新发现");
                        expectedInstanceId = value;
                    }
                    if (flight.IsMcp && expectedInstanceId is null) throw new AppException("INVALID_INSTANCE", "MCP 连接须提供已发现的实例身份");
                }
                var serviceModelId = method == "project.connectService" ? Strings(parameters, ["modelId"], 64)["modelId"] : null;
                string? remoteFormId = null;
                JsonElement remoteValues = default;
                if (method == "project.connectRemote")
                {
                    if (parameters.EnumerateObject().Count() != 2 || !parameters.TryGetProperty("formId", out var formId)
                        || formId.ValueKind != JsonValueKind.String || formId.GetString()!.Length is not 64
                        || !parameters.TryGetProperty("values", out remoteValues) || remoteValues.ValueKind != JsonValueKind.Object)
                        throw new AppException("VALIDATION_ERROR", "远端连接参数无效");
                    remoteFormId = formId.GetString();
                }
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "工程操作正在执行", 3);
                try
                {
                    using var address = method == "project.connectRemote"
                        ? (remoteForm ?? throw new AppException("UNSUPPORTED", "当前宿主不支持远端连接表单", 3)).Submit(remoteFormId!, remoteValues, flight.Cancellation.Token, () => BeginCommit(flight)) : null;
                    if (method == "project.connectRemote")
                    {
                        BusinessLog.RemoteConnectionSubmitted(log, timeout);
                        Send(new { protocolVersion = 1, requestPhase = new { requestId = id, phase = "connecting" } });
                    }
                    var recentServiceConnection = method == "project.connectService" ? cloudConnection?.CaptureConnection(serviceModelId!) : null;
                    var snapshot = method == "project.connectService" ? await connections.ServiceAsync(serviceModelId!, flight.Cancellation.Token)
                        : method == "project.connectLocal" ? await connections.LocalAsync(pid, flight.Cancellation.Token, expectedInstanceId)
                        : await connections.RemoteAsync(address!, flight.Cancellation.Token);
                    try { PublishProject(snapshot, flight); result = snapshot; }
                    catch { connections.Forget(snapshot.ProjectPath); throw; }
                    if (method == "project.connectService" && cloudConnection is not null)
                        await cloudConnection.RecordConnectionAsync(recentServiceConnection, CancellationToken.None);
                }
                finally { projectGate.Release(); }
                break;
            case "project.recent":
                RequireEmpty(parameters);
                result = recentProjects?.Load() ?? [];
                break;
            case "project.deleteRecent":
                var deleteRecent = RecentDeletionRequest(parameters);
                if (recentProjects is null) throw new AppException("UNSUPPORTED", "当前宿主未配置最近文件存储", 3);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "连接操作正在执行", 3);
                try { result = new RecentRecordDeletionResult(recentProjects.Delete(deleteRecent.Id, flight.Cancellation.Token, () => BeginCommit(flight))); }
                finally { projectGate.Release(); }
                break;
            case "project.open":
            case "project.openPath":
            case "project.inspect":
            case "project.openRecent":
                if (method != "project.inspect") RequireSavedModel();
                string? recentPath = null;
                if (method == "project.openRecent")
                    recentPath = recentProjects?.Resolve(Strings(parameters, ["id"], 64)["id"]) ?? throw new AppException("RECENT_PROJECT_NOT_FOUND", "最近工程不可用", 3);
                else if (method == "project.openPath")
                {
                    // 仅已核验本机 MCP 可传受限路径, 网页仍只能通过原生选择或可信最近记录打开.
                    if (!flight.IsMcp) throw new AppException("MCP_ONLY", "完整路径打开仅允许已核验的本机 MCP 请求", 3);
                    recentPath = Strings(parameters, ["path"], 32767)["path"];
                }
                else RequireEmpty(parameters);
                if (projects is null) throw new AppException("PROJECT_UNSUPPORTED", "当前宿主不支持工程读取", 3);
                if (method != "project.inspect" && connections is null) throw new AppException("DESKTOP_OPEN_UNSUPPORTED", "当前宿主不支持通过 Power BI Desktop 打开文件", 3);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "工程读取正在执行", 3);
                try
                {
                    var selected = (method == "project.open"
                        ? selectProject is null ? null : await selectProject(flight.Cancellation.Token)
                        : method is "project.openRecent" or "project.openPath" ? recentPath : projectPath) ?? throw new AppException(method == "project.inspect" ? "PROJECT_NOT_SELECTED" : "CANCELLED", method == "project.inspect" ? "尚未连接模型" : "未选择本地 Power BI 文件", method == "project.inspect" ? 3 : 130);
                    if (method != "project.inspect")
                        BeginDesktopConnection(id, flight);
                    var snapshot = method == "project.inspect" ? await projects.InspectAsync(selected, flight.Cancellation.Token)
                        : await connections!.FileAsync(selected, flight.Cancellation.Token);
                    try
                    {
                        if (method != "project.inspect" || !HasPendingChanges) PublishProject(snapshot, flight, method == "project.inspect" ? null : selected);
                        result = snapshot;
                    }
                    catch { if (method != "project.inspect") connections!.Forget(snapshot.ProjectPath); throw; }
                }
                finally { projectGate.Release(); }
                break;
            default: throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
        return result;
    }

    /// <summary>文件选择结束或打开新建工程时, 统一进入可取消的 Desktop 模型连接阶段.</summary>
    /// <param name="id">当前请求身份, 阶段通知只作用于该请求.</param>
    /// <param name="flight">同一会话持有的请求状态, 旧计时器在切换预算时失效.</param>
    /// <exception cref="OperationCanceledException">取消先于阶段切换受理时停止连接.</exception>
    private void BeginDesktopConnection(string id, Flight flight)
    {
        flight.Timer?.Dispose();
        flight.Timer = ArmTimeout(id, flight, timeout ?? TimeSpan.FromSeconds(180), "DesktopOpen");
        Send(new { protocolVersion = 1, requestPhase = new { requestId = id, phase = "connecting" } });
    }
}
