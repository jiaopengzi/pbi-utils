// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.TableRowCounts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 绑定当前模型和所选表执行显式只读精确计数, 不向网页提供自由查询或提权能力.

using PbiUtils.Application.Models;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>在同一模型门闩下统计所选表的当前账号可见行数, 全部成功后才返回.</summary>
    /// <param name="parameters">projectId、baselineId和唯一表名数组, 无路径/查询/账号参数.</param>
    /// <param name="flight">计数时限、取消及关闭排空范围.</param>
    /// <returns>所选表精确查询行数; 不把物理存储大小或权限未知项改为零.</returns>
    /// <exception cref="AppException">参数、当前模型身份、保存状态、权限或并发无效.</exception>
    private async Task<ModelTableStatisticsResult> ReadTableRowCountsAsync(JsonElement parameters, Flight flight)
    {
        if (parameters.EnumerateObject().Count() != 3 || !ReadIdentity("projectId", out var requestedProject)
            || !ReadIdentity("baselineId", out var requestedBaseline)
            || !parameters.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array
            || tables.GetArrayLength() is < 1 or > 2000
            || tables.EnumerateArray().Any(table => table.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(table.GetString()) || table.GetString()!.Length > 512 || table.GetString()!.Contains('\0')))
            throw new AppException("VALIDATION_ERROR", "精确行数统计参数无效");
        var names = tables.EnumerateArray().Select(table => table.GetString()!).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new AppException("VALIDATION_ERROR", "精确行数统计参数无效");
        if (projectPath is null || projects is null) throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
        if (tableRowCounts is null || edits is null) throw new AppException("UNSUPPORTED", "当前宿主未提供精确行数统计", 3);
        if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
        try
        {
            RequireSavedModel();
            return await edits.ExecuteReadAsync(requestedProject, requestedBaseline,
                (snapshot, token) => tableRowCounts.ReadRowCountsAsync(snapshot, names, token), flight.Cancellation.Token);
        }
        finally { projectGate.Release(); }

        /// <summary>读取宿主已签发的有界身份, 不接受空白、控制字符或连接参数.</summary>
        bool ReadIdentity(string name, out string value)
        {
            value = "";
            if (!parameters.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
            value = field.GetString()!;
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Contains('\0');
        }
    }
}
