// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Export\ModelTableSpreadsheetWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 无额外依赖地流式生成标准 Excel 工作簿, 保留标量类型并硬校验格式上限.

using PbiUtils.Application.Models.Export;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Models.Export;

/// <summary>标准 OOXML 导出器, 不建立全表内存副本或可执行公式.</summary>
internal sealed partial class ModelTableSpreadsheetWriter : IDisposable
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    // 标准毫秒日期包含23个字符, 默认列宽预留可读空间, 不扫描或缓存整表计算自动宽度.
    private const string DefaultColumnWidth = "25";
    private readonly ZipArchive zip;
    private readonly ModelTableExportRequest request;
    private readonly string[] sheets;
    private readonly List<(string Table, long Rows, int Columns)> completed = [];
    private bool finalized;

    /// <summary>建立一个流式工作簿, 仅预先写包关系和名称, 数据仍按单表游标依次消费.</summary>
    /// <param name="output">外部文件owner持有的私有输出流, 本类型不关闭它.</param>
    /// <param name="options">已验证的精确表顺序、语言及摘要选项.</param>
    internal ModelTableSpreadsheetWriter(Stream output, ModelTableExportRequest options)
    {
        request = options;
        var summary = options.IncludeLog ? options.Locale == "en-us" ? "Export log" : "导出日志" : null;
        sheets = ModelTableExportNames.Sheets(options.Tables, summary);
        zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        try { Package(zip, sheets, summary); }
        catch { zip.Dispose(); throw; }
    }

    /// <summary>按原始行流式写入真实 xlsx, 为数据和摘要预留日期可读列宽, 任一格式限制超出立即失败且不截断.</summary>
    /// <param name="table">当前已选表名, 顺序必须与请求一致.</param>
    /// <param name="rows">逐行读取的原始标量及标题, 不建立全表内存副本.</param>
    /// <param name="token">逐行及结束前检查的取消令牌.</param>
    /// <returns>完整写入的数据行数, 不包含标题或摘要.</returns>
    /// <exception cref="AppException">Excel 行列、单元格长度或值不支持时明确失败.</exception>
    /// <exception cref="OperationCanceledException">逐行或结束边界受理取消后中止写入.</exception>
    internal long Write(string table, IModelTableRows rows, CancellationToken token)
    {
        if (finalized || completed.Count >= request.Tables.Count || request.Tables[completed.Count] != table)
            throw new AppException("EXPORT_FILE_STATE", "导出文件已经写入", 4);
        if (rows.Columns.Count is < 1 or > 16384) throw Limit();
        long count = 0;
        using (var xml = Entry(zip, "xl/worksheets/sheet" + (completed.Count + 1).ToString(CultureInfo.InvariantCulture) + ".xml"))
        {
            xml.WriteStartElement("worksheet", Main);
            Empty(xml, "sheetFormatPr", Main, ("defaultColWidth", DefaultColumnWidth), ("defaultRowHeight", "15"));
            xml.WriteStartElement("sheetData", Main);
            Row(xml, 1, rows.Columns.Cast<object?>().ToArray());
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!rows.Read()) break;
                token.ThrowIfCancellationRequested();
                if (++count > 1048575) throw Limit();
                Row(xml, count + 1, Enumerable.Range(0, rows.Columns.Count).Select(rows.GetValue));
            }
            xml.WriteEndElement(); xml.WriteEndElement();
        }
        completed.Add((table, count, rows.Columns.Count));
        token.ThrowIfCancellationRequested();
        return count;
    }

    /// <summary>全部工作表完成后才写摘要及ZIP尾部, 任何限制、取消或写入失败均使整包不可发布.</summary>
    /// <param name="token">格式完成之前和同步包关闭之后检查的取消令牌.</param>
    /// <exception cref="AppException">并非全部工作表均已完成或重复完成当前工作簿.</exception>
    internal void Complete(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (finalized || completed.Count != request.Tables.Count) throw new AppException("EXPORT_FILE_STATE", "导出文件尚未完成或已经发布", 4);
        if (request.IncludeLog)
        {
            var english = request.Locale == "en-us";
            using var xml = Entry(zip, "xl/worksheets/sheet" + (completed.Count + 1).ToString(CultureInfo.InvariantCulture) + ".xml");
            xml.WriteStartElement("worksheet", Main);
            Empty(xml, "sheetFormatPr", Main, ("defaultColWidth", DefaultColumnWidth), ("defaultRowHeight", "15"));
            xml.WriteStartElement("sheetData", Main);
            Row(xml, 1, [english ? "Table" : "表", english ? "Worksheet" : "工作表", english ? "Exported rows" : "导出行数", english ? "Columns" : "列数",
                english ? "Export mode" : "导出范围", english ? "Row limit per table" : "每表行数上限"]);
            for (var index = 0; index < completed.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var (Table, Rows, Columns) = completed[index];
                Row(xml, index + 2, [Table, sheets[index], Rows, Columns,
                    request.RowLimit.HasValue ? english ? "Sample" : "抽样" : english ? "All rows" : "全部行", request.RowLimit]);
            }
            Row(xml, completed.Count + 2, [english ? "Completed (UTC)" : "完成时间 (UTC)", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)]);
            xml.WriteEndElement(); xml.WriteEndElement();
        }
        token.ThrowIfCancellationRequested();
        zip.Dispose();
        finalized = true;
        token.ThrowIfCancellationRequested();
    }

    /// <summary>失败和取消也关闭包资源, 只影响调用方私有临时文件, 不补写成功摘要.</summary>
    public void Dispose() => zip.Dispose();

    /// <summary>写入固定包关系、工作表声明和数值日期样式, 所有引用均为包内路径.</summary>
    private static void Package(ZipArchive zip, string[] sheets, string? summary)
    {
        var count = sheets.Length + (summary is null ? 0 : 1);
        using (var xml = Entry(zip, "[Content_Types].xml"))
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            xml.WriteStartElement("Types", ns);
            Empty(xml, "Default", ns, ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
            Empty(xml, "Default", ns, ("Extension", "xml"), ("ContentType", "application/xml"));
            Empty(xml, "Override", ns, ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
            Empty(xml, "Override", ns, ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
            for (var i = 1; i <= count; i++) Empty(xml, "Override", ns, ("PartName", $"/xl/worksheets/sheet{i}.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            xml.WriteEndElement();
        }
        using (var xml = Entry(zip, "_rels/.rels"))
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
            xml.WriteStartElement("Relationships", ns);
            Empty(xml, "Relationship", ns, ("Id", "rId1"), ("Type", Relationships + "/officeDocument"), ("Target", "xl/workbook.xml"));
            xml.WriteEndElement();
        }
        using (var xml = Entry(zip, "xl/_rels/workbook.xml.rels"))
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
            xml.WriteStartElement("Relationships", ns);
            for (var i = 1; i <= count; i++) Empty(xml, "Relationship", ns, ("Id", $"rId{i}"), ("Type", Relationships + "/worksheet"), ("Target", $"worksheets/sheet{i}.xml"));
            Empty(xml, "Relationship", ns, ("Id", "styles"), ("Type", Relationships + "/styles"), ("Target", "styles.xml"));
            xml.WriteEndElement();
        }
        using (var xml = Entry(zip, "xl/workbook.xml"))
        {
            xml.WriteStartElement("workbook", Main); xml.WriteAttributeString("xmlns", "r", null, Relationships); xml.WriteStartElement("sheets", Main);
            for (var i = 1; i <= count; i++)
            {
                xml.WriteStartElement("sheet", Main);
                xml.WriteAttributeString("name", i <= sheets.Length ? sheets[i - 1] : summary);
                xml.WriteAttributeString("sheetId", i.ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("r", "id", Relationships, $"rId{i}"); xml.WriteEndElement();
            }
            xml.WriteEndElement(); xml.WriteEndElement();
        }
        using (var xml = Entry(zip, "xl/styles.xml"))
        {
            xml.WriteStartElement("styleSheet", Main);
            xml.WriteStartElement("numFmts", Main); xml.WriteAttributeString("count", "1");
            Empty(xml, "numFmt", Main, ("numFmtId", "164"), ("formatCode", "yyyy-mm-dd hh:mm:ss.000")); xml.WriteEndElement();
            xml.WriteStartElement("fonts", Main); xml.WriteAttributeString("count", "1"); Empty(xml, "font", Main); xml.WriteEndElement();
            xml.WriteStartElement("fills", Main); xml.WriteAttributeString("count", "2");
            xml.WriteStartElement("fill", Main); Empty(xml, "patternFill", Main, ("patternType", "none")); xml.WriteEndElement();
            xml.WriteStartElement("fill", Main); Empty(xml, "patternFill", Main, ("patternType", "gray125")); xml.WriteEndElement(); xml.WriteEndElement();
            xml.WriteStartElement("borders", Main); xml.WriteAttributeString("count", "1"); Empty(xml, "border", Main); xml.WriteEndElement();
            xml.WriteStartElement("cellStyleXfs", Main); xml.WriteAttributeString("count", "1"); Empty(xml, "xf", Main, ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0")); xml.WriteEndElement();
            xml.WriteStartElement("cellXfs", Main); xml.WriteAttributeString("count", "2");
            Empty(xml, "xf", Main, ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"));
            Empty(xml, "xf", Main, ("numFmtId", "164"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"), ("applyNumberFormat", "1")); xml.WriteEndElement();
            xml.WriteStartElement("cellStyles", Main); xml.WriteAttributeString("count", "1"); Empty(xml, "cellStyle", Main, ("name", "Normal"), ("xfId", "0"), ("builtinId", "0")); xml.WriteEndElement();
            xml.WriteEndElement();
        }
    }

    /// <summary>逐个单元格写入行, 引用使用固定列坐标并保持空值的位置.</summary>
    private static void Row(XmlWriter xml, long number, IEnumerable<object?> values)
    {
        xml.WriteStartElement("row", Main); xml.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
        var index = 0;
        foreach (var value in values)
        {
            xml.WriteStartElement("c", Main); xml.WriteAttributeString("r", Column(++index) + number.ToString(CultureInfo.InvariantCulture));
            Cell(xml, value); xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    /// <summary>基础标量保留类型, 超过 Excel 有效数字能力的整数/小数以文本保全原始值.</summary>
    private static void Cell(XmlWriter xml, object? value)
    {
        if (value is null or DBNull) return;
        if (value is bool flag) { xml.WriteAttributeString("t", "b"); xml.WriteElementString("v", Main, flag ? "1" : "0"); return; }
        if (value is DateTime date && date.Year >= 1900)
        {
            // Excel 的 1900 闰年兼容日仅影响 3 月前日期, OLE Automation 在该区间多算一天.
            var serial = date.ToOADate() - (date < new DateTime(1900, 3, 1) ? 1 : 0);
            xml.WriteAttributeString("s", "1"); xml.WriteElementString("v", Main, serial.ToString("R", CultureInfo.InvariantCulture)); return;
        }
        var text = value is decimal number ? number.ToString("G29", CultureInfo.InvariantCulture) : ModelTableCsvWriter.Text(value);
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f)) throw new AppException("EXPORT_VALUE_UNSUPPORTED", "Excel 不支持非有限数值", 3);
            var significand = text.Split(['E', 'e'])[0].Replace(".", "", StringComparison.Ordinal).TrimStart('-', '+', '0').TrimEnd('0');
            if (significand.Length <= 15) { xml.WriteElementString("v", Main, text); return; }
        }
        if (text.Length > 32767) throw Limit();
        xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is", Main); xml.WriteStartElement("t", Main);
        xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve"); xml.WriteString(EscapeText(text)); xml.WriteEndElement(); xml.WriteEndElement();
    }

    /// <summary>以 OOXML 转义保留 XML 不支持的控制字符, 同时保护用户原有的相同转义文本.</summary>
    private static string EscapeText(string value)
    {
        var protectedText = EscapedCharacters().Replace(value, match => "_x005F_" + match.Value[1..]);
        var result = new StringBuilder(protectedText.Length);
        for (var i = 0; i < protectedText.Length; i++)
        {
            var c = protectedText[i];
            if (char.IsHighSurrogate(c) && i + 1 < protectedText.Length && char.IsLowSurrogate(protectedText[i + 1])) { result.Append(c); result.Append(protectedText[++i]); }
            else if (XmlConvert.IsXmlChar(c)) result.Append(c);
            else result.Append("_x").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
        }
        return result.ToString();
    }
    /// <summary>转换 1 起始列坐标, 上限由格式入口统一验证.</summary>
    private static string Column(int number) { var result = ""; while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; } return result; }
    /// <summary>创建包内 XML 流, 释放写入器会关闭当前 ZIP 条目.</summary>
    private static XmlWriter Entry(ZipArchive zip, string path) => XmlWriter.Create(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false, true), CloseOutput = true, NewLineHandling = NewLineHandling.Entitize });
    /// <summary>写入固定属性的空 XML 元素, 所有文本均经过 XML 编码.</summary>
    private static void Empty(XmlWriter xml, string name, string ns, params (string Name, string Value)[] attributes) { xml.WriteStartElement(name, ns); foreach (var (Name, Value) in attributes) xml.WriteAttributeString(Name, Value); xml.WriteEndElement(); }
    /// <summary>格式限制失败不能被截断为成功导出.</summary>
    private static AppException Limit() => new("EXPORT_EXCEL_LIMIT", "数据超过 Excel 行数、列数或单元格长度上限, 请使用 CSV", 3);
    /// <summary>识别用户原始的 OOXML 转义模式, 避免 Excel 将其解释为其他字符.</summary>
    [GeneratedRegex("_[xX][0-9a-fA-F]{4}_")]
    private static partial Regex EscapedCharacters();
}
