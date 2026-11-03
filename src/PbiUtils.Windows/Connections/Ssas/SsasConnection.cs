// FilePath    : pbi-utils\src\PbiUtils.Windows\Connections\Ssas\SsasConnection.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : SSAS TCP 可达性和安全错误诊断, 原生 Windows 凭据仅用于 SDK 连接.

using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.ComponentModel;
using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.AnalysisServices;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Connections.Ssas;

/// <summary>只处理直连 SSAS, 不改变 Desktop 和 Power BI Service 的认证协议.</summary>
public static class SsasConnection
{
    /// <summary>构造无凭据 SSPI 参数; 指定身份由外层原生网络上下文应用.</summary>
    /// <param name="address">经过原生授权并验证的 SSAS 目标.</param>
    /// <returns>仅供后端立即连接的字符串, 不得记录或回传.</returns>
    public static string ConnectionString(RemoteModelAddress address)
    {
        var builder = new DbConnectionStringBuilder
        {
            ["Data Source"] = address.Server.Trim(),
            // 先建立服务器会话, 再由 TOM 按名称或 ID 定位模型; 实测将 ID 填入 Initial Catalog 会在 SDK 握手时失败.
            ["Integrated Security"] = "SSPI",
            ["SSPI"] = "Negotiate",
            ["Protection Level"] = "Pkt Privacy",
            ["Persist Security Info"] = false,
            ["Connect Timeout"] = 5,
            ["Timeout"] = 10,
            ["Application Name"] = "pbi-utils"
        };
        return builder.ConnectionString;
    }

    /// <summary>解析默认或显式 TCP 端口; 命名实例由 SDK 的 Browser 协议定位.</summary>
    /// <param name="server">已验证的 SSAS 主机或命名实例.</param>
    /// <returns>可直接探测的主机和端口, 命名实例返回 null.</returns>
    public static (string Host, int Port)? TcpEndpoint(string server)
    {
        if (server.Contains('\\')) return null;
        var parts = server.Split(':');
        if (parts.Length == 1) return (parts[0], 2383);
        if (parts.Length == 2 && int.TryParse(parts[1], out var port) && port is > 0 and <= 65535) return (parts[0], port);
        throw new AppException("INVALID_SERVER", "SSAS 端口需为 1–65535");
    }

    /// <summary>建立并立即释放 TCP 探针, 不鉴权或读取模型; 独立超时不会伪装成用户取消.</summary>
    /// <param name="server">已验证的 SSAS 地址, 不写入日志.</param>
    /// <param name="logger">已有宿主的日志工具.</param>
    /// <param name="token">外层用户取消和执行时限.</param>
    /// <returns>探针完成的任务; 无法连接时抛出安全业务错误.</returns>
    public static async Task ProbeAsync(string server, ILogger logger, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var endpoint = TcpEndpoint(server);
        if (endpoint is null)
        {
            BusinessLog.SsasStageReason(logger, "TcpProbeSkipped", "NamedInstanceUsesSdkDiscovery");
            return;
        }
        using var operation = BusinessLog.Begin(logger, "PowerBi.SsasTcpProbe");
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        BusinessLog.SsasProbeStarted(logger);
        try
        {
            await client.ConnectAsync(endpoint.Value.Host, endpoint.Value.Port, deadline.Token);
            token.ThrowIfCancellationRequested();
            BusinessLog.SsasStage(logger, "TcpProbeCompleted");
            operation.Complete();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            BusinessLog.SsasStageReason(logger, "TcpProbeFailed", "ProbeDeadline");
            var failure = new AppException("MODEL_NETWORK_TIMEOUT", "连接超时，请检查服务器地址和网络后重试。", 3);
            operation.Fail(failure);
            throw failure;
        }
        catch (OperationCanceledException error) { operation.Fail(error); throw; }
        catch (Exception error)
        {
            LogFailure(logger, "TcpProbe", error);
            token.ThrowIfCancellationRequested();
            var failure = Failure("TCP 连接", error);
            operation.Fail(failure);
            throw failure;
        }
    }

    /// <summary>最多六层安全错误字段; 永久 Debug 点不记录异常正文、连接串或身份.</summary>
    /// <param name="logger">已有宿主的日志工具.</param>
    /// <param name="stage">调用方固定的安全阶段名.</param>
    /// <param name="error">仅提取类型和数字码的异常.</param>
    public static void LogFailure(ILogger logger, string stage, Exception error) => BusinessLog.ConnectionFailure(logger, stage, error, XmlaCodes);

    /// <summary>只提取最多六个 XMLA 数字码, 不访问 Description、Source、请求正文或错误位置.</summary>
    /// <param name="error">SDK 的操作异常或普通异常.</param>
    /// <returns>无错误集合时返回空数组.</returns>
    public static string[] XmlaCodes(Exception error) => error is OperationException operation
        ? [.. operation.Results.Cast<XmlaResult>().Take(6).SelectMany(result => result.Messages.Cast<XmlaMessage>().Take(6))
            .OfType<XmlaError>().Take(6).Select(item => $"0x{item.ErrorCode:X8}")]
        : [];

    /// <summary>按类型和数字码分类, 只返回简短操作提示; 安全异常链由调用边界写入日志.</summary>
    /// <param name="stage">固定的连接或读取阶段.</param>
    /// <param name="error">仅提取类型和数字码的异常.</param>
    /// <returns>可交给网页显示的安全业务错误.</returns>
    public static AppException Failure(string stage, Exception error)
    {
        string? code = null, reason = null;
        var depth = 0;
        for (Exception? current = error; current is not null && depth < 6; current = current.InnerException, depth++)
        {
            if (current is SocketException socket)
            {
                (code, reason) = socket.SocketErrorCode switch
                {
                    SocketError.TimedOut => ("MODEL_NETWORK_TIMEOUT", "连接超时，请检查网络后重试。"),
                    SocketError.ConnectionRefused => ("MODEL_CONNECTION_REFUSED", "无法连接服务器，请确认服务已启动。"),
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ("MODEL_SERVER_NOT_FOUND", "找不到服务器，请检查服务器地址。"),
                    SocketError.ConnectionReset => ("MODEL_HANDSHAKE_RESET", "连接被服务器中断，请核对账号和密码后重试。"),
                    _ => ("MODEL_NETWORK_FAILED", "连接失败，请检查网络后重试。")
                };
            }
            else if (current is AuthenticationException || current.HResult is unchecked((int)0x8009030C) or unchecked((int)0x8009030E)
                || current is Win32Exception { NativeErrorCode: 86 or 1326 } || current.HResult == unchecked((int)0x8007052E))
                (code, reason) = ("MODEL_AUTHENTICATION_FAILED", "身份验证失败，请检查账号和密码。");
            else if (current is Win32Exception { NativeErrorCode: 1327 or 1330 or 1331 or 1385 or 1909 } || current.HResult == unchecked((int)0x8007052F))
                (code, reason) = ("MODEL_ACCOUNT_RESTRICTED", "账号暂时无法登录，请联系管理员。");
            else if (current is UnauthorizedAccessException || current.HResult == unchecked((int)0x80070005))
                (code, reason) = ("MODEL_ACCESS_DENIED", "当前账号无法访问模型，请联系管理员。");
        }
        return new(code ?? "MODEL_CONNECTION_FAILED", reason ?? (stage.Contains("元数据", StringComparison.Ordinal)
            ? "模型读取失败，请重试或联系管理员。" : "无法连接模型，请检查连接信息后重试。"), 3);
    }
}
