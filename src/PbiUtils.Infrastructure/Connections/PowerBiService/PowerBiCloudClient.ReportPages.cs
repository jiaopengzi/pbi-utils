// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Connections\PowerBiService\PowerBiCloudClient.ReportPages.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 使用当前云和现有授权读取所选语义模型同工作区关联报表的页数.

using PbiUtils.Core;
using System.Text.Json;

namespace PbiUtils.Infrastructure.Connections.PowerBiService;

public sealed partial class PowerBiCloudClient
{
    /// <summary>按真实语义模型 ID 匹配同工作区报表, 仅在完整读取所有匹配页数时返回总数.</summary>
    /// <param name="modelId">当前目录发布的模型句柄.</param>
    /// <param name="token">调用方原请求取消令牌, 不因可选读取而吞掉取消.</param>
    /// <returns>当前账号可访问的同工作区关联报表页数; 权限、时限或服务能力不足时为 null.</returns>
    /// <exception cref="AppException">无有效登录或模型句柄无效.</exception>
    public async Task<int?> ReadReportPageCountAsync(string modelId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var auth = session ?? throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "请先登录 Microsoft 工作或学校账号", 3);
        var model = RequireModel(modelId);
        var config = configuration!;
        if (!Guid.TryParse(model.DatabaseId, out var datasetId)) return null;
        var path = "v1.0/myorg/";
        if (!model.Personal)
        {
            if (!Guid.TryParse(model.View.WorkspaceId, out var workspaceId)) return null;
            path += $"groups/{workspaceId:D}/";
        }
        path += "reports";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        int? pages = null;
        try
        {
            var (accessToken, _) = await auth.TokenAsync(budget.Token);
            using var reports = await RequestAsync(HttpMethod.Get, new(config.Backend, path), accessToken, budget.Token);
            if (CompleteItems(reports.RootElement) is { } items)
            {
                var matches = items.EnumerateArray().Where(item => Guid.TryParse(Text(item, "datasetId"), out var id) && id == datasetId)
                    .Select(item => Guid.TryParse(Text(item, "id"), out var id) ? id : Guid.Empty).Distinct().Take(9).ToArray();
                if (matches.Length <= 8 && !matches.Contains(Guid.Empty))
                {
                    var counts = await Task.WhenAll(matches.Select(async report =>
                    {
                        using var response = await RequestAsync(HttpMethod.Get, new(config.Backend, $"{path}/{report:D}/pages"), accessToken, budget.Token);
                        return CompleteItems(response.RootElement)?.GetArrayLength();
                    }));
                    if (counts.All(count => count is not null)) pages = counts.Sum();
                }
            }
        }
        catch (AppException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(auth, session)) throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录状态已改变, 请重新获取模型", 3);
        return pages;
    }

    /// <summary>只采用完整数组, 分页链接不能向未验证的其他地址发送令牌或让部分结果冒充总量.</summary>
    private static JsonElement? CompleteItems(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && !root.TryGetProperty("@odata.nextLink", out _) && !root.TryGetProperty("odata.nextLink", out _)
        && root.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array ? items : null;
}
