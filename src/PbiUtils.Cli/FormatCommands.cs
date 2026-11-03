// FilePath    : pbi-utils\src\PbiUtils.Cli\FormatCommands.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 无模型和凭据依赖的本地格式化命令, 返回一个完整 JSON 结果.

using PbiUtils.Application.Code;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Cli;

/// <summary>CLI 与 MCP 共用格式化实现及预算, 不执行代码或修改文件.</summary>
internal static class FormatCommands
{
    /// <summary>解析 format dax|m --text, 未知、重复、缺值参数按退出码 2 拒绝.</summary>
    internal static object Run(string[] args, CancellationToken token)
    {
        if (args.Length < 2 || args[1] is not ("dax" or "m")) throw new AppException("INVALID_ARGUMENT", "需要 format dax 或 format m");
        string? text = null;
        HashSet<string> seen = [];
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) throw new AppException("INVALID_ARGUMENT", "格式化选项不能重复");
            if (option == "--json") continue;
            if (option != "--text" || ++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new AppException("INVALID_ARGUMENT", "格式化仅接受 --text <代码> 和 --json");
            text = args[index];
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new AppException("INVALID_ARGUMENT", "格式化需要 1–16000 字符的 --text");
        return new { language = args[1], text = CodeFormatting.Format(args[1], text, token), executed = false };
    }
}
