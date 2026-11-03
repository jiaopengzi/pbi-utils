// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\SampleResources.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 加载上游地理与姓名资源, 完整释放经清单验证的 PBIP 模板.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.SampleData;

/// <summary>固定上游提交资源; 不加载本机 Desktop 缓存或运行时远程资源.</summary>
internal static partial class SampleResources
{
    private const string Prefix = "PbiUtils.Sample/";
    internal const string CalendarPath = "demo.SemanticModel/definition/tables/Calendar.tmdl";
    private const string ExpressionPath = "demo.SemanticModel/definition/expressions.tmdl";
    private static readonly Assembly Assembly = typeof(SampleResources).Assembly;
    private static readonly IReadOnlyDictionary<string, string> ResourceNames = Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(name => name[Prefix.Length..].Replace('\\', '/'), name => name, StringComparer.Ordinal);
    private static readonly Lazy<SampleDataset> Dataset = new(LoadDataset);

    /// <summary>线程安全地取得完整地理与姓名基础数据.</summary>
    internal static SampleDataset Load() => Dataset.Value;

    /// <summary>全量覆盖模板同名条目且保留未知文件, 只替换 Path 和日历年份.</summary>
    internal static void Install(string directory, string dataDirectory, SampleDataOptions options, CancellationToken cancellationToken)
    {
        var (start, end) = options.Validate();
        using var manifest = JsonDocument.Parse(Read("manifest.json"));
        var resources = manifest.RootElement.GetProperty("files").EnumerateArray().Select(entry => entry.GetProperty("path").GetString()!)
            .Where(name => name.StartsWith("pbip/", StringComparison.Ordinal)).ToArray();
        if (resources.Length == 0 || !resources.ToHashSet(StringComparer.Ordinal).SetEquals(ResourceNames.Keys.Where(name => name.StartsWith("pbip/", StringComparison.Ordinal))))
            throw TemplateError();
        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = resource["pbip/".Length..];
            var content = Read(resource);
            if (relative == ExpressionPath)
            {
                var text = Encoding.UTF8.GetString(content);
                var match = PathExpression().Match(text);
                if (!match.Success) throw TemplateError();
                // M 的 #(...) 为转义序列, 路径内的 # 必须保留为字面值.
                var value = "\"" + dataDirectory.Replace("#", "#(#)", StringComparison.Ordinal) + "\"";
                text = text[..match.Groups[1].End()] + value + text[match.End()..];
                content = Encoding.UTF8.GetBytes(text);
            }
            else if (relative == CalendarPath) content = ReplaceYears(content, start.Year, end.Year);
            var target = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
    }

