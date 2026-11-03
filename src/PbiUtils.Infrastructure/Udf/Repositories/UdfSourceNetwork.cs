// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Repositories\UdfSourceNetwork.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为仓库 HTTP(S) 连接固定 DNS 地址, 按来源隔离公网 GitHub 与显式内网仓库.

using System.Net;
using System.Net.Sockets;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Udf.Repositories;

/// <summary>仅用户登记的非 GitHub 仓库允许内网, 仍拒绝回环、链路本地、保留地址与代理绕过.</summary>
public static class UdfSourceNetwork
{
    /// <summary>仅允许可路由公网地址, IPv4 映射按实际 IPv4 地址检查.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is > 0 and < 224 && bytes[0] is not (10 or 127)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2))
                && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 1 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    /// <summary>显式仓库可使用公网、RFC1918、共享地址及 IPv6 ULA, 不允许本机和元数据服务地址.</summary>
    public static bool IsRepositoryAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPublicAddress(address) || bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        return IsPublicAddress(address) || address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }

    /// <summary>直连校验后的数值地址, 不继承代理、cookie或凭据; TLS 仍核验原始主机证书.</summary>
    /// <param name="repository">规范来源, null 仅供固定 GitHub 公网端点使用.</param>
    internal static HttpClient CreateClient(Uri? repository = null) => CreateBoundClient(repository, false);

    /// <summary>标准 Git 历史仅访问精确来源主机; GitHub 固定公网 HTTPS, 不开放额外日期服务.</summary>
    internal static HttpClient CreateGitClient(Uri repository) => CreateBoundClient(repository, true);

    /// <summary>复用 DNS 数值地址直连、无代理/凭据与重定向保护; 固定快照端点与精确仓库连接隔离.</summary>
    private static HttpClient CreateBoundClient(Uri? repository, bool git) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = null,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var github = repository is null || repository.Host == "github.com";
            if (github && git ? context.DnsEndPoint.Host != "github.com" || context.DnsEndPoint.Port != 443
                : github ? context.DnsEndPoint.Host is not ("api.github.com" or "codeload.github.com") || context.DnsEndPoint.Port != 443
                : !context.DnsEndPoint.Host.Equals(repository!.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != repository.Port) throw Invalid();
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(address => github ? !IsPublicAddress(address) : !IsRepositoryAddress(address))) throw Invalid();
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("函数来源连接失败");
        }
    });

    /// <summary>错误不回显用户地址或凭据.</summary>
    private static AppException Invalid() => new("UDF_SOURCE_ADDRESS_INVALID", "函数来源连接地址不符合仓库网络安全限制");
}
