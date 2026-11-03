// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleDataContracts.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 示例数据的配置, 地理数据与流式表存储契约.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.SampleData;

/// <summary>生成配置, 产品/门店数量和入库周期沿用上游范围, Seed 仅控制随机源.</summary>
public sealed record SampleDataOptions(string Locale, int ProductCount, int StoreCount, int InventoryCycle, string StartDate, string EndDate, int? Seed = null)
{
    /// <summary>解析日期并校验上游范围; 非法配置抛出 INPUT_INVALID.</summary>
    /// <returns>通过校验的起止日期.</returns>
    public (DateOnly Start, DateOnly End) Validate()
    {
        if (Locale is not ("zh-cn" or "en-us") || ProductCount is < 1 or > 10000 || StoreCount is < 1 or > 10000 || InventoryCycle is < 5 or > 180)
            throw new AppException("INPUT_INVALID", "产品和门店数量须为 1–10000, 入库周期须为 5–180, 语言须为 zh-cn 或 en-us.");
        if (!DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end.DayNumber - start.DayNumber < 60)
            throw new AppException("INPUT_INVALID", "日期须为 YYYY-MM-DD, 结束日期须晚于开始日期至少 60 天.");
        // 姓名生日向过去偏移最多约 59 年, 关店候选向未来偏移最多 4870 天.
        // C# DateOnly 的表示边界必须在写入前检查, 避免部分产物后才发生溢出.
        if (end.Year < 61 || end.Year > 9985)
            throw new AppException("INPUT_INVALID", "结束年份须为 61–9985, 以容纳生日与关店日期计算.");
        return (start, end);
    }
}

/// <summary>各业务表行数; 增量时维度数为既有数量, 事实数为本次新增数量.</summary>
public sealed record SampleGenerationResult(int Products, int Stores, int Customers, int Inventory, int Orders, int OrderItem);

/// <summary>总进度百分比与固定阶段键.</summary>
public sealed record SampleDataProgress(double Percent, string Stage);

/// <summary>大区基础记录.</summary>
public sealed record SampleRegion(int RegionId, string Name, string Manager, int CityId, string CityName, double Lat, double Lng);

/// <summary>省份基础记录; Short2 与五梯队定义对应.</summary>
public sealed record SampleProvince(int RegionId, int ProvinceId, string Full, string Short1, string Short2, double Lat, double Lng);

/// <summary>城市基础记录.</summary>
public sealed record SampleCity(int ProvinceId, int CityId, string Name, double Lat, double Lng);

/// <summary>区县基础记录.</summary>
public sealed record SampleDistrict(int CityId, int DistrictId, string Name, double Lat, double Lng);

/// <summary>基础地理及姓名资源, 资源加载由外部适配器负责.</summary>
public sealed record SampleDataset(IReadOnlyList<SampleRegion> Regions, IReadOnlyList<SampleProvince> Provinces, IReadOnlyList<SampleCity> Cities,
    IReadOnlyList<SampleDistrict> Districts, IReadOnlyList<string> FirstNames, IReadOnlyList<string> LastNames);

/// <summary>一次数据表写入, Dispose 必须完成刷新并传播写入失败.</summary>
public interface ISampleRowWriter : IDisposable
{
    /// <summary>写入一行字段; 存储异常直接传播.</summary>
    /// <param name="row">与表头对应的字段.</param>
    void Write(IReadOnlyList<string> row);
}

/// <summary>受限的 CSV 表存储, Application 不依赖文件系统或桌面类型.</summary>
public interface ISampleTableStorage
{
    /// <summary>新建或覆盖指定固定表名, 写入表头; 失败抛出存储异常.</summary>
    ISampleRowWriter Create(string fileName, IReadOnlyList<string> header);
    /// <summary>追加指定表; 既有表缺失或不兼容时失败.</summary>
    ISampleRowWriter Append(string fileName);
    /// <summary>逐行读取数据, 不含表头; 允许上游历史事实表缺失自动编号列.</summary>
    IEnumerable<string[]> Read(string fileName);
    /// <summary>检查指定固定表是否存在.</summary>
    bool Exists(string fileName);
}

/// <summary>增量窗口与既有事实日期交叉, 在开启追加写入器前抛出.</summary>
public sealed class SampleDateConflictException(DateOnly existingMin, DateOnly existingMax)
    : Exception($"增量日期与已有订单区间 [{existingMin:yyyy-MM-dd}, {existingMax:yyyy-MM-dd}] 重叠.")
{
    /// <summary>既有最早事实日期.</summary>
    public DateOnly ExistingMin { get; } = existingMin;
    /// <summary>既有最晚事实日期.</summary>
    public DateOnly ExistingMax { get; } = existingMax;
}
