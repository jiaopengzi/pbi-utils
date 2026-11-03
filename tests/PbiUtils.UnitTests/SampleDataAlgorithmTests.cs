// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\SampleDataAlgorithmTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Go 数理金标, 五梯队, 数据关联, 日期边界, 库存和目标规则.

using System.Globalization;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;
using PbiUtils.Infrastructure.SampleData;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>真实资源与独立输出聚合验证纯业务迁移, 不依赖 UI 或 Go 运行时.</summary>
public sealed class SampleDataAlgorithmTests
{
    private static readonly SampleDataset Dataset = SampleResources.Load();
    private static readonly SampleDataOptions Options = new("zh-cn", 8, 9, 14, "2021-01-01", "2025-06-01", 42);
    private static readonly string[] collection = ["线上", "线下"];

    /// <summary>数值来自上游固定提交的 Go overlay 实测, 验证银行家舍入和 uint64 溢出兼容.</summary>
    [Fact]
    public void MathMatchesIndependentGoGoldenNumbers()
    {
        // 197aaf4b6c3fac28990814851640c21796d49263 的 TestCSharpGolden, 2026-10-14 本机执行.
        int[] ids = [1, 2, 7, 55, 10000];
        int[] indices = [0, 4, 3, 2, 3];
        for (var i = 0; i < ids.Length; i++) Assert.Equal(indices[i], SampleDataGenerator.StoreFactorIndex(ids[i], 5));
        (int Pid, int Ym, double Expected)[] targets = [(110000, 202501, 0.2389912830261509), (440000, 202608, 0.34639496081511756), (820000, 203012, 0.8265055204834385)];
        foreach (var (pid, ym, expected) in targets) AssertClose(expected, SampleDataGenerator.TargetFactorUniform(pid, ym));
        (int Dim, int Year, double Amp, double Expected)[] drift = [(0, 2025, 0.4, 0.7455483633549099), (100, 2026, 0.1, 1.0862986411040767), (200, 2024, 0.2, 0.9302760091719725), (300, 2026, 0.06, 1.0360681117956645), (400, 2026, 0.05, 1.0191535925392223)];
        foreach (var (dim, year, amp, expected) in drift) AssertClose(expected, SampleDataGenerator.SeasonYearDrift(dim, year, amp));
        double[] values = [-3, -1, 0, 0.5, 1, 3, 6];
        double[] probabilities = [0.0013498980316301035, 0.15865525393145707, 0.5, 0.6914624612740131, 0.8413447460685429, 0.9986501019683699, 0.9999999990134123];
        for (var i = 0; i < values.Length; i++) AssertClose(probabilities[i], SampleDataGenerator.StandardNormalCdf(values[i]));
        Assert.Equal(new[] { -2, -2, 0, 2, 2, 4 }, new[] { -2.5, -1.5, 0.5, 1.5, 2.5, 3.5 }.Select(SampleDataGenerator.RoundInt));
        Assert.Equal(1.12, SampleDataGenerator.Round(1.125, 2));
        Assert.Equal(1.38, SampleDataGenerator.Round(1.375, 2));
        AssertClose(1.2599632622692485, SampleDataGenerator.CategorySeasonFactor("A类", 2026, 1));
        AssertClose(0.710006427980716, SampleDataGenerator.CategorySeasonFactor("J类", 2025, 11));
        Assert.Equal(1, SampleDataGenerator.CategorySeasonFactor("未知", 2025, 1));
    }

