// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\ConnectedModelReader.ExternalTool.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将外部工具启动端口绑定到实际发现的 Desktop 引擎, 精确核验数据库并拒绝失效目标.

using PbiUtils.Application.Connections;
using Microsoft.AnalysisServices.Tabular;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Models.Connected;

public sealed partial class ConnectedModelReader
{
    /// <summary>只使用发现服务的回环端口, 不使用调用方地址建立远端或未核验本机连接.</summary>
    /// <param name="target">已限制端口与数据库长度的启动目标, 本层再次校验.</param>
    /// <param name="token">取消发现和后续连接; 此方法本身不写入模型.</param>
    /// <returns>绑定完整 Desktop/引擎身份与预期数据库的私有句柄.</returns>
    /// <exception cref="AppException">目标已关闭、端口歧义或参数无效.</exception>
    public async Task<string> RegisterExternalToolAsync(ExternalToolLaunchTarget target, CancellationToken token)
    {
        target.Validate();
        var endpoint = ResolveExternalToolEndpoint(target, await discovery.DiscoverAsync(token));
        token.ThrowIfCancellationRequested();
        return Register(new("localhost:" + endpoint.Port, target.Database, endpoint));
    }

    /// <summary>必须唯一对应当前会话已核验的 Desktop 子引擎, 不按标题或任一可用端口回退.</summary>
    internal static PowerBiEndpoint ResolveExternalToolEndpoint(ExternalToolLaunchTarget target, IReadOnlyList<PowerBiEndpoint> endpoints)
    {
        target.Validate();
        var matches = endpoints.Where(endpoint => endpoint.Port == target.Port && endpoint.InstanceId is not null).ToArray();
        if (matches.Length != 1 || endpoints.Count(endpoint => endpoint.ProcessId == matches[0].ProcessId && endpoint.Port is not null) != 1)
            throw new AppException("EXTERNAL_TOOL_TARGET_UNAVAILABLE", "外部工具启动目标已失效, 请从 Power BI Desktop 重新打开", 3);
        return matches[0];
    }

    /// <summary>Desktop 仍只允许唯一模型, 且必须与启动数据库准确匹配; 已绑定 ID 后只接受该 ID.</summary>
    /// <param name="databases">当前只读 TOM 会话的数据库集合.</param>
    /// <param name="expected">启动时数据库名称或 ID, 首次读取后固定为真实 ID.</param>
    /// <param name="idOnly">已连接目标的后续核验只比较固定 ID.</param>
    /// <returns>唯一且匹配的实际数据库.</returns>
    /// <exception cref="AppException">无模型、多模型、名称或身份不匹配.</exception>
    internal static Database RequireExternalToolDatabase(IEnumerable<Database> databases, string expected, bool idOnly = false)
    {
        var candidates = databases.ToArray();
        if (candidates.Length != 1 || !(string.Equals(candidates[0].ID, expected, StringComparison.Ordinal)
            || !idOnly && string.Equals(candidates[0].Name, expected, StringComparison.Ordinal)))
            throw new AppException("EXTERNAL_TOOL_DATABASE_MISMATCH", "外部工具指定的数据库与当前 Desktop 模型不匹配, 请从 Power BI Desktop 重新打开", 3);
        return candidates[0];
    }

    /// <summary>元数据读取后再次核对固定数据库 ID, 同步 SDK 仅在边界取消, 不执行模型更改.</summary>
    private static Task VerifyExternalToolDatabaseAsync(Target target, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var server = new Server();
        server.Connect($"Data Source={target.Server};Connect Timeout=3;Timeout=5;Application Name=pbi-utils");
        token.ThrowIfCancellationRequested();
        RequireExternalToolDatabase(server.Databases.Cast<Database>(), target.Database, idOnly: true);
        token.ThrowIfCancellationRequested();
    }, token);
}
