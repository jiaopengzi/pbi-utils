// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Connections\PowerBiService\PowerBiCloudPolicy.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 集中定义 Power BI 云信任边界, 同一协议实现按配置选择端点与资源.

using PbiUtils.Application.Connections;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Connections.PowerBiService;

/// <summary>云间只有端点与资源差异, 认证、目录和模型协议继续由统一客户端实现.</summary>
internal sealed record PowerBiCloudPolicy(string Name, string Backend, string Resource, string ClusterSuffix, string[] Authorities)
{
    private static readonly Dictionary<string, PowerBiCloudPolicy> Policies = new(StringComparer.Ordinal)
    {
        ["GlobalCloud"] = new("GlobalCloud", "https://api.powerbi.com", "https://analysis.windows.net/powerbi/api", ".analysis.windows.net", ["https://login.microsoftonline.com/common"]),
        ["ChinaCloud"] = new("ChinaCloud", "https://api.powerbi.cn", "https://analysis.chinacloudapi.cn/powerbi/api", ".analysis.chinacloudapi.cn", ["https://login.chinacloudapi.cn/common", "https://login.partner.microsoftonline.cn/common"])
    };
    public string DisplayName => PowerBiEnvironment.Label(Name);
    /// <summary>取得固定受支持云的策略, 未知名称不能扩展信任边界.</summary>
    public static PowerBiCloudPolicy Get(string name) => Policies.TryGetValue(name, out var policy) ? policy
        : throw new AppException("INVALID_POWER_BI_ENVIRONMENT", "请选择受支持的登录环境", 3);
}
