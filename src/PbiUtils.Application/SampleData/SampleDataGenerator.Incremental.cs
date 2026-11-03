// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleDataGenerator.Incremental.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 完整移植增量维度复用, 日期冲突, 事实续号和全部实绩目标重建.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.SampleData;

public sealed partial class SampleDataGenerator
{
    /// <summary>复用既有产品/门店/客户, 追加窗口内事实, 从全部实绩重建已有目标表.</summary>
    /// <param name="storage">staging 中的既有示例表存储, 调用方负责整体发布/回滚.</param>
    /// <param name="cancellationToken">取消读取或生成; 不将部分 staging 宣布为成功.</param>
    /// <returns>既有维度数和本次新增事实行数.</returns>
    /// <exception cref="SampleDateConflictException">增量窗口与已有订单日期区间交叉.</exception>
    public SampleGenerationResult AppendIncremental(ISampleTableStorage storage, CancellationToken cancellationToken = default)
    {
        Begin(cancellationToken);
        foreach (var file in new[] { SampleTables.FileProduct, SampleTables.FileStore, SampleTables.FileCustomer, SampleTables.FileOrder })
            if (!storage.Exists(file)) throw new AppException("SAMPLE_NO_BASE_DATA", "该目录没有基础示例数据, 请先全量生成.", 3);
        LoadExistingDimensions(storage);
        var (number, id, min, max) = ScanExistingOrders(storage);
        if (min is { } existingMin && max is { } existingMax && _start <= existingMax && _end >= existingMin)
            throw new SampleDateConflictException(existingMin, existingMax);
        _idOrder = id;
        _idInventory = ScanMaxAutoId(storage, SampleTables.FileInventory);
        _idItem = ScanMaxAutoId(storage, SampleTables.FileOrderItem);
        GenerateOrders(storage, true, number);
        // 用户删除目标表时保持 "无目标表" 状态, 不偷偷重新创建.
        if (storage.Exists(SampleTables.FileSaleTarget))
        {
            LoadProvinceMonthSales(storage);
            if (_factMinYm != 0) GenerateSaleTargets(storage);
        }
        Report(100, "stageDone");
        return Result();
    }

