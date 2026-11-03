// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Export\ModelTableCsvWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 流式输出 UTF-8 CSV, 保留原值并正确转义逗号、引号及换行.

using PbiUtils.Application.Models.Export;
using System.Globalization;
using System.Text;
using PbiUtils.Application;

namespace PbiUtils.Infrastructure.Models.Export;

/// <summary>标准 CSV 保留数据值, 不添加会改变值的公式前缀或类型标记.</summary>
internal static class ModelTableCsvWriter
{
    /// <summary>写入标题和全部行, 检查逐行取消, 不拥有调用方输出流或 SDK 游标.</summary>
    public static long Write(Stream output, IModelTableRows rows, CancellationToken token)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(true, true), 65536, leaveOpen: true) { NewLine = "\r\n" };
        WriteRow(writer, rows.Columns);
        long count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!rows.Read()) break;
            token.ThrowIfCancellationRequested();
            WriteRow(writer, Enumerable.Range(0, rows.Columns.Count).Select(i => Text(rows.GetValue(i))));
            count++;
        }
        return count;
    }

    /// <summary>只对 CSV 分隔和换行语法转义, 原始文本不变成程序内公式.</summary>
    private static void WriteRow(TextWriter writer, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first) writer.Write(','); first = false;
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0) { writer.Write('"'); writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal)); writer.Write('"'); }
            else writer.Write(value);
        }
        writer.WriteLine();
    }

    /// <summary>使用文化不变表示保留标量, 日期保留完整精度, 空值为空字段.</summary>
    internal static string Text(object? value) => value switch
    {
        null or DBNull => "",
        bool flag => flag ? "TRUE" : "FALSE",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        byte[] binary => Convert.ToBase64String(binary),
        IFormattable scalar => scalar.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };
}
