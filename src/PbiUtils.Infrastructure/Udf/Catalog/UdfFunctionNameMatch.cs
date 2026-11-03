// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Catalog\UdfFunctionNameMatch.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以大小写无关包含或按序字符匹配生成函数名高亮区间.

namespace PbiUtils.Infrastructure.Udf.Catalog;

/// <summary>匹配仅解释普通文本, 不运行正则或HTML, 区间按UTF16索引.</summary>
internal static class UdfFunctionNameMatch
{
    /// <summary>空文本匹配全部; 先连续包含, 再按序子序列, 无匹配返回null.</summary>
    internal static int[][]? Ranges(string name, string query)
    {
        if (query.Length == 0) return [];
        var contiguous = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (contiguous >= 0) return [[contiguous, query.Length]];
        var positions = new List<int>(); var next = 0;
        for (var index = 0; index < name.Length && next < query.Length; index++)
        {
            if (char.ToUpperInvariant(name[index]) != char.ToUpperInvariant(query[next])) continue;
            positions.Add(index); next++;
        }
        if (next != query.Length) return null;
        var ranges = new List<int[]>();
        foreach (var position in positions)
        {
            if (ranges.Count > 0 && ranges[^1][0] + ranges[^1][1] == position) ranges[^1][1]++;
            else ranges.Add([position, 1]);
        }
        return [.. ranges];
    }
}
