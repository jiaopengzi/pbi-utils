// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\WorkdayImport.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 纯内容解析工作日CSV/TSV并统一验证最终覆盖的条数、日期及UTF8预算.

using System.Globalization;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Application.Calendar;

/// <summary>仅处理已由用户选择的内容, 不访问导入路径或外部日历服务.</summary>
public static class WorkdayImport
{
    public const int MaximumTextBytes = 2 * 1024 * 1024;
    public const int MaximumOverrides = 10000;
    public const int MaximumNormalizedBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[][] Headers = [["date", "Date", "日期"], ["isWorkday", "IsWorkday", "是否工作日"], ["name", "Name", "名称"]];

    /// <summary>解析明确CSV/TSV三列表格, 文件内同日重复使整批失败; 输出按日期升序.</summary>
    public static WorkdayOverride[] Parse(string text, string delimiter, int startYear, int endYear)
    {
        if (text is null || !ValidUtf8(text, MaximumTextBytes)) throw ImportError(1, "原文必须为有效 UTF-8 且不超过 2 MiB");
        var separator = delimiter switch
        {
            "csv" or "," => ',',
            "tsv" or "\t" => '\t',
            _ => throw ImportError(1, "请选择 CSV 或 TSV 分隔符")
        };
        ValidateRange(startYear, endYear);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var rows = ReadRows(text, separator);
        if (rows.Count == 0 || rows[0].Cells.Length != 3 || rows[0].Cells.Any(cell => cell.Any(char.IsControl))
            || rows[0].Cells.Where((cell, i) => !Headers[i].Contains(cell.Trim(), StringComparer.Ordinal)).Any())
            throw ImportError(rows.Count == 0 ? 1 : rows[0].Line, "表头必须为 date,isWorkday,name 或声明的中英文别名");
        var result = new List<WorkdayOverride>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            if (row.Cells.Length != 3) throw ImportError(row.Line, "必须恰好包含三列");
            if (row.Cells.Any(cell => cell.Any(char.IsControl))) throw ImportError(row.Line, "字段内不允许控制字符或换行");
            var date = row.Cells[0].Trim();
            var value = row.Cells[1].Trim();
            var flag = value switch
            {
                "true" or "1" or "是" or "工作日" => true,
                "false" or "0" or "否" or "休息日" => false,
                _ => throw ImportError(row.Line, "工作标志只能为 true/false/1/0/是/否/工作日/休息日")
            };
            var item = new WorkdayOverride(date, flag, row.Cells[2]);
            if (!ValidItem(item, startYear, endYear) || !seen.Add(date))
                throw ImportError(row.Line, "日期无效、重复、超出范围或名称无效");
            result.Add(item with { Name = item.Name.Trim() });
        }
        try { return ValidateFinal(result, startYear, endYear); }
        catch (AppException error) when (error.Code == "INVALID_WORKDAY_OVERRIDE") { throw ImportError(rows[^1].Line, error.Message); }
    }

    /// <summary>所有入口对合并后的完整覆盖复验, 不截断、不部分应用; 名称去除首尾空格.</summary>
    public static WorkdayOverride[] ValidateFinal(IReadOnlyList<WorkdayOverride>? overrides, int startYear, int endYear)
    {
        ValidateRange(startYear, endYear);
        if (overrides is null) return [];
        if (overrides.Count > MaximumOverrides) throw InvalidFinal("工作日覆盖合并后最多 10000 条");
        var dates = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new WorkdayOverride[overrides.Count];
        for (var i = 0; i < overrides.Count; i++)
        {
            var item = overrides[i];
            if (!ValidItem(item, startYear, endYear) || !dates.Add(item.Date))
                throw InvalidFinal("工作日覆盖日期无效、重复、超出范围或名称无效");
            normalized[i] = item with { Name = item.Name.Trim() };
        }
        Array.Sort(normalized, (left, right) => string.CompareOrdinal(left.Date, right.Date));
        if (SerializeNormalized(normalized).Length > MaximumNormalizedBytes)
            throw InvalidFinal("工作日覆盖合并后的规范化 UTF-8 JSON 不得超过 512 KiB");
        return normalized;
    }

    /// <summary>固定camelCase属性顺序和UTF8文本编码, 字节预算包含所有记录与JSON结构.</summary>
    public static byte[] SerializeNormalized(IReadOnlyList<WorkdayOverride> overrides)
    {
        // 默认JSON编码器仍会转义emoji等合法Unicode标量, 此处按JSON.stringify的明确字符串规则计量.
        var output = new StringBuilder("[");
        for (var index = 0; index < overrides.Count; index++)
        {
            var item = overrides[index];
            if (index != 0) output.Append(',');
            output.Append("{\"date\":").Append(JsonText(item.Date)).Append(",\"isWorkday\":").Append(item.IsWorkday ? "true" : "false")
                .Append(",\"name\":").Append(JsonText(item.Name)).Append('}');
        }
        return StrictUtf8.GetBytes(output.Append(']').ToString());
    }

    /// <summary>只转义JSON必需字符, 合法代理对保持UTF8原文; 无效UTF16仍拒绝.</summary>
    private static string JsonText(string value)
    {
        _ = StrictUtf8.GetByteCount(value);
        var output = new StringBuilder("\"");
        foreach (var character in value)
            output.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        return output.Append('"').ToString();
    }

    private static bool ValidItem(WorkdayOverride? item, int startYear, int endYear) => item is not null
        && DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && date.Year >= startYear && date.Year <= endYear && item.Name is not null && item.Name.Length <= 200
        && !item.Name.Any(char.IsControl) && !item.Name.Contains('\uFEFF', StringComparison.Ordinal) && ValidUtf8(item.Name, 800);

    private static bool ValidUtf8(string text, int maximum)
    {
        try { return StrictUtf8.GetByteCount(text) <= maximum; }
        catch (EncoderFallbackException) { return false; }
    }

    private static void ValidateRange(int startYear, int endYear)
    {
        if (startYear < 1900 || endYear > 2100 || startYear > endYear)
            throw new AppException("INVALID_DATE_RANGE", "工作日覆盖年份需为 1900–2100 且起始不晚于结束");
    }

    private static AppException InvalidFinal(string message) => new("INVALID_WORKDAY_OVERRIDE", message);
    private static AppException ImportError(int line, string message) => new("INVALID_WORKDAY_IMPORT", $"第 {line.ToString(CultureInfo.InvariantCulture)} 行: {message}");

    private sealed record Row(int Line, string[] Cells);

    /// <summary>有界RFC风格引用解析; 引用内换行虽可分词, 最终名称规则仍明确拒绝控制字符.</summary>
    private static List<Row> ReadRows(string text, char delimiter)
    {
        var rows = new List<Row>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var closed = false;
        var line = 1;
        var rowLine = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (char.IsControl(current) && current is not ('\t' or '\r' or '\n') || current == '\uFEFF')
                throw ImportError(line, "不允许控制字符或非开头 BOM");
            if (quoted)
            {
                if (current == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else
                {
                    cell.Append(current);
                    if (current == '\n' || current == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) line++;
                }
                continue;
            }
            if (current == delimiter)
            {
                cells.Add(cell.ToString()); cell.Clear(); closed = false;
                if (cells.Count >= 3) throw ImportError(rowLine, "必须恰好包含三列");
            }
            else if (current is '\r' or '\n')
            {
                cells.Add(cell.ToString()); cell.Clear(); closed = false;
                AddRow();
                if (current == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                rowLine = ++line;
            }
            else if (current == '"')
            {
                if (cell.Length != 0 || closed) throw ImportError(line, "引号只能位于字段开头并成对闭合");
                quoted = true;
            }
            else if (closed)
            {
                if (current != ' ') throw ImportError(line, "闭合引号后只能为分隔符或换行");
            }
            else cell.Append(current);
        }
        if (quoted) throw ImportError(rowLine, "引用字段缺少闭合引号");
        if (cell.Length > 0 || cells.Count > 0 || closed) { cells.Add(cell.ToString()); AddRow(); }
        return rows;

        void AddRow()
        {
            if (cells.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                rows.Add(new(rowLine, [.. cells]));
                if (rows.Count > MaximumOverrides + 1) throw ImportError(rowLine, "工作日覆盖最多 10000 条");
            }
            cells.Clear();
        }
    }
}
