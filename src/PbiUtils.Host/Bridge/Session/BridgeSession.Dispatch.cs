// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Dispatch.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按固定方法将请求路由至业务分发, 取消仍在会话锁内裁决.

using System.Text.Json;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>只将固定白名单交给对应业务分发, 请求身份和资源仍由当前会话拥有.</summary>
    /// <param name="method">已解析的方法名称, 未知名称返回固定错误.</param>
    /// <param name="parameters">请求存活期间有效的参数文档.</param>
    /// <param name="id">当前注册请求的身份.</param>
    /// <param name="flight">拥有取消、时限和提交阶段的当前请求.</param>
    /// <returns>真实业务结果任务; 参数、前置和执行错误交回协议统一处理.</returns>
    private Task<object?> DispatchAsync(string? method, JsonElement parameters, string id, Flight flight)
    {
        switch (method)
        {
            case "updates.status":
            case "updates.save":
            case "updates.check":
            case "updates.open":
                return DispatchUpdatesAsync(method, parameters, flight);
            case "learning.update":
            case "learning.close":
            case "learning.open":
                return DispatchLearningAsync(method, parameters, flight);
            case "mcp.connections":
            case "mcp.disconnect":
            case "mcp.setAdmission":
                return DispatchMcpConnectionsAsync(method, parameters, flight);
            case "calendar.preview":
            case "scaffold.templates":
            case "scaffold.templateDetails":
            case "scaffold.chooseOutput":
            case "scaffold.openTemplates":
            case "scaffold.openDirectory":
            case "scaffold.openDesktop":
            case "scaffold.updateDraft":
            case "scaffold.preview":
            case "scaffold.create":
            case "scaffold.discard":
            case "calendar.preferences":
            case "calendar.savePreferences":
            case "calendar.parseWorkdays":
            case "udf.catalog":
            case "udf.catalogRefresh":
            case "udf.sources":
            case "udf.sourceSave":
            case "udf.sourceDelete":
            case "udf.localList":
            case "udf.localGet":
            case "udf.localSave":
            case "udf.localDelete":
            case "configuration.context":
            case "configuration.apply":
            case "configuration.complete":
            case "configuration.discard":
                return DispatchScaffoldingAsync(method, parameters, id, flight);
            case "ui.dialogRespond":
            case "ui.settings":
            case "ui.saveSettings":
            case "logging.settings":
            case "logging.save":
            case "logging.openDirectory":
            case "app.info":
            case "mcp.configuration":
            case "app.resetUserData":
            case "doctor":
                return DispatchSettingsAsync(method, parameters, flight);
            case "powerbi.instances":
            case "powerbi.serviceStatus":
            case "powerbi.serviceSignIn":
            case "powerbi.serviceDeleteLogin":
            case "powerbi.serviceModels":
            case "powerbi.serviceRecentModels":
            case "powerbi.serviceDeleteRecentModel":
            case "powerbi.serviceSignOut":
            case "project.disconnect":
            case "remote.form":
            case "remote.mutate":
            case "project.connectLocal":
            case "project.connectExternalTool":
            case "project.connectRemote":
            case "project.connectService":
            case "project.recent":
            case "project.deleteRecent":
            case "project.open":
            case "project.openPath":
            case "project.inspect":
            case "project.openRecent":
                return DispatchConnectionsAsync(method, parameters, id, flight);
            case "dax.format":
            case "model.udfLint":
            case "model.tableRowCounts":
            case "model.exportLocation":
            case "model.openExportDirectory":
            case "model.tableStatistics":
            case "model.exportTables":
            case "model.edit":
            case "model.save":
            case "model.discard":
                return DispatchModelAsync(method, parameters, id, flight);
            case "sample.status":
            case "sample.chooseDirectory":
            case "sample.generate":
            case "sample.clear":
            case "sample.openDirectory":
                return DispatchSampleAsync(method, parameters, id, flight);
            case "request.cancel":
                if (parameters.EnumerateObject().Count() != 1 || !parameters.TryGetProperty("requestId", out var target)
                    || target.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(target.GetString()) || target.GetString()!.Length > 100
                    || target.GetString() == id) throw new AppException("VALIDATION_ERROR", "需要独立的目标 requestId");
                var accepted = Cancel(target.GetString()!, "CANCELLED");
                var result = accepted ? new { accepted = true, reason = (string?)null } : new { accepted = false, reason = (string?)"NOT_IN_FLIGHT" };
                return Task.FromResult<object?>(result);
            default: throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
    }
}
