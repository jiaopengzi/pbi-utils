// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\SampleCsvStorage.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定十一表的 BOM UTF-8 流式读写, 严格校验当前完整表头和行宽.

using System.Text;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.SampleData;

/// <summary>受控目录内的 CSV 适配器, 不提供任意文件名读取能力.</summary>
internal sealed class SampleCsvStorage(string directory) : ISampleTableStorage
{
    /// <summary>只接受固定表与准确表头, 新建文件写一次 BOM.</summary>
    public ISampleRowWriter Create(string fileName, IReadOnlyList<string> header)
    {
        if (!SampleTables.Header(fileName).SequenceEqual(header)) throw InvalidCsv();
        Directory.CreateDirectory(directory);
        return new CsvWriter(Path.Combine(directory, fileName), false, header);
    }

    /// <summary>检查既有表头后追加, 不重写 BOM 或表头.</summary>
    public ISampleRowWriter Append(string fileName)
    {
        using (var records = ReadRecords(Path.Combine(directory, FixedName(fileName))).GetEnumerator())
            ValidateHeader(fileName, records.MoveNext() ? records.Current : null);
        return new CsvWriter(Path.Combine(directory, fileName), true, null);
    }

    /// <summary>逐行解析带引号和换行的 CSV, 拒绝不匹配当前表头和行宽的记录.</summary>
    public IEnumerable<string[]> Read(string fileName)
    {
        using var records = ReadRecords(Path.Combine(directory, FixedName(fileName))).GetEnumerator();
        ValidateHeader(fileName, records.MoveNext() ? records.Current : null);
        var count = SampleTables.Header(fileName).Count;
        while (records.MoveNext())
        {
            var row = records.Current;
            if (row.Length != count) throw InvalidCsv();
            yield return row;
        }
    }

    /// <summary>只查询固定表是否存在.</summary>
    public bool Exists(string fileName) => File.Exists(Path.Combine(directory, FixedName(fileName)));

    /// <summary>验证固定文件的当前完整表头, 缺列或未知表头均返回稳定错误.</summary>
    private static void ValidateHeader(string fileName, string[]? header)
    {
        var expected = SampleTables.Header(fileName);
        if (header is null || !expected.SequenceEqual(header)) throw InvalidCsv();
    }

    /// <summary>拒绝未知表名与路径片段.</summary>
    private static string FixedName(string fileName)
    {
        _ = SampleTables.Header(fileName);
        return fileName;
    }

    /// <summary>严格 UTF-8 的有界流式 CSV 解析, 在解析过程中限制记录和字段长度.</summary>
    private static IEnumerable<string[]> ReadRecords(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), false, 256 * 1024);
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var endedQuote = false;
        var started = false;
        var length = 0;
        if (ReadCharacter(reader, true) == '\uFEFF') ReadCharacter(reader);
        while (true)
        {
            var code = ReadCharacter(reader);
            if (field.Length > 64 * 1024 || fields.Count > 128) throw InvalidCsv();
            if (code < 0)
            {
                if (quoted) throw InvalidCsv();
                if (started || fields.Count > 0 || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                yield break;
            }
            var value = (char)code;
            if (++length > 256 * 1024) throw InvalidCsv();
            if (quoted)
            {
                if (value == '"')
                {
                    if (ReadCharacter(reader, true) == '"') { ReadCharacter(reader); field.Append('"'); }
                    else { quoted = false; endedQuote = true; }
                }
                else
                {
                    if (value == '\r' && ReadCharacter(reader, true) == '\n') { ReadCharacter(reader); value = '\n'; }
                    field.Append(value);
                }
                continue;
            }
            if (value == '"' && field.Length == 0 && !endedQuote)
            {
                quoted = true;
                started = true;
                continue;
            }
            if (value == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                endedQuote = false;
                started = true;
                continue;
            }
            if (value == '\n' || value == '\r' && ReadCharacter(reader, true) == '\n')
            {
                if (value == '\r') ReadCharacter(reader);
                if (started || fields.Count > 0 || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                fields.Clear();
                field.Clear();
                endedQuote = false;
                started = false;
                length = 0;
                continue;
            }
            if (endedQuote || value == '"') throw InvalidCsv();
            field.Append(value);
            started = true;
        }
    }

    /// <summary>解码失败映射为安全损坏错误, 不默认替换非法字节.</summary>
    private static int ReadCharacter(StreamReader reader, bool peek = false)
    {
        try { return peek ? reader.Peek() : reader.Read(); }
        catch (DecoderFallbackException) { throw InvalidCsv(); }
    }

    /// <summary>返回安全错误, 不泄漏 CSV 内容或路径.</summary>
    private static AppException InvalidCsv() => new("SAMPLE_INVALID_CSV", "示例数据表结构不兼容或 CSV 已损坏, 请重新生成.", 4);

    /// <summary>用 LF 与上游一致地写入带引号字段, Dispose 传播刷新错误.</summary>
    private sealed class CsvWriter : ISampleRowWriter
    {
        private readonly StreamWriter writer;

        /// <summary>建立带缓冲的写入器, 新建时由编码写 BOM, 追加保持原文件前缀.</summary>
        public CsvWriter(string path, bool append, IReadOnlyList<string>? header)
        {
            var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
            writer = new StreamWriter(stream, new UTF8Encoding(!append), 256 * 1024) { NewLine = "\n" };
            if (header is not null) Write(header);
        }

        /// <summary>逐字段转义逗号, 引号, 换行和前导空白; 不改变字段文本.</summary>
        public void Write(IReadOnlyList<string> row)
        {
            for (var index = 0; index < row.Count; index++)
            {
                if (index > 0) writer.Write(',');
                var value = row[index];
                if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n') ||
                    value.Length > 0 && char.IsWhiteSpace(value[0]))
                {
                    writer.Write('"');
                    writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
                    writer.Write('"');
                }
                else writer.Write(value);
            }
            writer.WriteLine();
        }

        /// <summary>刷新并关闭文件, 失败向调用方传播.</summary>
        public void Dispose() => writer.Dispose();
    }
}
