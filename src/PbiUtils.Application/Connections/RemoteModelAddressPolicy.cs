// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\RemoteModelAddressPolicy.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原生远端连接与最近记录共用的地址, 端口及身份模式验证.

using System.Text.RegularExpressions;
using PbiUtils.Core;

namespace PbiUtils.Application.Connections;

/// <summary>只验证授权输入, 不联网、解析密码或写日志.</summary>
public static partial class RemoteModelAddressPolicy
{
    /// <summary>允许 SSAS 主机、固定端口、命名实例及官方 XMLA URL, 拒绝连接串注入.</summary>
    /// <param name="address">原生目标, 指定 Windows 身份仅能用于 SSAS.</param>
    /// <exception cref="AppException">目标或身份模式不合法.</exception>
    public static void Validate(RemoteModelAddress address)
    {
        if (address is null || string.IsNullOrWhiteSpace(address.Server) || address.Server.Length > 1024 || string.IsNullOrWhiteSpace(address.Database) || address.Database.Length > 200
            || address.Server.Any(c => char.IsControl(c) || c is ';' or '=' or '@') || address.Database.Any(char.IsControl))
            throw new AppException("INVALID_SERVER", "请填写服务器和数据库, 不接受连接字符串");
        var server = address.Server.Trim();
        if (server.Contains("://", StringComparison.Ordinal))
        {
            if (address.Credential is not null) throw new AppException("INVALID_AUTHENTICATION_MODE", "指定 Windows 账号仅用于 SSAS 主机, Power BI XMLA 使用组织身份");
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != "powerbi" || uri.Host != "api.powerbi.com"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal) || uri.Segments.Length != 4)
                throw new AppException("INVALID_SERVER", "XMLA 地址需为 powerbi://api.powerbi.com/...; SSAS 使用主机或主机:端口");
        }
        else
        {
            if (!SsasServerRegex().IsMatch(server)) throw new AppException("INVALID_SERVER", "服务器格式不合法");
            var parts = server.Split(':');
            if (parts.Length == 2 && (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535))
                throw new AppException("INVALID_SERVER", "SSAS 端口需为 1–65535");
        }
    }

    /// <summary>编译生成 SSAS 主机和实例规则, 不接受连接字符串.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]+(?:\\[A-Za-z0-9_-]+|:[0-9]{1,5})?$")]
    private static partial Regex SsasServerRegex();
}
