// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.TableExport.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将选定表的批量导出绑定到当前模型、原生保存目标及同一模型操作门闩.

using PbiUtils.Application.Models.Export;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>校验当前身份后选择文件, 与连接切换及模型写入串行, 文件发布后等待唯一终态.</summary>
    /// <param name="parameters">仅含身份、表名、格式、日志开关及语言的固定参数.</param>
    /// <param name="id">当前协议请求身份.</param>
    /// <param name="flight">当前请求取消、超时与提交状态.</param>
    /// <returns>导出摘要; 不向网页返回模型数据、连接或用户凭据.</returns>
    /// <exception cref="AppException">参数无效、模型变化、来源不支持或用户取消保存选择.</exception>
    private async Task<ModelTableExportResult> ExportTableAsync(JsonElement parameters, string id, Flight flight)
    {
        var request = ParseTableExport(parameters);
        if (projectPath is null || projects is null) throw new AppException("PROJECT_NOT_SELECTED", "请先连接模型", 3);
        if (tableExports is null || chooseTableExportFile is null || edits is null)
            throw new AppException("UNSUPPORTED", "当前宿主未提供表数据导出", 3);
        if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
        try
        {
            RequireSavedModel();
            var current = await projects.InspectAsync(projectPath, flight.Cancellation.Token);
            ModelTableExportService.ValidateSnapshot(current, request);
            var destination = await chooseTableExportFile(request, flight.Cancellation.Token);
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            if (destination is null) throw new AppException("CANCELLED", "操作已取消", 130);
            flight.Timer?.Dispose();
            flight.Timer = ArmTimeout(id, flight, timeout ?? TimeSpan.FromSeconds(600), "TableExport");
            Send(new { protocolVersion = 1, requestPhase = new { requestId = id, phase = "exporting" } });
            var exported = await edits.ExecuteReadAsync(request.ProjectId, request.BaselineId,
                (snapshot, token) => tableExports.ExportAsync(snapshot.ProjectPath, request, destination, token, () => BeginModelCommit(id, flight), ReportProgress),
                flight.Cancellation.Token);
            if (exportLocations is null) return exported;
            // 文件已经发布, 目录记录失败不能把成功伪装成可重放的导出失败.
            var directory = request.Format == "csv" && request.Tables.Count > 1 ? destination : Path.GetDirectoryName(destination)!;
            var completedAtUtc = DateTimeOffset.UtcNow;
            var location = await Task.Run(() => exportLocations.Remember(directory, completedAtUtc), CancellationToken.None);
            return exported with { LocationWarning = location.Warning };

            /// <summary>仅向同一当前请求发送有序安全计数, 已取消、结束或销毁的文档不再收到进度.</summary>
            void ReportProgress(ModelTableExportProgress progress)
            {
                lock (sync)
                {
                    if (disposed || flight.Reason is not null || !flights.TryGetValue(id, out var current) || !ReferenceEquals(current, flight)) return;
                    Send(new { protocolVersion = 1, requestProgress = new { requestId = id, kind = "tableExport", progress.Stage, progress.CompletedTables, progress.TotalTables, progress.TableIndex, progress.RowsRead } });
                }
            }
        }
        finally { projectGate.Release(); }
    }

    /// <summary>严格解析固定导出参数, 不接受路径、连接地址、查询或未知字段.</summary>
    /// <param name="parameters">已经检查重复字段的参数对象.</param>
    /// <returns>类型及长度通过检查的导出请求.</returns>
    /// <exception cref="AppException">字段缺失、多余或取值不合法.</exception>
    private static ModelTableExportRequest ParseTableExport(JsonElement parameters)
    {
        if (parameters.EnumerateObject().Count() != 7
            || !ReadString("projectId", 200, out var projectId)
            || !ReadString("baselineId", 200, out var baselineId)
            || !parameters.TryGetProperty("tables", out var tables)
            || tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() is < 1 or > 2000
            || tables.EnumerateArray().Any(table => table.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(table.GetString()) || table.GetString()!.Length > 512 || table.GetString()!.Contains('\0'))
            || !parameters.TryGetProperty("rowLimit", out var rowLimit)
            || rowLimit.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number)
            || rowLimit.ValueKind == JsonValueKind.Number && (!rowLimit.TryGetInt32(out var limit) || limit < 1)
            || !ReadString("format", 4, out var format)
            || !ReadString("locale", 5, out var locale)
            || !parameters.TryGetProperty("includeLog", out var includeLog)
            || includeLog.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || format is not ("xlsx" or "csv") || locale is not ("zh-cn" or "en-us")
            || format == "csv" && includeLog.GetBoolean())
            throw new AppException("VALIDATION_ERROR", "导出参数不正确");
        var selected = tables.EnumerateArray().Select(table => table.GetString()!).ToArray();
        if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length) throw new AppException("VALIDATION_ERROR", "导出参数不正确");
        return new(projectId, baselineId, selected, format, includeLog.GetBoolean(), locale, rowLimit.ValueKind == JsonValueKind.Null ? null : rowLimit.GetInt32());

        /// <summary>读取指定名称的非空有界字符串, 字段缺失或类型错误返回 false.</summary>
        bool ReadString(string name, int maximum, out string value)
        {
            value = "";
            if (!parameters.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
            value = field.GetString()!;
            return !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
        }
    }
}
