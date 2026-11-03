// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Settings.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 分发界面对话框、设置、诊断及本地 MCP 配置, 不增加宿主权限.

using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>分发界面对话框、设置、诊断及本地 MCP 配置, 不增加宿主权限.</summary>
    /// <param name="method">属于当前业务的固定方法名.</param>
    /// <param name="parameters">来源和重复字段已经核验的参数对象.</param>
    /// <param name="flight">同一会话持有的取消、计时和提交状态.</param>
    /// <returns>业务结果; 所有错误保持原码并交回协议完成路径.</returns>
    private async Task<object?> DispatchSettingsAsync(string method, JsonElement parameters, Flight flight)
    {
        object? result = null;
        switch (method)
        {
            case "app.resetUserData":
                return await ResetUserDataAsync(parameters, flight);
            case "ui.dialogRespond":
                if (dialogs is null) throw new AppException("UNSUPPORTED", "当前宿主不支持界面对话框", 3);
                if (parameters.EnumerateObject().Count() != 3
                    || !parameters.TryGetProperty("dialogId", out var dialogId) || dialogId.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(dialogId.GetString()) || dialogId.GetString()!.Length > 64
                    || !parameters.TryGetProperty("action", out var dialogAction) || dialogAction.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(dialogAction.GetString()) || dialogAction.GetString()!.Length > 64
                    || !parameters.TryGetProperty("values", out var dialogValues) || dialogValues.ValueKind != JsonValueKind.Object)
                    throw new AppException("VALIDATION_ERROR", "对话框回复参数无效");
                // 回复使用独立控制通道, 即使原请求正等待弹窗或占有业务门闩也能完成.
                result = new { accepted = dialogs.TryRespond(dialogId.GetString()!, dialogAction.GetString()!, dialogValues) };
                break;
            case "ui.settings":
                RequireEmpty(parameters);
                result = (uiSettings ?? throw new AppException("UNSUPPORTED", "当前宿主不支持语言设置", 3)).Read();
                break;
            case "ui.saveSettings":
                if (uiSettings is null) throw new AppException("UNSUPPORTED", "当前宿主不支持语言设置", 3);
                var uiFields = parameters.EnumerateObject().ToArray();
                if (uiFields.Length is < 1 or > 3 || uiFields.Any(field => field.Name is not ("locale" or "showRecentFiles" or "daxFormatStyle")))
                    throw new AppException("VALIDATION_ERROR", "界面设置至少修改一个有效字段");
                string? uiLocale = null;
                bool? showRecentFiles = null;
                string? daxFormatStyle = null;
                if (parameters.TryGetProperty("locale", out var localeValue))
                {
                    if (localeValue.ValueKind != JsonValueKind.String) throw new AppException("VALIDATION_ERROR", "界面设置字段类型无效");
                    uiLocale = localeValue.GetString();
                }
                if (parameters.TryGetProperty("showRecentFiles", out var recentValue))
                {
                    if (recentValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new AppException("VALIDATION_ERROR", "界面设置字段类型无效");
                    showRecentFiles = recentValue.GetBoolean();
                }
                if (parameters.TryGetProperty("daxFormatStyle", out var styleValue))
                {
                    if (styleValue.ValueKind != JsonValueKind.String) throw new AppException("VALIDATION_ERROR", "界面设置字段类型无效");
                    daxFormatStyle = styleValue.GetString();
                }
                result = uiSettings.Save(new(uiLocale, showRecentFiles, daxFormatStyle), flight.Cancellation.Token, () => BeginCommit(flight));
                break;
            case "logging.settings":
                RequireEmpty(parameters);
                if (logging is null) throw new AppException("UNSUPPORTED", "当前宿主不支持日志设置", 3);
                result = logging.Read();
                break;
            case "logging.save":
                if (logging is null) throw new AppException("UNSUPPORTED", "当前宿主不支持日志设置", 3);
                result = logging.Save(Strings(parameters, ["level"], 16)["level"], flight.Cancellation.Token, () => BeginCommit(flight));
                break;
            case "logging.openDirectory":
                RequireEmpty(parameters);
                if (openLogDirectory is null) throw new AppException("UNSUPPORTED", "当前宿主不支持打开日志目录", 3);
                BeginCommit(flight);
                await openLogDirectory(CancellationToken.None);
                result = new { opened = true, isSimulation = false };
                break;
            case "mcp.configuration":
                RequireEmpty(parameters);
                var executablePath = Path.Combine(AppContext.BaseDirectory, "pbi-utils.exe");
                result = new { executablePath, readWrite = new { command = executablePath, args = new[] { "mcp", "serve", "--allow-write" } }, isSimulation = false };
                break;
            case "app.info":
                RequireEmpty(parameters);
                result = new { version = Product.Version, webView2 = runtime, hasExternalToolTarget = Volatile.Read(ref pendingExternalToolTarget) is not null };
                break;
            case "doctor":
                RequireEmpty(parameters);
                result = new { diagnostic = doctor.Execute(flight.Cancellation.Token), webView2 = runtime };
                break;
            default: throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
        return result;
    }
}