    /// <summary>谐波均值严格为 1, 各旺月不同, 全部年度漂移在给定范围内.</summary>
    [Fact]
    public void SeasonalityRetainsPositiveUnitMeanAndDimensionDifferences()
    {
        (double Amp, double Peak)[][] groups =
        [
            [(0.24, 9.5), (0.22, 10.5), (0.20, 1), (0.19, 3), (0.18, 7), (0.17, 5), (0.15, 8.5), (0.13, 4), (0.10, 2.5), (0.07, 11)],
            [(0.55, 9), (0.50, 4), (0.45, 11), (0.60, 7.5), (0.50, 2.5), (0.55, 1)],
            [(0.60, 4.4), (0.45, 7.9), (0.55, 6.1), (0.50, 9.6), (0.70, 1), (0.60, 11.3), (0.50, 2.7)],
        ];
        foreach (var parameters in groups)
            for (var i = 0; i < parameters.Length; i++)
            {
                var values = Enumerable.Range(1, 12).Select(m => SampleDataGenerator.SeasonHarmonic(parameters[i].Amp, parameters[i].Peak, m)).ToArray();
                Assert.All(values, v => Assert.True(v > 0));
                AssertClose(1, values.Average());
                for (var j = i + 1; j < parameters.Length; j++)
                    Assert.True(Enumerable.Range(1, 12).Max(m => Math.Abs(values[m - 1] - SampleDataGenerator.SeasonHarmonic(parameters[j].Amp, parameters[j].Peak, m))) > 0.02);
            }
        for (var dimension = 0; dimension < 500; dimension++)
            for (var year = 2020; year <= 2030; year++) Assert.InRange(SampleDataGenerator.SeasonYearDrift(dimension, year, 0.05), 0.95, 1.05);
    }

    /// <summary>五梯队覆盖 34 省, 50 种子下双权重正值, 配额均值为 1, 块间严格有序.</summary>
    [Fact]
    public void ProvinceWeightModelKeepsCoverageNormalizationAndBoundaryExchange()
    {
        var uniqueBlocks = new HashSet<string>();
        int[] sizes = [8, 5, 7, 8, 6];
        for (var seed = 1; seed <= 50; seed++)
        {
            var model = new SampleDataGenerator(Options with { Seed = seed }, Dataset).ProvinceWeights;
            Assert.Equal(Dataset.Provinces.Select(p => p.ProvinceId).Order(), model.Quota.Keys.Order());
            AssertClose(1, model.Quota.Values.Average());
            Assert.All(model.Quota.Values, q => Assert.True(q > 0));
            Assert.All(model.Volume.Values, v => Assert.True(v > 0));
            var effective = model.Quota.Sum(p => p.Value / model.CityFactor[p.Key]);
            var effectiveVolume = model.Quota.Sum(p => p.Value / model.CityFactor[p.Key] * model.Volume[p.Key]);
            AssertClose(effective / effectiveVolume, model.VolumeScale);
            for (var b = 0; b < sizes.Length; b++)
            {
                Assert.Equal(sizes[b], model.Blocks[b].Length);
                if (b < sizes.Length - 1)
                {
                    var low = model.Blocks[b].Min(pid => model.Quota[pid] * model.Volume[pid]);
                    var nextHigh = model.Blocks[b + 1].Max(pid => model.Quota[pid] * model.Volume[pid]);
                    Assert.True(low > nextHigh, $"种子 {seed}, 梯队 {b}: {low} <= {nextHigh}");
                }
            }
            uniqueBlocks.Add(string.Join(',', model.Blocks.SelectMany(b => b)));
        }
        Assert.True(uniqueBlocks.Count > 40);
    }

