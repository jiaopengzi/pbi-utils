// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Export\ModelTableExportNames.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为多表导出建立无路径含义且大小写唯一的CSV文件名与Excel工作表名称.

using System.Globalization;
using System.Text;

namespace PbiUtils.Infrastructure.Models.Export;

/// <summary>只清理输出容器中的显示名称, 不改写模型表名、查询标识符或业务单元格.</summary>
internal static class ModelTableExportNames
{
    /// <summary>按表顺序产生唯一工作表名, 并预留摘要名称, 不拆分Unicode代理项.</summary>
    /// <param name="tables">实际表名, 顺序和原始模型身份保持不变.</param>
    /// <param name="summary">可选摘要页保留名称.</param>
    /// <returns>可供Excel工作簿使用的合法唯一显示名称.</returns>
    internal static string[] Sheets(IReadOnlyList<string> tables, string? summary)
        => Unique(tables, 31, value => Clean(value, "[]:*?/\\").Trim('\''), summary);

    /// <summary>产生可在Windows打开的单层CSV文件名, 拒绝路径、设备名及大小写覆盖碰撞.</summary>
    /// <param name="tables">已选择表名, 不作为目录路径使用.</param>
    /// <returns>与表顺序一致的单层CSV文件名.</returns>
    internal static string[] CsvFiles(IReadOnlyList<string> tables)
        => [.. Unique(tables, 120, value =>
        {
            var name = Clean(value, new string(Path.GetInvalidFileNameChars())).Trim('.', ' ');
            var stem = name.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])) name = "_" + name;
            return name;
        }, null).Select(name => name + ".csv")];

    /// <summary>先清理再按大小写无关集合消除碰撞, 后缀也受名称总长度上限约束.</summary>
    private static string[] Unique(IReadOnlyList<string> tables, int maximum, Func<string, string> clean, string? reserved)
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        if (reserved is not null) used.Add(reserved);
        return [.. tables.Select(table =>
        {
            var basis = Truncate(clean(table), maximum).Trim('\'');
            if (basis.Length == 0) basis = "Table";
            var name = basis;
            for (var number = 2; !used.Add(name); number++)
            {
                var suffix = " (" + number.ToString(CultureInfo.InvariantCulture) + ")";
                name = Truncate(basis, maximum - suffix.Length) + suffix;
            }
            return name;
        })];
    }

    /// <summary>控制字符和容器不接受的字符改为下划线, 无效代理项由Rune替换而不写入非法XML.</summary>
    private static string Clean(string value, string invalid) => string.Concat(value.EnumerateRunes().Select(rune => rune.Value < 32 || rune.Value <= char.MaxValue && invalid.Contains((char)rune.Value) ? "_" : rune.ToString()));

    /// <summary>按UTF-16名称预算截取完整Unicode字符, 不以字符串切片拆开代理对.</summary>
    private static string Truncate(string value, int maximum)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > maximum) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
