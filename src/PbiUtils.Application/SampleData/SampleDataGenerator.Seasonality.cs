// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleDataGenerator.Seasonality.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 完整移植分类, 行业, 大区季节谐波, 年度漂移和毛利率交互规则.

namespace PbiUtils.Application.SampleData;

public sealed partial class SampleDataGenerator
{
    private static readonly (double Amp, double Peak)[] CategorySeasonParameters = [(0.24, 9.5), (0.22, 10.5), (0.20, 1), (0.19, 3), (0.18, 7), (0.17, 5), (0.15, 8.5), (0.13, 4), (0.10, 2.5), (0.07, 11)];
    private static readonly (double Amp, double Peak)[] RegionSeasonParameters = [(0.55, 9), (0.50, 4), (0.45, 11), (0.60, 7.5), (0.50, 2.5), (0.55, 1)];
    private static readonly (double Amp, double Peak)[] IndustrySeasonParameters = [(0.60, 4.4), (0.45, 7.9), (0.55, 6.1), (0.50, 9.6), (0.70, 1), (0.60, 11.3), (0.50, 2.7)];
    private static readonly double[] MarginRegionAmplitude = [0.18, 0.16, 0.15, 0.20, 0.16, 0.18];
    private static readonly double[] MarginRegionNormalization = MarginNormalizations();
    private const double DiscountSeasonKappa = 0.4;
    private const double MarginRegionKappa = 0.55;
    private const int DiscountSeasonLag = 3;

    /// <summary>归一化大区让利的销量加权年均值, 只保留月度形态, 消除年度偏移.</summary>
    private static double[] MarginNormalizations()
    {
        var result = new double[RegionSeasonParameters.Length];
        for (var r = 0; r < result.Length; r++)
        {
            var (amp, peak) = RegionSeasonParameters[r];
            double weighted = 0, volume = 0;
            for (var m = 1; m <= 12; m++)
            {
                var v = MonthTrend[m - 1] * SeasonHarmonic(amp, peak, m);
                var f = 1 - MarginRegionKappa * (SeasonHarmonic(MarginRegionAmplitude[r], peak + DiscountSeasonLag, m) - 1);
                weighted += v * f;
                volume += v;
            }
            result[r] = weighted / volume;
        }
        return result;
    }

    /// <summary>年均值为 1 的月度余弦谐波, 旺月可为小数.</summary>
    internal static double SeasonHarmonic(double amplitude, double peak, int month) => 1 + amplitude * Math.Cos(2 * Math.PI * (month - peak) / 12);

    /// <summary>维度与年份导出的年度漂移, 保留上游无符号 64 位溢出语义.</summary>
    internal static double SeasonYearDrift(int dimension, int year, double amplitude)
    {
        var x = unchecked((ulong)dimension * 0x9E3779B97F4A7C15 + (ulong)year * 2654435761);
        x ^= x >> 13;
        x = unchecked(x * 1274126177);
        x ^= x >> 16;
        return 1 - amplitude + 2 * amplitude * (x % 1000003) / 1000003.0;
    }

    /// <summary>月度谐波乘确定性年度漂移.</summary>
    private static double DimensionSeason((double Amp, double Peak) parameter, int dimension, int year, int month, double drift) =>
        SeasonHarmonic(parameter.Amp, parameter.Peak, month) * SeasonYearDrift(dimension, year, drift);

    /// <summary>分类 A–J 对应 0–9, 未知分类为 -1.</summary>
    private static int CategoryIndex(string category) => category.Length > 0 && category[0] is >= 'A' and <= 'J' ? category[0] - 'A' : -1;

    /// <summary>城市经省份关联大区, 未知城市或大区为 -1.</summary>
    private int RegionIndex(int cityId)
    {
        if (!_cityToProvince.TryGetValue(cityId, out var pid) || !_provinceById.TryGetValue(pid, out var province)) return -1;
        var result = province.RegionId - 1;
        return result is >= 0 and < 6 ? result : -1;
    }

    /// <summary>门店编号的散列索引, 与大区交错序列解耦, 全量/增量一致.</summary>
    internal static int StoreFactorIndex(int id, int count)
    {
        var x = unchecked((ulong)id * 0x9E3779B97F4A7C15 + 0xABC98372F);
        x ^= x >> 13;
        x = unchecked(x * 1274126177);
        x ^= x >> 16;
        return (int)(x % (ulong)count);
    }

    /// <summary>大区季节与年度漂移同时作用于订单数和每单种类数, 未知大区为 1.</summary>
    private double RegionOrderSeasonFactor(Store store, int year, int month)
    {
        var index = RegionIndex(store.CityId);
        return index < 0 ? 1 : DimensionSeason(RegionSeasonParameters[index], 100 + index, year, month, 0.10);
    }

    /// <summary>分类季节与生命周期年度漂移作用于产品数量, 未知分类为 1.</summary>
    internal static double CategorySeasonFactor(string category, int year, int month)
    {
        var index = CategoryIndex(category);
        return index < 0 ? 1 : DimensionSeason(CategorySeasonParameters[index], index, year, month, 0.40);
    }

    /// <summary>客户行业季节同时作用于种类数和产品数量, 无客户或未知行业为 1.</summary>
    private double IndustrySeasonFactor(int customerIndex, int year, int month)
    {
        if (customerIndex < 0 || customerIndex >= _customers.Count) return 1;
        var index = Array.IndexOf(Industries, _customers[customerIndex].Industry);
        return index < 0 ? 1 : DimensionSeason(IndustrySeasonParameters[index], 200 + index, year, month, 0.20);
    }

    /// <summary>折扣修正叠加大区水平漂移, 旺季滞后三月让利和分类 x 大区年度交互.</summary>
    private double DiscountSeasonFactor(Store store, string category, int year, int month)
    {
        var region = RegionIndex(store.CityId);
        var factor = 1.0;
        if (region >= 0)
        {
            factor *= SeasonYearDrift(300 + region, year, 0.06);
            factor *= (1 - MarginRegionKappa * (SeasonHarmonic(MarginRegionAmplitude[region], RegionSeasonParameters[region].Peak + DiscountSeasonLag, month) - 1)) / MarginRegionNormalization[region];
        }
        var categoryIndex = CategoryIndex(category);
        if (categoryIndex >= 0)
        {
            var (Amp, Peak) = CategorySeasonParameters[categoryIndex];
            factor *= 1 - DiscountSeasonKappa * (SeasonHarmonic(Amp, Peak + DiscountSeasonLag, month) - 1);
            if (region >= 0) factor *= SeasonYearDrift(400 + categoryIndex * 6 + region, year, 0.05);
        }
        // 上游 orders.go 不截断折扣: 大于 1 的系数允许旺季溢价.
        // 修正: 上游 seasonality.go 的旧注释称调用处保证不超过 1, 与实际代码不符.
        return factor;
    }
}