    /// <summary>全量输出验证所有关联, 自增编号, 城市匹配, 日期范围与库存涵盖销售数量.</summary>
    [Fact]
    public void FullOutputPreservesRelationsFieldsBusinessDatesAndInventory()
    {
        var storage = new MemoryStorage();
        var result = new SampleDataGenerator(Options, Dataset).GenerateAll(storage);
        Assert.Equal(11, storage.Tables.Count);
        Assert.Equal(result.Orders, storage.Tables[SampleTables.FileOrder].Count);
        foreach (var (file, rows) in storage.Tables)
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.Equal(SampleTables.Header(file).Count, rows[i].Length);
                Assert.Equal((i + 1).ToString(CultureInfo.InvariantCulture), rows[i][0]);
            }
        var products = storage.Tables[SampleTables.FileProduct].ToDictionary(r => r[1]);
        var stores = storage.Tables[SampleTables.FileStore].ToDictionary(r => r[1]);
        var customers = storage.Tables[SampleTables.FileCustomer].ToDictionary(r => r[1]);
        var orders = storage.Tables[SampleTables.FileOrder].ToDictionary(r => r[1]);
        var cityNames = Dataset.Cities.ToDictionary(c => c.CityId, c => c.Name);
        foreach (var row in stores.Values) Assert.Equal(cityNames[Integer(row[5])], row[6]);
        var sold = new Dictionary<(string Store, string Product), int>();
        foreach (var row in orders.Values)
        {
            var store = stores[row[2]];
            var date = Date(row[3]);
            Assert.True(date >= Date(store[4]));
            Assert.True(date < Date(store[9].Length == 0 ? Options.EndDate : store[9]));
            Assert.InRange(Date(row[4]).DayNumber - date.DayNumber, 9, 13);
            if (row[5].Length > 0) Assert.True(customers.ContainsKey(row[5]));
            Assert.Contains(row[6], collection);
        }
        foreach (var row in storage.Tables[SampleTables.FileOrderItem])
        {
            Assert.Contains(row[1], orders.Keys);
            Assert.Contains(row[2], products.Keys);
            Assert.Equal(Number(products[row[2]][4]), Number(row[3]));
            Assert.True(Integer(row[5]) >= 1);
            var key = (orders[row[1]][2], row[2]);
            sold[key] = sold.GetValueOrDefault(key) + Integer(row[5]);
        }
        var received = storage.Tables[SampleTables.FileInventory].GroupBy(r => (Store: r[3], Product: r[1])).ToDictionary(g => g.Key, g => g.Sum(r => Integer(r[2])));
        foreach (var (key, quantity) in sold) Assert.InRange(received[key] - quantity, 0, 5);
        Assert.Equal(products.Count, products.Values.Select(r => r[3]).Distinct().Count());
        foreach (var row in products.Values)
        {
            Assert.Matches("^产品[A-Z0-9]{3}$", row[3]);
            Assert.Contains(row[3][2..], char.IsDigit);
            Assert.Contains(row[3][2..], char.IsAsciiLetter);
        }
    }

    /// <summary>1000 个产品保持价格截断, 右偏, 成本低于售价和 A–J 基本覆盖.</summary>
    [Fact]
    public void ProductPricesRetainLognormalDistributionAndCategoryCoverage()
    {
        var storage = new MemoryStorage();
        new SampleDataGenerator(Options with { ProductCount = 1000, StoreCount = 1, StartDate = "2025-01-01", EndDate = "2025-03-02" }, Dataset).GenerateAll(storage);
        var rows = storage.Tables[SampleTables.FileProduct];
        foreach (var row in rows)
        {
            Assert.InRange(Number(row[4]), 1000, 30000);
            Assert.InRange(Number(row[5]), 0, Number(row[4]));
        }
        var prices = rows.Select(r => Number(r[4])).Order().ToArray();
        Assert.True(prices.Average() > prices[prices.Length / 2] * 1.2);
        Assert.True(rows.Select(r => r[2]).Distinct().Count() >= 8);
    }

    /// <summary>目标覆盖首事实年次年至末年年末, 月度完成率为 90–120%, 无实绩月目标为零.</summary>
    [Fact]
    public void SaleTargetsUseActualSalesAnchorsAndPlanToYearEnd()
    {
        var storage = new MemoryStorage();
        new SampleDataGenerator(Options, Dataset).GenerateAll(storage);
        var cityProvince = Dataset.Cities.ToDictionary(c => c.CityId, c => c.ProvinceId);
        var storeProvince = storage.Tables[SampleTables.FileStore].ToDictionary(r => r[1], r => cityProvince[Integer(r[5])]);
        var orderLocations = storage.Tables[SampleTables.FileOrder].ToDictionary(r => r[1], r => (Pid: storeProvince[r[2]], Date: Date(r[3])));
        var min = orderLocations.Values.Min(l => l.Date);
        var max = orderLocations.Values.Max(l => l.Date);
        var startYear = Math.Min(min.Year + 1, max.Year);
        var actual = storage.Tables[SampleTables.FileOrderItem].GroupBy(r => (orderLocations[r[1]].Pid, Ym(orderLocations[r[1]].Date))).ToDictionary(g => g.Key, g => g.Sum(r => Number(r[6])));
        var plan = 0.0;
        foreach (var row in storage.Tables[SampleTables.FileSaleTarget])
        {
            var date = Date(row[3]);
            Assert.InRange(date.Year, startYear, max.Year);
            Assert.Equal(1, date.Day);
            var value = Number(row[4]);
            if (Ym(date) > Ym(max)) plan += value;
            else if (value > 0) Assert.InRange(actual.GetValueOrDefault((Integer(row[1]), Ym(date))) / value, 0.9, 1.2);
            else Assert.Equal(0, actual.GetValueOrDefault((Integer(row[1]), Ym(date))));
        }
        Assert.True(plan > 0);
        Assert.All(storage.Tables[SampleTables.FileSaleTarget].GroupBy(r => Integer(r[1])), g => Assert.Equal((max.Year - startYear + 1) * 12, g.Count()));
    }

    /// <summary>单年度事实仍生成该年全年目标.</summary>
    [Fact]
    public void SingleYearTargetsCoverTwelveMonths()
    {
        var storage = new MemoryStorage();
        new SampleDataGenerator(Options with { StartDate = "2025-01-01", EndDate = "2025-05-15" }, Dataset).GenerateAll(storage);
        Assert.NotEmpty(storage.Tables[SampleTables.FileSaleTarget]);
        Assert.All(storage.Tables[SampleTables.FileSaleTarget], row => Assert.Equal(2025, Date(row[3]).Year));
        Assert.All(storage.Tables[SampleTables.FileSaleTarget].GroupBy(r => r[1]), group => Assert.Equal(12, group.Count()));
    }

    /// <summary>增量支持前置或后置窗口, 端点包含, 已关店跳过, 全量排除结束日.</summary>
    [Fact]
    public void IncrementalWindowIncludesEndAndClosedStoreIntersection()
    {
        var storage = new MemoryStorage();
        new SampleDataGenerator(Options with { StoreCount = 1, ProductCount = 1, StartDate = "2025-01-01", EndDate = "2025-05-01" }, Dataset).GenerateAll(storage);
        var store = storage.Tables[SampleTables.FileStore][0];
        store[4] = "2024-01-01";
        store[9] = "2025-07-15";
        var baseline = storage.Tables.ToDictionary(p => p.Key, p => p.Value.Select(r => r.ToArray()).ToList());
        var before = storage.Tables[SampleTables.FileOrder].Count;
        var options = Options with { StoreCount = 1, ProductCount = 1, StartDate = "2025-05-02", EndDate = "2025-08-31", Seed = 1 };
        new SampleDataGenerator(options, Dataset).AppendIncremental(storage);
        var added = storage.Tables[SampleTables.FileOrder].Skip(before).ToArray();
        Assert.NotEmpty(added);
        Assert.All(added, row => Assert.InRange(Date(row[3]), Date(options.StartDate), Date("2025-07-15")));
        // 单店省份低量级可在任意一天无单, 因此先定位同种子的第一个已知有订单日.
        // 将关店日缩到该日再重放独立基线, 其前每日无订单, 随机序列一致, 可精确验证端点包含.
        var endpoint = added.Min(r => Date(r[3]));
        var endpointStorage = new MemoryStorage();
        foreach (var (file, rows) in baseline) endpointStorage.Tables[file] = [.. rows.Select(r => r.ToArray())];
        endpointStorage.Tables[SampleTables.FileStore][0][9] = endpoint.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        new SampleDataGenerator(options, Dataset).AppendIncremental(endpointStorage);
        Assert.Contains(endpointStorage.Tables[SampleTables.FileOrder].Skip(before), row => Date(row[3]) == endpoint);
        var zero = new SampleDataGenerator(options with { StartDate = "2025-09-01", EndDate = "2025-11-15" }, Dataset).AppendIncremental(storage);
        Assert.Equal(0, zero.Orders);
        var prior = new SampleDataGenerator(options with { StartDate = "2024-01-01", EndDate = "2024-05-01" }, Dataset).AppendIncremental(storage);
        Assert.True(prior.Orders > 0);
    }

    /// <summary>增量自动编号与业务编号均续接最大值, 无关维度不重生成.</summary>
    [Fact]
    public void IncrementalContinuesIdsAndDoesNotModifyEarlierFactTargets()
    {
        var storage = new MemoryStorage();
        new SampleDataGenerator(Options, Dataset).GenerateAll(storage);
        var before = storage.Tables[SampleTables.FileSaleTarget].ToDictionary(r => (r[1], r[3]), r => r[4]);
        var priorMaxYm = storage.Tables[SampleTables.FileOrder].Max(r => Ym(Date(r[3])));
        var dimensions = storage.Tables.Where(p => p.Key is not (SampleTables.FileInventory or SampleTables.FileOrder or SampleTables.FileOrderItem or SampleTables.FileSaleTarget)).ToDictionary(p => p.Key, p => p.Value.Select(r => string.Join('|', r)).ToArray());
        var result = new SampleDataGenerator(Options with { StartDate = "2025-06-02", EndDate = "2025-12-01" }, Dataset).AppendIncremental(storage);
        Assert.True(result.Orders > 0);
        foreach (var file in new[] { SampleTables.FileInventory, SampleTables.FileOrder, SampleTables.FileOrderItem })
            Assert.Equal(Enumerable.Range(1, storage.Tables[file].Count), storage.Tables[file].Select(r => Integer(r[0])));
        Assert.Equal(storage.Tables[SampleTables.FileOrder].Count, storage.Tables[SampleTables.FileOrder].Select(r => r[1]).Distinct().Count());
        foreach (var (file, values) in dimensions) Assert.Equal(values, storage.Tables[file].Select(r => string.Join('|', r)));
        var after = storage.Tables[SampleTables.FileSaleTarget].ToDictionary(r => (r[1], r[3]), r => r[4]);
        foreach (var (key, value) in before.Where(p => Ym(Date(p.Key.Item2)) <= priorMaxYm)) Assert.Equal(value, after[key]);
        storage.Tables.Remove(SampleTables.FileSaleTarget);
        new SampleDataGenerator(Options with { StartDate = "2026-01-01", EndDate = "2026-04-01" }, Dataset).AppendIncremental(storage);
        Assert.False(storage.Exists(SampleTables.FileSaleTarget));
    }

    /// <summary>冲突必须在追加写入器之前失败, 当前订单业务编号和自动编号均从最大值续接.</summary>
    [Fact]
    public void IncrementalRejectsOverlapBeforeAppendAndContinuesCurrentFactIds()
    {
        var storage = new MemoryStorage();
        new SampleDataGenerator(Options, Dataset).GenerateAll(storage);
        var count = storage.Tables[SampleTables.FileOrder].Count;
        Assert.Throws<SampleDateConflictException>(() => new SampleDataGenerator(Options, Dataset).AppendIncremental(storage));
        Assert.Equal(count, storage.Tables[SampleTables.FileOrder].Count);
        Assert.Equal(0, storage.AppendCount);
        var maxOrder = storage.Tables[SampleTables.FileOrder].Max(r => Integer(r[1][3..]));
        var maxId = storage.Tables[SampleTables.FileOrder].Max(r => Integer(r[0]));
        new SampleDataGenerator(Options with { StartDate = "2025-06-02", EndDate = "2025-09-01" }, Dataset).AppendIncremental(storage);
        Assert.Equal("OC_" + (maxOrder + 1).ToString("D7", CultureInfo.InvariantCulture), storage.Tables[SampleTables.FileOrder][count][1]);
        Assert.Equal((maxId + 1).ToString(CultureInfo.InvariantCulture), storage.Tables[SampleTables.FileOrder][count][0]);
    }

    /// <summary>任一事实表缺少自动编号时在追加前拒绝, 不按业务编号或行宽推测旧格式.</summary>
    [Theory]
    [InlineData(SampleTables.FileInventory)]
    [InlineData(SampleTables.FileOrder)]
    [InlineData(SampleTables.FileOrderItem)]
    public void IncrementalRejectsMissingAutoIdBeforeAppend(string file)
    {
        var storage = new MemoryStorage();
        new SampleDataGenerator(Options, Dataset).GenerateAll(storage);
        Assert.NotEmpty(storage.Tables[file]);
        storage.Tables[file][0] = storage.Tables[file][0][1..];
        var before = storage.Tables.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => string.Join('|', row)).ToArray());
        var error = Assert.Throws<AppException>(() => new SampleDataGenerator(Options with { StartDate = "2025-06-02", EndDate = "2025-09-01" }, Dataset).AppendIncremental(storage));
        Assert.Equal("SAMPLE_DATA_INVALID", error.Code);
        Assert.Equal(0, storage.AppendCount);
        foreach (var (name, rows) in before) Assert.Equal(rows, storage.Tables[name].Select(row => string.Join('|', row)));
    }

    /// <summary>配置边界保持上游范围, 预取消不写任何表, 同生成器不允许重放.</summary>
    [Fact]
    public void ConfigurationCancellationAndSingleRunBoundaries()
    {
        Assert.Throws<AppException>(() => (Options with { ProductCount = 0 }).Validate());
        Assert.Throws<AppException>(() => (Options with { StoreCount = 10001 }).Validate());
        Assert.Throws<AppException>(() => (Options with { InventoryCycle = 4 }).Validate());
        Assert.Throws<AppException>(() => (Options with { InventoryCycle = 181 }).Validate());
        Assert.Throws<AppException>(() => (Options with { StartDate = "2025-05-01", EndDate = "2025-06-01" }).Validate());
        Assert.Throws<AppException>(() => (Options with { StartDate = "2025-02-29" }).Validate());
        var storage = new MemoryStorage();
        Assert.ThrowsAny<OperationCanceledException>(() => new SampleDataGenerator(Options, Dataset).GenerateAll(storage, new CancellationToken(true)));
        Assert.Empty(storage.Tables);
        var generator = new SampleDataGenerator(Options with { ProductCount = 1, StoreCount = 1, StartDate = "2025-01-01", EndDate = "2025-03-02" }, Dataset);
        generator.GenerateAll(storage);
        Assert.Throws<InvalidOperationException>(() => generator.GenerateAll(storage));
    }

    /// <summary>全量固定种子可复现 C# 产物, 上游与 C# 的随机序列不要求逐字节相同.</summary>
    [Fact]
    public void FixedSeedReproducesAllTables()
    {
        var first = new MemoryStorage(); var second = new MemoryStorage();
        new SampleDataGenerator(Options, Dataset).GenerateAll(first);
        new SampleDataGenerator(Options, Dataset).GenerateAll(second);
        foreach (var file in SampleTables.AllFiles) Assert.Equal(first.Tables[file].Select(r => string.Join('|', r)), second.Tables[file].Select(r => string.Join('|', r)));
    }

    /// <summary>十种子真实生成验证默认五梯队份额与噪声预算, 事实仅保留流式聚合.</summary>
    [Fact]
    public void ProvinceSalesDistributionRemainsWithinUpstreamStatisticalBands()
    {
        (double Min, double Max)[] shares = [(0.42, 0.58), (0.16, 0.30), (0.10, 0.19), (0.06, 0.13), (0.015, 0.048)];
        int[] inversionsAllowed = [30, 8, 17, 9];
        var headInversions = 0;
        for (var seed = 1; seed <= 10; seed++)
        {
            var output = new ProvinceAggregateStorage(Dataset);
            var generator = new SampleDataGenerator(Options with { ProductCount = 120, StoreCount = 55, StartDate = "2023-08-07", EndDate = "2026-08-31", Seed = seed }, Dataset);
            generator.GenerateAll(output);
            var model = generator.ProvinceWeights;
            // 上游配额使用无偏随机舍入, 55 店的尾省期望可低于 1, 合法种子可出现零店.
            // 权重必须完整覆盖全部省, 实际销售分布只对有正实绩的省份检查逆序, 与 Go positive() 一致.
            Assert.NotEmpty(output.Sales);
            Assert.All(output.Sales, p => { Assert.Contains(p.Key, model.Quota.Keys); Assert.True(p.Value > 0); });
            var total = output.Sales.Values.Sum();
            for (var b = 0; b < model.Blocks.Count; b++)
            {
                var share = model.Blocks[b].Sum(pid => output.Sales.GetValueOrDefault(pid)) / total;
                Assert.True(share >= shares[b].Min && share <= shares[b].Max, $"种子 {seed}, 梯队 {b}, 份额 {share:P2}");
                if (b + 1 < model.Blocks.Count)
                {
                    var bad = model.Blocks[b].Where(output.Sales.ContainsKey).Sum(pid => model.Blocks[b + 1].Count(next => output.Sales.ContainsKey(next) && output.Sales[pid] < output.Sales[next]));
                    Assert.True(bad <= inversionsAllowed[b], $"种子 {seed}, 梯队 {b}, 逆序 {bad}");
                    if (b == 0) headInversions += bad;
                }
                if (b + 2 < model.Blocks.Count)
                {
                    var bad = model.Blocks[b].Where(output.Sales.ContainsKey).Sum(pid => model.Blocks[b + 2].Count(next => output.Sales.ContainsKey(next) && output.Sales[pid] <= output.Sales[next]));
                    Assert.True(bad <= (b == 0 ? 6 : 0), $"种子 {seed}, 梯队 {b}/{b + 2}, 相隔逆序 {bad}");
                }
            }
        }
        Assert.True(headInversions / 10.0 < 20);
    }

    /// <summary>误差限制为双精度计算误差, 不用舍入隐藏散列规则差异.</summary>
    private static void AssertClose(double expected, double actual) => Assert.True(Math.Abs(expected - actual) < 1e-13, $"expected {expected:R}, actual {actual:R}");
    /// <summary>按文化不变格式解析测试 CSV 日期.</summary>
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    /// <summary>解析测试 CSV 数字.</summary>
    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    /// <summary>解析测试 CSV 整数.</summary>
    private static int Integer(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    /// <summary>转换年月键.</summary>
    private static int Ym(DateOnly date) => date.Year * 100 + date.Month;

    /// <summary>小规模逻辑验收用表存储, 与生产流式接口一致.</summary>
    private sealed class MemoryStorage : ISampleTableStorage
    {
        public Dictionary<string, List<string[]>> Tables { get; } = [];
        public int AppendCount { get; private set; }
        /// <inheritdoc/>
        public ISampleRowWriter Create(string fileName, IReadOnlyList<string> header) { Tables[fileName] = []; return new RowWriter(row => Tables[fileName].Add([.. row])); }
        /// <inheritdoc/>
        public ISampleRowWriter Append(string fileName) { AppendCount++; return new RowWriter(row => Tables[fileName].Add([.. row])); }
        /// <inheritdoc/>
        public IEnumerable<string[]> Read(string fileName) => Tables[fileName];
        /// <inheritdoc/>
        public bool Exists(string fileName) => Tables.ContainsKey(fileName);
    }

    /// <summary>大规模分布测试只保留订单到省的关联和销售聚合, 不缓存明细表.</summary>
    private sealed class ProvinceAggregateStorage(SampleDataset dataset) : ISampleTableStorage
    {
        private readonly Dictionary<int, int> cityProvince = dataset.Cities.ToDictionary(c => c.CityId, c => c.ProvinceId);
        private readonly Dictionary<string, int> storeProvince = [];
        private readonly Dictionary<string, int> orderProvince = [];
        public Dictionary<int, double> Sales { get; } = [];
        /// <inheritdoc/>
        public ISampleRowWriter Create(string fileName, IReadOnlyList<string> header) => new RowWriter(row =>
        {
            if (fileName == SampleTables.FileStore) storeProvince[row[1]] = cityProvince[Integer(row[5])];
            else if (fileName == SampleTables.FileOrder) orderProvince[row[1]] = storeProvince[row[2]];
            else if (fileName == SampleTables.FileOrderItem) { var pid = orderProvince[row[1]]; Sales[pid] = Sales.GetValueOrDefault(pid) + Number(row[6]); }
        });
        /// <inheritdoc/>
        public ISampleRowWriter Append(string fileName) => throw new NotSupportedException();
        /// <inheritdoc/>
        public IEnumerable<string[]> Read(string fileName) => throw new NotSupportedException();
        /// <inheritdoc/>
        public bool Exists(string fileName) => false;
    }

    /// <summary>测试行接收器; 生产路径由独立真实 CSV 存储测试验证.</summary>
    private sealed class RowWriter(Action<IReadOnlyList<string>> write) : ISampleRowWriter
    {
        /// <inheritdoc/>
        public void Write(IReadOnlyList<string> row) => write(row);
        /// <inheritdoc/>
        public void Dispose() { }
    }
}
