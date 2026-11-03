// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldCalendarProgramTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证独立日期函数的动态依赖, 显式调用与确定性编译边界.

using PbiUtils.Application.Calendar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>编译契约只证明生成内容, 不冒充实际 Power Query 计算.</summary>
public sealed partial class ScaffoldCalendarProgramTests
{
    /// <summary>简单函数仅携带所需公历计算, 调用单独传四个参数.</summary>
    [Fact]
    public void SimpleCalendarReturnsFunctionWithoutUnusedCalculations()
    {
        var options = new ScaffoldCalendarOptions("simple", 1900, 2100, "Dates");
        var program = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options));
        Assert.Contains("Value.ReplaceType", program.FunctionExpression, StringComparison.Ordinal);
        Assert.Contains("type function", program.FunctionExpression, StringComparison.Ordinal);
        Assert.Contains("Documentation.FieldCaption", program.FunctionExpression, StringComparison.Ordinal);
        Assert.StartsWith("PbiUtilsCalendar(", program.InvocationExpression);
        Assert.Contains("1900", program.InvocationExpression, StringComparison.Ordinal);
        Assert.Contains("2100", program.InvocationExpression, StringComparison.Ordinal);
        foreach (var unused in new[] { "Lunar", "YearRules", "Quarter", "OverrideMap", "WeekCN", "MonthCN", "ColumnIndexes" })
            Assert.DoesNotContain(unused, program.FunctionExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("List.Dates", program.InvocationExpression, StringComparison.Ordinal);
        Assert.Equal("calendar-m-v5", program.GeneratorVersion);
    }

    /// <summary>可复制查询完整包装命名函数, 参数类型和双语说明含有效示例及年份边界.</summary>
    [Theory]
    [InlineData("zh-cn", false, "开始年份", "返回 2024 年")]
    [InlineData("en-us", false, "Start year", "Returns dates for 2024")]
    [InlineData("zh-cn", true, "开始年份", "返回 2024 年")]
    [InlineData("en-us", true, "Start year", "Returns dates for 2024")]
    public void FunctionDocumentationIncludesCompleteTypedMetadata(string locale, bool lunar, string caption, string exampleResult)
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: lunar ? ["Dates", "LunarYear"] : ["Dates"]);
        var program = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), locale);
        var code = program.FunctionExpression;
        Assert.Contains("PbiUtilsCalendar = (", code, StringComparison.Ordinal);
        Assert.Contains("Value.ReplaceType(PbiUtilsCalendar, FunctionType)", code, StringComparison.Ordinal);
        foreach (var field in new[] { "Documentation.Name", "Documentation.Description", "Documentation.LongDescription",
            "Documentation.Author", "Documentation.WhoAskedTheRightQuestion", "Documentation.Examples", "Documentation.FieldCaption",
            "Documentation.FieldDescription", "Documentation.SampleValues", "Documentation.AllowedValues" })
            Assert.Contains(field, code, StringComparison.Ordinal);
        Assert.Contains(caption, code, StringComparison.Ordinal);
        Assert.Contains(exampleResult, code, StringComparison.Ordinal);
        Assert.Contains("PbiUtilsCalendar(2024, 2024, {0, 1, 2, 3, 4}, {})", code, StringComparison.Ordinal);
        Assert.Contains(lunar ? "{2000 .. 2099}" : "{1900 .. 2100}", code, StringComparison.Ordinal);
        Assert.Contains("StartYear as (", code, StringComparison.Ordinal);
        Assert.Contains("EndYear as (", code, StringComparison.Ordinal);
        Assert.Contains("Workdays as (", code, StringComparison.Ordinal);
        Assert.Contains("Overrides as (", code, StringComparison.Ordinal);
    }

    /// <summary>查询输出直接引用最后一个已绑定步骤, 保留函数类型和完整参数元数据.</summary>
    [Theory]
    [InlineData("zh-cn", false)]
    [InlineData("en-us", false)]
    [InlineData("zh-cn", true)]
    [InlineData("en-us", true)]
    public void FunctionOutputReferencesItsFinalTypedStep(string locale, bool lunar)
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: lunar ? ["Dates", "LunarYear"] : ["Dates"]);
        var program = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), locale);
        Assert.Matches(@"(?s)\]\s*,\s*(?<step>[A-Za-z][A-Za-z0-9_]*)\s*=\s*Value\.ReplaceType\(PbiUtilsCalendar,\s*FunctionType\)\s*in\s*\k<step>\s*$", program.FunctionExpression);
    }

    /// <summary>显式函数名与默认输出步骤同名时, 最后步骤仍唯一且直接返回类型包装结果.</summary>
    [Theory]
    [InlineData("zh-cn")]
    [InlineData("en-us")]
    public void FunctionOutputStepDoesNotDuplicateItsBoundFunctionName(string locale)
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Dates"]);
        var program = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), locale, "TypedFunction");
        var output = TypedFunctionOutputPattern().Match(program.FunctionExpression);
        Assert.True(output.Success);
        Assert.NotEqual(program.FunctionName, output.Groups["step"].Value);
    }

    /// <summary>工作日状态不引入传统节日或农历计算.</summary>
    [Fact]
    public void WorkdayStatusDoesNotCarryLunarRules()
    {
        var options = new ScaffoldCalendarOptions("full", 1900, 2100, Fields: ["Dates", "Status"]);
        var code = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options)).FunctionExpression;
        Assert.Contains("OverrideMap", code, StringComparison.Ordinal);
        Assert.Contains("工作日", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Lunar", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Binary.FromText", code, StringComparison.Ordinal);
    }

    /// <summary>最终映射中的旧农历列决定边界和固定规则, 不只看用户字段.</summary>
    [Fact]
    public void FinalAliasesDetermineLunarClosureAndBounds()
    {
        var options = new ScaffoldCalendarOptions("full", 2000, 2099, Fields: ["Dates"]);
        var program = ScaffoldCalendarM.Compile(options, [new("日期", "Dates"), new("旧农历年", "LunarYear")]);
        Assert.Contains(Convert.ToBase64String(ScaffoldCalendar.GetLunarYearData()), program.FunctionExpression, StringComparison.Ordinal);
        Assert.Contains("FindMonth", program.FunctionExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("LunarDayCN", program.FunctionExpression, StringComparison.Ordinal);
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options with { StartYear = 1900 }, [new("日期", "Dates"), new("旧农历年", "LunarYear")]));
    }

    /// <summary>同一语义多别名保留其相对顺序, 输出顺序遵循固定目录.</summary>
    [Fact]
    public void AliasesKeepActualNamesAndCatalogOrder()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Dates", "Year"], NumberedColumns: false);
        var code = ScaffoldCalendarM.Compile(options, [new("年", "Year"), new("日期\"#(lf)", "Dates"), new("旧日期", "Dates")]).FunctionExpression;
        Assert.Contains("日期\"\"#(#)(lf)", code, StringComparison.Ordinal);
        Assert.True(code.IndexOf("日期\"\"#(#)(lf)", StringComparison.Ordinal) < code.IndexOf("旧日期", StringComparison.Ordinal));
        Assert.DoesNotContain("C28_Year", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NameFor", code, StringComparison.Ordinal);
    }

    /// <summary>名称固定且不受表名影响, 显式调用与系统区域及配置输入顺序无关.</summary>
    [Fact]
    public void EquivalentInputsProduceIdenticalProgramAndStableName()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, "Dates", Fields: ["Status", "Dates"], Workdays: [6, 0],
            Overrides: [new("2024-03-01", true, ""), new("2024-01-01", false, "假")]);
        var first = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var reordered = options with { Fields = ["Dates", "Status"], Workdays = [0, 6], Overrides = [.. options.Overrides!.Reverse()] };
            Assert.Equal(first, ScaffoldCalendarM.Compile(reordered, ScaffoldCalendarFields.Resolve(reordered)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("PbiUtilsCalendar", first.FunctionName);
        Assert.Equal(first.FunctionName, ScaffoldCalendarM.Compile(options with { TableName = "dates" }, ScaffoldCalendarFields.Resolve(options)).FunctionName);
    }

    /// <summary>界面语言只改变函数说明, 调用和固定业务标签保持原值.</summary>
    [Fact]
    public void LocaleChangesDocumentationWithoutChangingInvocation()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Dates", "Status"]);
        var zh = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options));
        var en = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), "en-us");
        Assert.Equal(zh.InvocationExpression, en.InvocationExpression);
        Assert.Contains("开始年份", zh.FunctionExpression, StringComparison.Ordinal);
        Assert.Contains("Start year", en.FunctionExpression, StringComparison.Ordinal);
        Assert.Contains("工作日", en.FunctionExpression, StringComparison.Ordinal);
        Assert.Equal("en-us", en.Locale);
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), "fr-fr"));
    }

    /// <summary>宿主提供的绑定名称始终作为引用编码, 不允许控制字符或表达式注入.</summary>
    [Fact]
    public void BoundFunctionNamesAreQuotedAndInvalidInputsRejected()
    {
        var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: ["Dates"]);
        var code = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), functionName: "函数\"#(lf)");
        Assert.StartsWith("#\"函数\"\"#(#)(lf)\"(", code.InvocationExpression);
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, [new("Date", "Dates"), new("date", "Year")]));
        Assert.Throws<AppException>(() => ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), functionName: "bad\nname"));
        Assert.Throws<OperationCanceledException>(() => ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options), cancellationToken: new(true)));
    }

    /// <summary>每个字段可独立展开依赖, 只有目录标记的农历字段携带固定规则.</summary>
    [Fact]
    public void EveryCatalogFieldCompilesWithItsOwnDependencyClosure()
    {
        foreach (var field in ScaffoldCalendarFields.All)
        {
            var options = new ScaffoldCalendarOptions("full", 2024, 2024, Fields: field.Id == "Dates" ? ["Dates"] : ["Dates", field.Id]);
            var program = ScaffoldCalendarM.Compile(options, ScaffoldCalendarFields.Resolve(options));
            Assert.Equal(field.RequiresLunar, program.FunctionExpression.Contains("Binary.FromText", StringComparison.Ordinal));
            foreach (var forbidden in new[] { "File.Contents", "Web.Contents", "DateTime.LocalNow", "DateTimeZone.", "ColumnIndexes" })
                Assert.DoesNotContain(forbidden, program.FunctionExpression, StringComparison.Ordinal);
        }
    }

    /// <summary>合法大批量覆盖和最大别名数不被通用格式化的小输入边界误伤.</summary>
    [Theory]
    [InlineData(10000, "")]
    [InlineData(1900, "########################################################################################################################################################################################################")]
    [InlineData(1000, "\"\"\"\"\"\"\"\"\"\"\"\"\"\"\"\"\"\"\"\"")]
    public void AcceptedLargeOverridesFitTheProgramBudget(int count, string name)
    {
        var start = new DateOnly(2000, 1, 1);
        var overrides = Enumerable.Range(0, count).Select(index => new WorkdayOverride(start.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false, name)).ToArray();
        var options = new ScaffoldCalendarOptions("full", 2000, 2099, Fields: ["Dates"], Overrides: overrides);
        var columns = Enumerable.Range(0, 256).Select(index => new ScaffoldCalendarColumn("Date_" + index.ToString(CultureInfo.InvariantCulture), "Dates")).ToArray();
        var program = ScaffoldCalendarM.Compile(options, columns);
        Assert.True(Encoding.UTF8.GetByteCount(program.FunctionExpression) + Encoding.UTF8.GetByteCount(program.InvocationExpression) <= 3 * 1024 * 1024);
        Assert.Contains("Date_255", program.FunctionExpression, StringComparison.Ordinal);
        Assert.Contains("#date", program.InvocationExpression, StringComparison.Ordinal);
    }

    /// <summary>生产程序集只保留年度规则, 独立逐日预言机改由测试程序集持有.</summary>
    [Fact]
    public void LegacyDailyOracleIsNotShippedInProductionAssembly()
    {
        Assert.DoesNotContain("PbiUtils.Scaffolding.Lunar.v1", typeof(ScaffoldCalendar).Assembly.GetManifestResourceNames());
        using var stream = typeof(ScaffoldCalendarProgramTests).Assembly.GetManifestResourceStream("PbiUtils.Tests.Scaffolding.Lunar.v1");
        Assert.NotNull(stream);
        Assert.Equal(146100, stream.Length);
        Assert.Equal("1B1611C6F1A378C45EA073532126E26E7F1D0FAC5581E198A26B50B3949ED4D0", Convert.ToHexString(SHA256.HashData(stream)));
    }
    /// <summary>匹配内部函数名与默认输出步骤同名时的最终类型包装和直接返回约束.</summary>
    [GeneratedRegex(@"(?s)\]\s*,\s*(?<step>[A-Za-z][A-Za-z0-9_]*)\s*=\s*Value\.ReplaceType\(#""TypedFunction"",\s*FunctionType\)\s*in\s*\k<step>\s*$")]
    private static partial Regex TypedFunctionOutputPattern();


}
