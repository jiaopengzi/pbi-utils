// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\ExternalToolLaunchTarget.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 严格解析 Power BI 外部工具启动目标, 仅保留待发现核验的回环端口与数据库.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.Connections;

/// <summary>启动参数不是连接授权; 平台仍须发现并核验唯一 Desktop 引擎与数据库.</summary>
public sealed record ExternalToolLaunchTarget(int Port, string Database)
{
    /// <summary>解析无参数启动或两个唯一配对参数, 拒绝远端地址、附加选项和连接字符串.</summary>
    /// <param name="args">操作系统已拆分的参数, 引号由系统处理.</param>
    /// <returns>无参数返回 null; 外部工具启动返回有界目标.</returns>
    /// <exception cref="AppException">缺失、重复、未知参数或非回环地址.</exception>
    public static ExternalToolLaunchTarget? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        if (args.Length != 4) throw Invalid();
        string? server = null;
        string? database = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (args[index] == "--server" && server is null) server = args[index + 1];
            else if (args[index] == "--database" && database is null) database = args[index + 1];
            else throw Invalid();
        }
        if (server is null || database is null || server.Length > 32) throw Invalid();
        var separator = server.LastIndexOf(':');
        if (separator <= 0 || !new[] { "localhost", "127.0.0.1", "[::1]" }.Contains(server[..separator], StringComparer.OrdinalIgnoreCase)
            || !int.TryParse(server.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)) throw Invalid();
        var target = new ExternalToolLaunchTarget(port, database);
        target.Validate();
        return target;
    }

    /// <summary>再次核验本机通道反序列化目标; 不依赖客户端解析结果.</summary>
    /// <exception cref="AppException">端口、数据库长度或控制字符无效.</exception>
    public void Validate()
    {
        if (Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Database) || Database.Length > 256 || Database.Any(char.IsControl)) throw Invalid();
    }

    /// <summary>安全失败不包含调用方提供的地址、数据库或其他参数.</summary>
    private static AppException Invalid() => new("EXTERNAL_TOOL_ARGUMENTS_INVALID", "外部工具启动参数无效, 请从 Power BI Desktop 重新打开", 2);
}
