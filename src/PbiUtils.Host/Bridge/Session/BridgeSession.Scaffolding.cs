// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Scaffolding.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 分发脚手架、日历预览和同目标配置, 沿用当前会话门闩和提交边界.

using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Host.Bridge.Calendar;
using PbiUtils.Application.Models;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>同步转发阶段, 不把进度排队到请求结果之后.</summary>
    private sealed class ScaffoldProgressSink(Action<ScaffoldProgress> report) : IProgress<ScaffoldProgress>
    {
        /// <summary>阶段与百分比由实际共享业务提供.</summary>
        public void Report(ScaffoldProgress value) => report(value);
    }

    /// <summary>分发脚手架、日历预览和同目标配置, 沿用当前会话门闩和提交边界.</summary>
    /// <param name="method">属于当前业务的固定方法名.</param>
    /// <param name="parameters">来源和重复字段已经核验的参数对象.</param>
    /// <param name="id">当前请求身份, 用于阶段及事件关联.</param>
    /// <param name="flight">同一会话持有的取消、计时和提交状态.</param>
    /// <returns>业务结果; 所有错误保持原码并交回协议完成路径.</returns>
    private async Task<object?> DispatchScaffoldingAsync(string method, JsonElement parameters, string id, Flight flight)
    {
        object? result = null;
        switch (method)
        {
            case "calendar.preview":
                var previewInput = CalendarPreviewInput.Parse(parameters);
                if (previewInput.Text("kind") == "template")
                {
                    result = await (scaffold ?? throw new AppException("UNSUPPORTED", "当前宿主不支持模板代码预览", 3))
                        .HandleAsync(method, parameters, null, flight.Cancellation.Token);
                }
                else
                {
                    if (configuration is null) throw new AppException("UNSUPPORTED", "当前宿主不支持模型代码预览", 3);
                    await calendarPreviewGate.WaitAsync(flight.Cancellation.Token);
                    try
                    {
                        ProjectSnapshot? previewCurrent = null;
                        var previewPath = projectPath;
                        if (edits is not null && previewPath is not null
                            && (!configuration.IsCreatedTargetId(previewInput.Text("targetId")) || configuration.SharesCurrentPath(previewPath)))
                            previewCurrent = await edits.ReadAsync(previewPath, flight.Cancellation.Token);
                        var program = await configuration.PreviewCalendarAsync(previewInput.Text("targetId"), previewInput.Text("baselineId"),
                            previewInput.Calendar, previewInput.Locale, previewCurrent, flight.Cancellation.Token);
                        if (previewCurrent is not null)
                        {
                            if (previewPath != projectPath) throw new AppException("STALE_BASELINE", "预览期间模型目标已变化", 3);
                            var fresh = await edits!.ReadAsync(previewPath!, flight.Cancellation.Token);
                            if (fresh.ProjectId != previewCurrent.ProjectId || fresh.BaselineId != previewInput.Text("baselineId") || previewPath != projectPath)
                                throw new AppException("STALE_BASELINE", "预览期间模型已变化", 3);
                        }
                        result = CalendarPreviewResult.Create(previewInput, program);
                    }
                    finally { calendarPreviewGate.Release(); }
                }
                CalendarBridgeBudget.PreviewResult(id, result, connectionRevision);
                break;
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
                if (scaffold is null) throw new AppException("UNSUPPORTED", "当前宿主未提供新建脚手架", 3);
                if (method is "scaffold.preview" or "scaffold.create" or "scaffold.chooseOutput")
                {
                    flight.Timer?.Dispose();
                    flight.Timer = ArmTimeout(id, flight, timeout ?? Timeout.InfiniteTimeSpan, "ScaffoldGeneration");
                    Send(new { protocolVersion = 1, requestPhase = new { requestId = id, phase = "generating" } });
                }
                var scaffoldSequence = 0;
                var scaffoldProgress = new ScaffoldProgressSink(value =>
                {
                    lock (sync)
                    {
                        if (!disposed && !flight.IsMcp && flights.TryGetValue(id, out var current) && ReferenceEquals(current, flight))
                            Send(new { protocolVersion = 1, @event = new OperationEvent(id, ++scaffoldSequence, "scaffoldProgress", DateTimeOffset.UtcNow.ToString("O"), value) });
                    }
                });
                var creatingConfiguration = method == "scaffold.create";
                var requiresProjectGate = creatingConfiguration || method is "scaffold.openDirectory" or "scaffold.openDesktop";
                if (requiresProjectGate && !await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
                try
                {
                    if (creatingConfiguration || method == "scaffold.openDesktop") configuration?.RequireCreatedSaved();
                    if (method == "scaffold.openDesktop")
                    {
                        RequireEmpty(parameters);
                        RequireSavedModel();
                        BeginDesktopConnection(id, flight);
                        // 只读取已保存的创建目标, 不隐式完成部分保存或待核验提交.
                        if (configuration is not null)
                        {
                            var openingContext = await configuration.ContextAsync("created", null, flight.Cancellation.Token);
                            if (openingContext.Project.HasPendingChanges || openingContext.Project.SaveResult?.ErrorCode is not null)
                                throw new AppException("UNSAVED_CHANGES", "已创建工程的保存尚未完成", 3);
                        }
                    }
                    result = await scaffold.HandleAsync(method, parameters, scaffoldProgress, flight.Cancellation.Token, () => BeginModelCommit(id, flight));
                    if (result is ScaffoldResult createdResult) configuration?.BindCreated(createdResult);
                    if (result is ScaffoldBridge.DesktopOpenResult opened)
                    {
                        try { PublishProject(opened.Project, flight, opened.EntryPath); }
                        catch { connections?.Forget(opened.Project.ProjectPath); throw; }
                    }
                }
                finally { if (requiresProjectGate) projectGate.Release(); }
                break;
            case "configuration.context":
            case "configuration.apply":
            case "configuration.complete":
            case "configuration.discard":
                if (configuration is null) throw new AppException("UNSUPPORTED", "当前宿主未提供共享模型配置", 3);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
                try
                {
                    flight.Timer?.Dispose();
                    flight.Timer = ArmTimeout(id, flight, timeout ?? Timeout.InfiniteTimeSpan, "ModelConfiguration");
                    ProjectSnapshot? currentConfiguration = null;
                    if (method == "configuration.context")
                    {
                        var configurationTarget = Strings(parameters, ["target"], 16)["target"];
                        if (configurationTarget == "current" && edits is not null && projectPath is not null)
                        {
                            currentConfiguration = await edits.ReadAsync(projectPath, flight.Cancellation.Token);
                            if (!edits.HasPendingChanges) edits.Select(currentConfiguration);
                        }
                        result = await configuration.ContextAsync(configurationTarget, currentConfiguration, flight.Cancellation.Token);
                    }
                    else
                    {
                        if (parameters.TryGetProperty("targetId", out var targetId) && targetId.ValueKind == JsonValueKind.String
                            && edits is not null && projectPath is not null
                            && (!configuration.IsCreatedTargetId(targetId.GetString()!) || configuration.SharesCurrentPath(projectPath)))
                            currentConfiguration = await edits.ReadAsync(projectPath, flight.Cancellation.Token);
                        result = method == "configuration.apply"
                            ? await configuration.ApplyAsync(parameters, currentConfiguration, edits, flight.Cancellation.Token, () => BeginModelCommit(id, flight), flight.IsMcp)
                            : await configuration.RecoverAsync(parameters, method == "configuration.discard", currentConfiguration, edits,
                                flight.Cancellation.Token, () => BeginModelCommit(id, flight));
                    }
                }
                finally { projectGate.Release(); }
                break;
            default: throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
        return result;
    }
}