    /// <summary>读取 T00/T01/T02, 门店衍生系数按 CSV 行序和可重建规模计算.</summary>
    private void LoadExistingDimensions(ISampleTableStorage storage)
    {
        foreach (var r in storage.Read(SampleTables.FileProduct))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RequireColumns(r, 6);
            _products.Add(new(ParseInt(r[0]), r[1], r[2], r[3], ParseFloat(r[4]), ParseFloat(r[5])));
        }
        foreach (var r in storage.Read(SampleTables.FileStore))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RequireColumns(r, 10);
            _stores.Add(new()
            {
                Id = ParseInt(r[0]),
                Code = r[1],
                Name = r[2],
                Manager = r[3],
                OpenDate = ParseRequiredDate(r[4]),
                CityId = ParseInt(r[5]),
                City = r[6],
                Lat = ParseFloat(r[7]),
                Lng = ParseFloat(r[8]),
                CloseDate = string.IsNullOrWhiteSpace(r[9]) ? null : ParseRequiredDate(r[9])
            });
        }
        AssignStoreTraffic();
        AssignProvinceStoreComp();
        AssignDiscountClasses();
        foreach (var r in storage.Read(SampleTables.FileCustomer))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RequireColumns(r, 8);
            _customers.Add(new(ParseInt(r[0]), r[1], r[2], ParseRequiredDate(r[3]), r[4], ParseRequiredDate(r[5]), r[6], r[7]));
        }
        if (_products.Count == 0) throw new AppException("SAMPLE_NO_BASE_DATA", "现有产品表为空, 请先全量生成.", 3);
    }

    /// <summary>扫描当前订单表的业务编号, 自动编号和日期范围.</summary>
    private (int OrderNumber, int AutoId, DateOnly? Min, DateOnly? Max) ScanExistingOrders(ISampleTableStorage storage)
    {
        var orderMax = 0; var idMax = 0;
        DateOnly? min = null, max = null;
        foreach (var r in storage.Read(SampleTables.FileOrder))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var (id, code, _, date) = OrderRowParts(r);
            var underscore = code.LastIndexOf('_');
            var number = underscore >= 0 ? ParseInt(code[(underscore + 1)..]) : 0;
            orderMax = Math.Max(orderMax, number);
            idMax = Math.Max(idMax, ParseInt(id));
            if (ParseDate(date) is not { } d) continue;
            if (min is null || d < min) min = d;
            if (max is null || d > max) max = d;
        }
        return (orderMax, idMax, min, max);
    }

    /// <summary>扫描当前完整事实表的最大自动编号, 缺列时拒绝增量.</summary>
    private int ScanMaxAutoId(ISampleTableStorage storage, string file)
    {
        var max = 0;
        foreach (var row in storage.Read(file))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RequireColumns(row, SampleTables.Header(file).Count);
            max = Math.Max(max, ParseInt(row[0]));
        }
        return max;
    }

    /// <summary>先建立轻量订单→省月索引, 再逐行读取明细累计, 不缓存整张事实表.</summary>
    private void LoadProvinceMonthSales(ISampleTableStorage storage)
    {
        _provinceMonth.Clear();
        _factMinYm = 0; _factMaxYm = 0;
        var storeCity = _stores.ToDictionary(s => s.Code, s => s.CityId);
        Dictionary<string, (int Pid, int Ym)> locations = [];
        foreach (var row in storage.Read(SampleTables.FileOrder))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var (_, code, store, text) = OrderRowParts(row);
            if (code.Length == 0 || !storeCity.TryGetValue(store, out var cityId) || !_cityToProvince.TryGetValue(cityId, out var pid) || ParseDate(text) is not { } date) continue;
            var ym = date.Year * 100 + date.Month;
            locations[code] = (pid, ym);
            TrackFactMonth(ym);
        }
        foreach (var row in storage.Read(SampleTables.FileOrderItem))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RequireColumns(row, SampleTables.Header(SampleTables.FileOrderItem).Count);
            var code = row[1];
            var amount = row[6];
            if (!locations.TryGetValue(code, out var location) || amount.Length == 0) continue;
            var key = new MonthKey(location.Pid, location.Ym / 100, location.Ym % 100);
            _provinceMonth[key] = _provinceMonth.GetValueOrDefault(key) + ParseFloat(amount);
        }
    }

    /// <summary>提取当前完整 T04 的自动编号, 业务编号, 门店和日期; 缺列时拒绝增量.</summary>
    private static (string Id, string Code, string Store, string Date) OrderRowParts(string[] row)
    {
        RequireColumns(row, SampleTables.Header(SampleTables.FileOrder).Count);
        return (row[0], row[1], row[2], row[3]);
    }

    /// <summary>要求数据行与当前固定列数一致, 避免格式漂移进入业务计算.</summary>
    private static void RequireColumns(string[] row, int count)
    {
        if (row.Length != count) throw new AppException("SAMPLE_DATA_INVALID", "示例 CSV 数据行字段数与当前结构不一致.", 3);
    }

    /// <summary>按固定文化宽松解析整数, 无效输入返回 0.</summary>
    private static int ParseInt(string text) => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>宽松有限浮点解析, 与上游失败取 0 一致, 非有限输入不进入业务计算.</summary>
    private static double ParseFloat(string text) => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : 0;

    /// <summary>文化无关日期解析, 无效时返回 null.</summary>
    private static DateOnly? ParseDate(string text) => DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>必需维度日期非法时拒绝增量, 避免上游零值日期导致溢出或超量生成.</summary>
    private static DateOnly ParseRequiredDate(string text) => ParseDate(text) ?? throw new AppException("SAMPLE_DATA_INVALID", "示例维度 CSV 包含无效日期.", 3);
}
