// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Commands\ProductUpdateCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : CLI 显式查询固定来源的软件更新, 复用共享服务且不打开浏览器或执行安装.

using PbiUtils.Application.Updates;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Commands;

/// <summary>固定只读更新命令, 业务执行由已认证共享 Host 承载.</summary>
public static class ProductUpdateCommands
{
    /// <summary>严格解析 updates check, 手动查询不受自动检查开关和24小时节流限制.</summary>
    /// <param name="args">独立命令参数, source 缺省为 GitHub, 不接受 URL.</param>
    /// <param name="service">宿主注入的同一更新用例.</param>
    /// <param name="token">传输及提交检查记录之前的取消令牌.</param>
    /// <returns>实际检查结果; 输入错误和网络故障返回稳定异常, 由 CLI 映射为非零退出码.</returns>
    public static async Task<ProductUpdateCheckResponse> RunAsync(string[] args, ProductUpdateService service, CancellationToken token)
    {
        if (args.Length < 2 || args[0] != "updates" || args[1] != "check") throw Invalid();
        var source = "github";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) throw Invalid();
            if (option == "--json") continue;
            if (option != "--source" || ++index >= args.Length || args[index] is not ("github" or "gitee")) throw Invalid();
            source = args[index];
        }
        var result = await service.CheckAsync(source, false, token).ConfigureAwait(false);
        if (result.Check is { Status: "failed" } failed)
            throw new AppException(failed.ErrorCode ?? "UPDATE_CHECK_FAILED", "软件更新检查失败, 请稍后重试或选择另一个来源", 3);
        return result;
    }

    /// <summary>不接受安装、执行、任意 URL 或重复选项.</summary>
    private static AppException Invalid() => new("INVALID_ARGUMENT", "仅支持 updates check [--source github|gitee] [--json]");
}
