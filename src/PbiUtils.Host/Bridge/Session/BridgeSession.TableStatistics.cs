// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.TableStatistics.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将只读表统计绑定到当前模型身份和操作门闩, 不授予任意查询能力.

using PbiUtils.Application.Models;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>读取当前模型的表统计, 与切换和写入串行, 数据统计不进入模型定义指纹.</summary>
    /// <param name="parameters">仅包含当前projectId和baselineId的严格对象.</param>
    /// <param name="flight">宿主拥有的取消和生命周期.</param>
    /// <returns>实际读取的统计, 未提供的行数或大小保持null.</returns>
    /// <exception cref="AppException">参数、目标、基线、能力或并发状态无效.</exception>
    private async Task<ModelTableStatisticsResult> ReadTableStatisticsAsync(JsonElement parameters, Flight flight)
    {
        if (parameters.EnumerateObject().Count() != 2 || !Read("projectId", out var requestedProject)
            || !Read("baselineId", out var requestedBaseline)) throw new AppException("VALIDATION_ERROR", "统计参数不正确");
        if (projectPath is null || projects is null) throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
        if (tableStatistics is null || edits is null) throw new AppException("UNSUPPORTED", "当前宿主未提供表统计", 3);
        if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
        try
        {
            RequireSavedModel();
            return await edits.ExecuteReadAsync(requestedProject, requestedBaseline,
                (snapshot, token) => tableStatistics.ReadStatisticsAsync(snapshot, token), flight.Cancellation.Token);
        }
        finally { projectGate.Release(); }

        /// <summary>仅接受非空有界身份, 不接受控制字符或网页连接地址.</summary>
        bool Read(string name, out string value)
        {
            value = "";
            if (!parameters.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
            value = field.GetString()!;
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Contains('\0');
        }
    }
}
