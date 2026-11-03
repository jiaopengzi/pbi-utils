// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\DesktopBridgeClient.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 通过官方本机命名管道读取 Desktop 文件身份并重新加载 PBIP, 不暴露通用 RPC.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PbiUtils.Core;

namespace PbiUtils.Windows.Desktop;

/// <summary>只在后端持有 Desktop 进程启动身份, 模型提交与焦点保护不要求已保存文件.</summary>
public record DesktopProcessTarget(int ProcessId, DateTime Started);
/// <summary>文件操作额外绑定实际保存路径, 不由进程身份推定文件已保存.</summary>
public sealed record DesktopFileTarget(int ProcessId, DateTime Started, string Path, bool HasUnsavedChanges) : DesktopProcessTarget(ProcessId, Started);

/// <summary>官方 Desktop Bridge 的有限客户端, 不调用截图或数据查询接口.</summary>
public static partial class DesktopBridgeClient
{
    /// <summary>核实清单和当前文件状态, 不接受来自网页的 PID 或路径.</summary>
    public static async Task<DesktopFileTarget> InspectAsync(int processId, CancellationToken token)
    {
        using var process = Process.GetProcessById(processId);
        var started = process.StartTime.ToUniversalTime();
        var manifest = await ReadAsync(processId, started, "bridge.manifest", new { }, token);
        if (!manifest.TryGetProperty("methods", out var methods) || !methods.EnumerateArray().Any(m => m.GetProperty("name").GetString() == "application.state.get/v1"))
            throw new AppException("DESKTOP_BRIDGE_UNSUPPORTED", "当前 Desktop 版本不支持文件状态接口, 未执行自动保存", 3);
        var state = await ReadAsync(processId, started, "application.state.get/v1", new { client = "pbi-utils", args = new { } }, token);
        var path = state.GetProperty("currentFilePath").GetString();
        if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path)
            || System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".pbip" or ".pbir" or ".pbix"))
            throw new AppException("DESKTOP_FILE_REQUIRED", "请先在 Power BI Desktop 将报表保存为本地 PBIX/PBIP 文件", 3);
        return new(processId, started, System.IO.Path.GetFullPath(path), state.GetProperty("hasUnsavedChanges").GetBoolean());
    }

    /// <summary>每次动作前复核进程和完整文件路径, 同名报表不构成身份依据.</summary>
    public static async Task<DesktopFileTarget> VerifyAsync(DesktopFileTarget target, CancellationToken token)
    {
        var current = await InspectAsync(target.ProcessId, token);
        if (current.Started != target.Started || !current.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase))
            throw new AppException("DESKTOP_FILE_CHANGED", "Desktop 已切换报表或重启, 未操作其他文件", 3);
        return current;
    }

    /// <summary>只重新加载模型与报表定义, 不调用数据源刷新; 有未保存内容时不覆盖.</summary>
    public static async Task ReloadAsync(DesktopFileTarget target, CancellationToken token)
    {
        var current = await VerifyAsync(target, token);
        if (current.HasUnsavedChanges) throw new AppException("DESKTOP_UNSAVED_CHANGES", "Desktop 有未保存的修改, 请处理后重试同步", 3);
        var result = await CallAsync(target.ProcessId, target.Started, "file.reload/v1", new { client = "pbi-utils", args = new { reloadModelDefinition = true } }, token);
        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new AppException("DESKTOP_RELOAD_FAILED", "PBIP 已保存, 但 Desktop 未完成同步", 4);
    }

    /// <summary>Desktop 在 TOM 同步和窗口操作时短暂拒绝并发; 只对无副作用读取作有界重试.</summary>
    private static async Task<JsonElement> ReadAsync(int processId, DateTime started, string method, object parameters, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await CallAsync(processId, started, method, parameters, token); }
            catch (AppException e) when (e.Code == "DESKTOP_BRIDGE_REJECTED" && attempt < 20)
            { await Task.Delay(150, token); }
        }
    }

    /// <summary>限制本会话进程、服务端 PID、方法和响应大小; SDK/系统原文不作为用户提示.</summary>
    private static async Task<JsonElement> CallAsync(int processId, DateTime started, string method, object parameters, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(method == "file.reload/v1" ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(5));
        try
        {
            using var process = Process.GetProcessById(processId);
            using var self = Process.GetCurrentProcess();
            if (process.ProcessName != "PBIDesktop" || process.SessionId != self.SessionId || process.StartTime.ToUniversalTime() != started)
                throw new AppException("DESKTOP_INSTANCE_CHANGED", "Desktop 实例身份已变化", 3);
            await using var pipe = new NamedPipeClientStream(".", "pbi-desktop-bridge-" + processId, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var owner) || owner != processId)
                throw new AppException("DESKTOP_BRIDGE_IDENTITY", "Desktop 本地接口身份验证失败", 3);
            var id = Guid.NewGuid().ToString("N");
            var body = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters });
            await pipe.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), deadline.Token);
            await pipe.WriteAsync(body, deadline.Token); await pipe.FlushAsync(deadline.Token);
            using var doc = JsonDocument.Parse(await ReadFrameAsync(pipe, deadline.Token));
            var response = doc.RootElement;
            if (!response.TryGetProperty("id", out var resultId) || resultId.GetString() != id) throw new AppException("DESKTOP_BRIDGE_PROTOCOL", "Desktop 返回了不匹配的响应", 4);
            if (response.TryGetProperty("error", out _)) throw new AppException("DESKTOP_BRIDGE_REJECTED", "Desktop 未接受操作, 请检查是否有弹窗、在途操作或未启用外部更改重载", 3);
            return response.GetProperty("result").Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new AppException("DESKTOP_BRIDGE_TIMEOUT", "Desktop 本地接口未及时响应; 请检查 Desktop 版本、弹窗及 Bridge 设置", 3); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
        { throw new AppException("DESKTOP_BRIDGE_UNAVAILABLE", "无法访问 Desktop 本地接口; 请检查报表是否仍打开及 Desktop Bridge 是否可用", 3); }
    }

    /// <summary>按字节长度读取分片帧, 拒绝重复长度、超长头和超大正文, 不依赖一次 Read 返回全部内容.</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        List<byte> header = [];
        var one = new byte[1];
        while (header.Count < 1024)
        {
            await stream.ReadExactlyAsync(one, token); header.Add(one[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
        }
        var lines = Encoding.ASCII.GetString([.. header]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var lengths = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (header.Count >= 1024 || lengths.Length != 1 || !int.TryParse(lengths[0][15..].Trim(), out var size) || size < 2 || size > 1024 * 1024)
            throw new AppException("DESKTOP_BRIDGE_PROTOCOL", "Desktop 返回的消息长度无效", 4);
        var result = new byte[size]; await stream.ReadExactlyAsync(result, token); return result;
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int serverProcessId);
}