    /// <summary>增量只扩张当前模板日历年份; 模板缺失保持缺失.</summary>
    internal static void ExpandCalendar(string directory, SampleDataOptions options)
    {
        var path = Path.Combine(directory, CalendarPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return;
        var (start, end) = options.Validate();
        var content = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(content);
        var currentStart = ReadYear(text, CalendarStart());
        var currentEnd = ReadYear(text, CalendarEnd());
        var nextStart = Math.Min(currentStart, start.Year);
        var nextEnd = Math.Max(currentEnd, end.Year);
        if (nextStart != currentStart || nextEnd != currentEnd) File.WriteAllBytes(path, ReplaceYears(content, nextStart, nextEnd));
    }

    /// <summary>替换首个年份捕获组, 保留 BOM, 原换行, 月日与其他文本.</summary>
    private static byte[] ReplaceYears(byte[] content, int start, int end)
    {
        var text = Encoding.UTF8.GetString(content);
        text = ReplaceYear(text, CalendarStart(), start);
        text = ReplaceYear(text, CalendarEnd(), end);
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>在确切捕获位置替换年份, 不执行其他表达式改写.</summary>
    private static string ReplaceYear(string text, Regex regex, int year)
    {
        var group = regex.Match(text).Groups[1];
        if (!group.Success) throw TemplateError();
        return text[..group.Index] + year.ToString(CultureInfo.InvariantCulture) + text[group.End()..];
    }

    /// <summary>解析日历首个年值, 缺少表达式时明确失败.</summary>
    private static int ReadYear(string text, Regex regex)
    {
        var group = regex.Match(text).Groups[1];
        if (!group.Success || !int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw TemplateError();
        return value;
    }

    /// <summary>读取内嵌资源原始字节, 缺失视为打包失败.</summary>
    internal static byte[] Read(string relative)
    {
        if (!ResourceNames.TryGetValue(relative.Replace('\\', '/'), out var resourceName)) throw TemplateError();
        using var stream = Assembly.GetManifestResourceStream(resourceName) ?? throw TemplateError();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>解析内嵌 CSV 与逗号姓名表, 原六大区信息逐条等价迁移.</summary>
    private static SampleDataset LoadDataset()
    {
        var provinces = Rows("province.csv").Select(row => new SampleProvince(Int(row[0]), Int(row[1]), row[2], row[3], row[4], Number(row[5]), Number(row[6]))).ToArray();
        var cities = Rows("city.csv").Select(row => new SampleCity(Int(row[0]), Int(row[1]), row[2], Number(row[3]), Number(row[4]))).ToArray();
        var districts = Rows("district.csv").Select(row => new SampleDistrict(Int(row[0]), Int(row[1]), row[2], Number(row[3]), Number(row[4]))).ToArray();
        SampleRegion[] regions = [new(1, "东区", "欧阳经往", 310000, "上海", 31.231518, 121.471518),
            new(2, "西区", "焦阿灰", 510100, "成都", 30.659518, 104.065518),
            new(3, "南区", "左丘垂漫", 440100, "广州", 23.125518, 113.280518),
            new(4, "北区", "左烈佐", 210100, "沈阳", 41.796518, 123.429518),
            new(5, "中区", "焦仔耘", 110000, "北京", 39.901518, 116.401518),
            new(6, "港澳台", "安修谊", 810000, "香港", 22.320518, 114.173518)];
        return new(regions, provinces, cities, districts, Names("first_name.txt"), Names("last_name.txt"));
    }

    /// <summary>资源 CSV 无带引号逗号字段, 沿用上游逐行拆分且跳过表头.</summary>
    private static IEnumerable<string[]> Rows(string name) => Encoding.UTF8.GetString(Read(name)).Split('\n').Skip(1)
        .Select(line => line.Trim()).Where(line => line.Length > 0).Select(line => line.Split(','));

    /// <summary>保留内嵌姓名的顺序及重名权重, 仅过滤空值.</summary>
    private static string[] Names(string name) => [.. Encoding.UTF8.GetString(Read(name)).Trim().Split(',').Select(value => value.Trim()).Where(value => value.Length > 0)];

    /// <summary>按上游宽松整数转换, 解析失败返回零.</summary>
    private static int Int(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    /// <summary>按上游宽松小数转换, 解析失败返回零.</summary>
    private static double Number(string value) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    /// <summary>模板损坏错误不携带内部目录或原始内容.</summary>
    private static AppException TemplateError() => new("SAMPLE_TEMPLATE_INVALID", "内嵌示例模板不完整或日历表达式已改变.", 4);
    /// <summary>匹配固定 Path 参数前缀与旧引号值.</summary>
    [GeneratedRegex("(expression\\s+Path\\s*=\\s*)\"[^\"]*\"", RegexOptions.CultureInvariant)]
    private static partial Regex PathExpression();
    /// <summary>匹配日历开始年份, 保留月日和注释.</summary>
    [GeneratedRegex(@"date_start\s*=\s*#date\(\s*(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex CalendarStart();
    /// <summary>匹配日历结束年份, 保留月日和注释.</summary>
    [GeneratedRegex(@"date_end\s*=\s*#date\(\s*(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex CalendarEnd();
    /// <summary>计算捕获段末尾, 便于按位置保留未修改文本.</summary>
    private static int End(this Capture capture) => capture.Index + capture.Length;
}
