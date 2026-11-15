// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\SampleSalesDistributionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 独立验证十种子真实样例的省份销售分布, 事实仅保留流式聚合.

using System.Globalization;
using PbiUtils.Application.SampleData;
using PbiUtils.Infrastructure.SampleData;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>长时间的统计验收使用独立集合和独占的生成状态.</summary>
[Collection(SampleSalesDistributionTests.CollectionName)]
public sealed class SampleSalesDistributionTests
{
    internal const string CollectionName = "Sample sales distribution";

    /// <summary>十种子真实生成验证默认五梯队份额与噪声预算, 事实仅保留流式聚合.</summary>
    [Fact]
    public void ProvinceSalesDistributionRemainsWithinUpstreamStatisticalBands()
    {
        (double Min, double Max)[] shares = [(0.42, 0.58), (0.16, 0.30), (0.10, 0.19), (0.06, 0.13), (0.015, 0.048)];
        int[] inversionsAllowed = [30, 8, 17, 9];
        var dataset = SampleResources.Load();
        var headInversions = 0;
        for (var seed = 1; seed <= 10; seed++)
        {
            var output = new ProvinceAggregateStorage(dataset);
            var generator = new SampleDataGenerator(new("zh-cn", 120, 55, 14, "2023-08-07", "2026-08-31", seed), dataset);
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

    /// <summary>大规模分布测试只保留订单到省的关联和销售聚合, 不缓存明细表.</summary>
    private sealed class ProvinceAggregateStorage(SampleDataset dataset) : ISampleTableStorage
    {
        private readonly Dictionary<int, int> cityProvince = dataset.Cities.ToDictionary(c => c.CityId, c => c.ProvinceId);
        private readonly Dictionary<string, int> storeProvince = [];
        private readonly Dictionary<string, int> orderProvince = [];
        public Dictionary<int, double> Sales { get; } = [];
        /// <inheritdoc/>
        public ISampleRowWriter Create(string fileName, IReadOnlyList<string> header) => fileName switch
        {
            SampleTables.FileStore => new SampleTestRowWriter(row => storeProvince[row[1]] = cityProvince[int.Parse(row[5], CultureInfo.InvariantCulture)]),
            SampleTables.FileOrder => new SampleTestRowWriter(row => orderProvince[row[1]] = storeProvince[row[2]]),
            SampleTables.FileOrderItem => new SampleTestRowWriter(row =>
            {
                var pid = orderProvince[row[1]];
                Sales[pid] = Sales.GetValueOrDefault(pid) + double.Parse(row[6], CultureInfo.InvariantCulture);
            }),
            _ => new SampleTestRowWriter(_ => { })
        };
        /// <inheritdoc/>
        public ISampleRowWriter Append(string fileName) => throw new NotSupportedException();
        /// <inheritdoc/>
        public IEnumerable<string[]> Read(string fileName) => throw new NotSupportedException();
        /// <inheritdoc/>
        public bool Exists(string fileName) => false;
    }
}
