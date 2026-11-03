// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Statistics\ModelTableRowCountQuery.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅从已核验所选表名构造有界固定COUNTROWS模板, 不接受自由DAX或安全身份覆盖.

using PbiUtils.Application.Code.Dax;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Models.Statistics;

/// <summary>仅供后端可信模型表集合使用的固定计数查询生成器.</summary>
public static class ModelTableRowCountQuery
{
    /// <summary>构造最多50表的 long 布局计数查询, 空表通过COALESCE保留真实0, 不使用ALL或绕过RLS.</summary>
    /// <param name="tables">已由当前权威模型核验的实际表名, 仍在此复核批次边界和名称格式.</param>
    /// <param name="cancellationToken">入口、逐表验证/生成和共享排版期间检查取消, 不返回部分查询.</param>
    /// <returns>共享 long 格式的固定EVALUATE/ROW/UNION/COUNTROWS查询, 保留名称转义与原有长查询容量.</returns>
    /// <exception cref="AppException">空、重复、过多或无效名称, 或固定查询无法安全排版.</exception>
    /// <exception cref="OperationCanceledException">调用方在生成开始前或生成过程中取消.</exception>
    public static string Create(IReadOnlyList<string> tables, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tables is null || tables.Count is < 1 or > 50)
            throw new AppException("VALIDATION_ERROR", "精确行数统计参数无效");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Contains('\0') || !names.Add(name))
                throw new AppException("VALIDATION_ERROR", "精确行数统计参数无效");
        }
        var rows = tables.Select(name =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var escapedStringName = name.Replace("\"", "\"\"", StringComparison.Ordinal);
            var escapedTableName = name.Replace("'", "''", StringComparison.Ordinal);
            return $"""ROW("__Table","{escapedStringName}","__Rows",COALESCE(COUNTROWS('{escapedTableName}'),0))""";
        }).ToArray();
        // 自有固定查询复用 long 规划, 不把50个长表名限制到交互式30000字符预算.
        var expression = rows.Length == 1 ? rows[0] : $"""UNION({string.Join(",", rows)})""";
        return DaxFormatter.FormatGeneratedQuery($"""EVALUATE {expression}""", cancellationToken);
    }
}
