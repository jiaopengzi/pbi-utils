// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.Model.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 分发显式模型编辑、保存、放弃及本地 DAX 格式化.

using PbiUtils.Application.Models;
using PbiUtils.Application.Code.Dax;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

using PbiUtils.Application.Input;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>分发显式模型编辑、保存、放弃及本地 DAX 格式化.</summary>
    /// <param name="method">属于当前业务的固定方法名.</param>
    /// <param name="parameters">来源和重复字段已经核验的参数对象.</param>
    /// <param name="id">当前请求身份, 用于阶段及事件关联.</param>
    /// <param name="flight">同一会话持有的取消、计时和提交状态.</param>
    /// <returns>业务结果; 所有错误保持原码并交回协议完成路径.</returns>
    private async Task<object?> DispatchModelAsync(string method, JsonElement parameters, string id, Flight flight)
    {
        object? result = null;
        switch (method)
        {
            case "model.exportTables":
                return await ExportTableAsync(parameters, id, flight);
            case "model.tableStatistics":
                return await ReadTableStatisticsAsync(parameters, flight);
            case "model.tableRowCounts":
                return await ReadTableRowCountsAsync(parameters, flight);
            case "model.exportLocation":
                return await ReadExportLocationAsync(parameters, flight);
            case "model.openExportDirectory":
                return await OpenExportDirectoryAsync(parameters, id, flight);
            case "dax.format":
                var daxInput = Strings(parameters, ["value", "style"], 30000);
                var daxStyle = daxInput["style"] switch
                {
                    "short" => DaxFormattingStyle.Short,
                    "long" => DaxFormattingStyle.Long,
                    _ => throw new AppException("VALIDATION_ERROR", "DAX 格式必须为 short 或 long")
                };
                result = new { expressions = DaxFormatter.FormatMeasures(StructuredInput.Parse<string[]>(daxInput["value"]), daxStyle, flight.Cancellation.Token) };
                break;
            case "model.udfLint":
                var lintInput = Strings(parameters, ["projectId", "baselineId"], 200);
                if (edits is null) throw new AppException("UNSUPPORTED", "当前宿主未提供模型分析", 3);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
                try { result = await edits.AnalyzeUdfsAsync(lintInput["projectId"], lintInput["baselineId"], flight.Cancellation.Token); }
                finally { projectGate.Release(); }
                break;
            case "model.edit":
            case "model.save":
            case "model.discard":
                if (edits is null) throw new AppException("UNSUPPORTED", "当前宿主未提供模型编辑", 3);
                if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
                try
                {
                    if (method == "model.edit")
                    {
                        var fields = Strings(parameters, ["operation", "objectName", "property", "value", "table"], 30000);
                        result = await edits.EditAndSaveAsync(new(fields["operation"], fields["objectName"], fields["property"], fields["value"], fields["table"]),
                            flight.Cancellation.Token, () => BeginModelCommit(id, flight));
                    }
                    else
                    {
                        RequireEmpty(parameters);
                        result = method == "model.discard" ? await edits.DiscardAsync(flight.Cancellation.Token)
                            : await edits.SaveAsync(flight.Cancellation.Token, () => BeginModelCommit(id, flight));
                    }
                    if (result is ProjectSnapshot savedConfiguration) configuration?.ObserveSaved(savedConfiguration);
                }
                finally { projectGate.Release(); }
                break;
            default: throw new AppException("UNKNOWN_METHOD", "未注册的方法");
        }
        return result;
    }
}
