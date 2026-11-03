// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Session\BridgeSession.ExportLocation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 读取宿主记录的成功导出目录, 只允许按当前记录身份打开而不接受网页路径.

using System.Text.Json;
using PbiUtils.Core;
using static PbiUtils.Host.Bridge.Protocol.BridgeRequestParameters;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Session;

public sealed partial class BridgeSession
{
    /// <summary>读取最近成功导出目录及可用性, 文件探测在线程池完成, 不授予网页文件系统能力.</summary>
    /// <param name="parameters">必须为空的固定参数.</param>
    /// <param name="flight">当前读取的取消与生命周期.</param>
    /// <returns>展示路径、记录身份及打开能力, 未记录时为空.</returns>
    /// <exception cref="AppException">宿主未提供目录记录能力.</exception>
    private async Task<ModelExportLocation> ReadExportLocationAsync(JsonElement parameters, Flight flight)
    {
        RequireEmpty(parameters);
        if (exportLocations is null) throw new AppException("UNSUPPORTED", "当前宿主未提供导出目录记录", 3);
        return await Task.Run(exportLocations.Read, flight.Cancellation.Token);
    }

    /// <summary>只打开已展示记录的同一目录, 新记录、目录失效和链接均拒绝; 启动后排空不重放.</summary>
    /// <param name="parameters">只包含记录id, 不能提供路径.</param>
    /// <param name="id">当前桥接请求身份.</param>
    /// <param name="flight">平台启动前可取消, 启动后等待唯一终态.</param>
    /// <returns>平台目录打开事实, 不改变模型或导出文件.</returns>
    /// <exception cref="AppException">记录不匹配、目录不可用或宿主能力缺失.</exception>
    private async Task<object> OpenExportDirectoryAsync(JsonElement parameters, string id, Flight flight)
    {
        var recordId = Strings(parameters, ["id"], 64)["id"];
        if (exportLocations is null || openExportDirectory is null) throw new AppException("UNSUPPORTED", "当前宿主未提供导出目录打开", 3);
        if (!await projectGate.WaitAsync(0, flight.Cancellation.Token)) throw new AppException("BUSY", "模型操作正在执行", 3);
        try
        {
            var directory = await Task.Run(() => exportLocations.Resolve(recordId), flight.Cancellation.Token);
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            BeginModelCommit(id, flight);
            await openExportDirectory(directory, CancellationToken.None);
            return new { opened = true, isSimulation = false };
        }
        finally { projectGate.Release(); }
    }
}
