// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleDataGenerator.Orders.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 完整移植流式订单, 折扣, 库存结算与实绩锚定销售目标规则.

namespace PbiUtils.Application.SampleData;

public sealed partial class SampleDataGenerator
{
    /// <summary>三事实表独立流式写出; using 确保中途失败仍释放已打开的写入器.</summary>
    private void GenerateOrders(ISampleTableStorage storage, bool incremental, int startingOrderNumber)
    {
        using var inventory = incremental ? storage.Append(SampleTables.FileInventory) : storage.Create(SampleTables.FileInventory, SampleTables.HeaderInventory);
        using var order = incremental ? storage.Append(SampleTables.FileOrder) : storage.Create(SampleTables.FileOrder, SampleTables.HeaderOrder);
        using var item = incremental ? storage.Append(SampleTables.FileOrderItem) : storage.Create(SampleTables.FileOrderItem, SampleTables.HeaderOrderItem);
        var orderNumber = startingOrderNumber;
        for (var i = 0; i < _stores.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var store = _stores[i];
            if (incremental) GenerateStoreOrdersRange(inventory, order, item, store, ref orderNumber);
            else GenerateStoreOrders(inventory, order, item, store, ref orderNumber);
            Report(incremental ? (i + 1) * 100.0 / _stores.Count : 55 + (i + 1) * 35.0 / _stores.Count, "stageOrders");
        }
    }

    /// <summary>全量按营业天数遍历, 结束日不生成订单; 保留随机 5–7 日补货调度.</summary>
    private void GenerateStoreOrders(ISampleRowWriter inventory, ISampleRowWriter order, ISampleRowWriter item, Store store, ref int orderNumber)
    {
        var end = store.CloseDate ?? _end;
        var days = end.DayNumber - store.OpenDate.DayNumber;
        if (days < 1) return;
        var inventoryDays = BuildInventoryDays(days);
        var pointer = 0;
        Dictionary<string, int> quantities = [];
        List<string> keys = [];
        for (var day = 1; day <= days; day++)
        {
            var date = store.OpenDate.AddDays(day - 1);
            GenerateDayOrders(order, item, store, date, day, ref orderNumber, quantities, keys);
            if (pointer < inventoryDays.Count && inventoryDays[pointer] == day && day < days)
            {
                pointer++;
                FlushInventory(inventory, store, date, quantities, keys, 0);
                quantities.Clear(); keys.Clear();
            }
            else if (day == days)
            {
                FlushInventory(inventory, store, date, quantities, keys, RoundInt(_random.NextDouble() * 5));
                quantities.Clear(); keys.Clear();
            }
        }
    }

    /// <summary>增量按自然日期交集遍历, 包含结束日和关店日, 每入库周期结算.</summary>
    private void GenerateStoreOrdersRange(ISampleRowWriter inventory, ISampleRowWriter order, ISampleRowWriter item, Store store, ref int orderNumber)
    {
        var naturalEnd = store.CloseDate is { } close && close < _end ? close : _end;
        var from = store.OpenDate > _start ? store.OpenDate : _start;
        var to = naturalEnd < _end ? naturalEnd : _end;
        if (from > to) return;
        Dictionary<string, int> quantities = [];
        List<string> keys = [];
        var day = 0;
        for (var dayNumber = from.DayNumber; dayNumber <= to.DayNumber; dayNumber++)
        {
            var date = DateOnly.FromDayNumber(dayNumber);
            day++;
            GenerateDayOrders(order, item, store, date, day, ref orderNumber, quantities, keys);
            if ((day % _options.InventoryCycle == 0 && date != to) || date == to)
            {
                var extra = date == to ? RoundInt(_random.NextDouble() * 5) : 0;
                FlushInventory(inventory, store, date, quantities, keys, extra);
                quantities.Clear(); keys.Clear();
            }
        }
    }

    /// <summary>无偏日订单数, 客户搜索, 渠道与送货日期共用全量/增量规则.</summary>
    private void GenerateDayOrders(ISampleRowWriter order, ISampleRowWriter item, Store store, DateOnly date, int day, ref int orderNumber, Dictionary<string, int> quantities, List<string> keys)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var month = date.Month;
        var count = StochasticFloor(_random.NextDouble() * 4 * MonthTrend[month - 1] * CityOrderFactor(store.CityId) * OrderVolumeFactor(store, date));
        var customerMax = _customers.Count - 1;
        for (var i = 1; i <= count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            orderNumber = checked(orderNumber + 1);
            var code = "OC_" + Pad(orderNumber, 7);
            var sj = (_random.NextDouble() + CustomerDistribution[day * i % 12]) / 2;
            var customerIndex = SelectCustomer(store, RoundInt(customerMax * sj), customerMax, orderNumber);
            var customerCode = customerIndex >= 0 && customerIndex <= customerMax ? _customers[customerIndex].Code : "";
            var delivery = date.AddDays(RoundInt(4 * sj + 8) + 1);
            _idOrder = checked(_idOrder + 1);
            order.Write([I(_idOrder), code, store.Code, D(date), D(delivery), customerCode, sj >= 0.7 ? "线下" : "线上"]);
            _nOrders = checked(_nOrders + 1);
            GenerateOrderItems(item, store, customerIndex, code, date, quantities, keys);
        }
    }

    /// <summary>种类数, 人气和分类/行业季节共同决定销量; 折扣允许超过 1 的旺季溢价.</summary>
    private void GenerateOrderItems(ISampleRowWriter writer, Store store, int customerIndex, string orderCode, DateOnly date, Dictionary<string, int> quantities, List<string> keys)
    {
        var productMax = _products.Count - 1;
        var year = date.Year;
        var month = date.Month;
        var kinds = RoundInt(5 * _random.NextDouble() * IndustrySeasonFactor(customerIndex, year, month) * RegionOrderSeasonFactor(store, year, month)) + 1;
        HashSet<int> selected = [];
        List<int> selectionOrder = [];
        for (var n = 1; n <= kinds; n++)
        {
            var productIndex = kinds < 4 ? RoundInt(productMax * _random.NextDouble() / 5) : RoundInt(productMax * _random.NextDouble());
            if (selected.Add(productIndex)) selectionOrder.Add(productIndex);
        }
        foreach (var sku in selectionOrder)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var product = _products[sku];
            var quantity = RoundInt(5 * _random.NextDouble() * UnitCountFactor[StoreFactorIndex(store.Id, UnitCountFactor.Length)] * UnitCountFactor[sku % 5] * ProductPopularity[sku % 29] *
                CategorySeasonFactor(product.Category, year, month) * IndustrySeasonFactor(customerIndex, year, month)) + 1;
            var discount = Discount(store.DiscountClass, sku, customerIndex, month) * DiscountSeasonFactor(store, product.Category, year, month);
            // 金额按未舍入折扣计算, CSV 折扣仅显示 2 位; 保留上游口径而不从显示比例反算.
            var amount = Round(product.SalePrice * quantity * discount, 2);
            _idItem = checked(_idItem + 1);
            writer.Write([I(_idItem), orderCode, product.Code, F(product.SalePrice), F(Round(discount, 2)), I(quantity), F(amount)]);
            _nItems = checked(_nItems + 1);
            if (!quantities.ContainsKey(product.Code)) keys.Add(product.Code);
            quantities[product.Code] = checked(quantities.GetValueOrDefault(product.Code) + quantity);
            AccumulateProvinceSales(store.CityId, date, amount);
        }
    }

    /// <summary>折扣优先级为门店类别, 产品索引, 客户行业, 职业, 默认无折扣.</summary>
    private double Discount(int storeClass, int sku, int customerIndex, int month)
    {
        var coefficient = DiscountMonth[month - 1];
        if (storeClass > 30) return Discounts[0] * coefficient;
        if (storeClass < 10) return Discounts[5] * coefficient;
        if (sku % 8 < 1) return Discounts[1] * coefficient;
        if (sku % 8 > 5) return Discounts[3] * coefficient;
        if (customerIndex >= 0 && customerIndex < _customers.Count)
        {
            var customer = _customers[customerIndex];
            if (DiscountIndustries.Contains(customer.Industry)) return Discounts[2] * coefficient;
            if (DiscountProfessions.Contains(customer.Profession)) return Discounts[4] * coefficient;
        }
        return 1;
    }

    /// <summary>忠实保留上游注册日期/月启发式左右搜索, 包括已关店右搜索第二分支.</summary>
    private int SelectCustomer(Store store, int customerIndex, int customerMax, int orderNumber)
    {
        if (customerMax < 0) return -1;
        customerIndex = Math.Clamp(customerIndex, 0, customerMax);
        var open = store.OpenDate;
        if (store.CloseDate is null)
        {
            if (_customers[customerIndex].Registration >= open && orderNumber % 13 > 6) return customerIndex;
            for (var i = customerIndex; i <= customerMax; i++)
            {
                var registration = _customers[i].Registration;
                if (registration >= open && (registration.Month > 8 || registration.Month % 3 > 1)) return i;
            }
            for (var i = customerIndex; i >= 0; i--)
            {
                var registration = _customers[i].Registration;
                if (registration >= open && (registration.Month <= 8 || registration.Month % 3 <= 1)) return i;
            }
            return customerIndex;
        }
        var close = store.CloseDate.Value;
        var selected = _customers[customerIndex].Registration;
        if (selected >= open && selected < close && orderNumber % 13 < 6) return customerIndex;
        for (var i = customerIndex; i <= customerMax; i++)
        {
            var registration = _customers[i].Registration;
            if (registration >= open && registration < close && registration.Month > 8) return i;
            // 上游此分支不要求注册早于关店; 严格翻译, 不替换为新的客户资格规则.
            if (registration >= open && registration.Month % 3 > 1) return i;
        }
        for (var i = customerIndex; i >= 0; i--)
        {
            var registration = _customers[i].Registration;
            if (registration >= open && registration < close && (registration.Month <= 8 || registration.Month % 3 <= 1)) return i;
        }
        return customerIndex;
    }

    /// <summary>首调度日为营业天数%周期+1, 此后随机 5–7 天, 沿用全量规则.</summary>
    private List<int> BuildInventoryDays(int days)
    {
        var d = days % _options.InventoryCycle + 1;
        List<int> result = [d];
        while (true)
        {
            d += RoundInt(_random.NextDouble() * 2 + 5);
            if (d > days) break;
            result.Add(d);
        }
        return result;
    }

    /// <summary>累计销量结算为入库, 末批每种产品增加相同随机缓冲量.</summary>
    private void FlushInventory(ISampleRowWriter writer, Store store, DateOnly date, Dictionary<string, int> quantities, List<string> keys, int extra)
    {
        foreach (var code in keys)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _idInventory = checked(_idInventory + 1);
            writer.Write([I(_idInventory), code, I(checked(quantities[code] + extra)), store.Code, D(date)]);
            _nInventory = checked(_nInventory + 1);
        }
    }

    /// <summary>年景 x 月度噪声 x 星期 x 客流 x 大区季节 x 省份双通道权重.</summary>
    private double OrderVolumeFactor(Store store, DateOnly date) => YearTrend[date.Year % YearTrend.Length] * MonthNoise[(date.Year * 12 + date.Month) % MonthNoise.Length] *
        WeekdayFactor[(int)date.DayOfWeek] * store.Traffic * RegionOrderSeasonFactor(store, date.Year, date.Month) * ProvinceBaseFactor(store);

    /// <summary>省 x 年 x 月汇总实绩, 同时维护事实月份区间.</summary>
    private void AccumulateProvinceSales(int cityId, DateOnly date, double amount)
    {
        if (!_cityToProvince.TryGetValue(cityId, out var pid)) return;
        var key = new MonthKey(pid, date.Year, date.Month);
        _provinceMonth[key] = _provinceMonth.GetValueOrDefault(key) + amount;
        TrackFactMonth(date.Year * 100 + date.Month);
    }

    /// <summary>记录事实最早及最晚年月.</summary>
    private void TrackFactMonth(int ym)
    {
        if (_factMinYm == 0 || ym < _factMinYm) _factMinYm = ym;
        if (ym > _factMaxYm) _factMaxYm = ym;
    }

    /// <summary>从事实首年次年一月到末年十二月生成目标; 单年度退化为全年.</summary>
    private void GenerateSaleTargets(ISampleTableStorage storage)
    {
        using var writer = storage.Create(SampleTables.FileSaleTarget, SampleTables.HeaderSaleTarget);
        var startYear = _factMinYm / 100 + 1;
        var endYear = _factMaxYm / 100;
        if (startYear > endYear) startYear = endYear;
        var id = 0;
        foreach (var pid in _provinceMonth.Keys.Select(k => k.ProvinceId).Distinct().Order())
        {
            var shortName = _provinceById[pid].Short2;
            var recent = ProvinceRecentMean(pid);
            for (var year = startYear; year <= endYear; year++)
                for (var month = 1; month <= 12; month++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    id++;
                    writer.Write([I(id), I(pid), shortName, D(new DateOnly(year, month, 1)), F(SaleTargetValue(pid, year, month, recent))]);
                }
        }
    }

    /// <summary>事实期以实绩锚定; 计划期以近 12 个月有实绩月均 x 淡旺季外推.</summary>
    private double SaleTargetValue(int pid, int year, int month, double recent)
    {
        var factor = (0.92 + TargetFactorUniform(pid, year * 100 + month) * 0.12) * (1 + TargetYearBias[year % TargetYearBias.Length]);
        return Round(year * 100 + month <= _factMaxYm ? _provinceMonth.GetValueOrDefault(new(pid, year, month)) * factor : recent * MonthTrend[month - 1] * factor, 0);
    }

    /// <summary>省/年月确定性目标系数, 保留上游无符号 64 位溢出语义.</summary>
    internal static double TargetFactorUniform(int pid, int ym)
    {
        var x = unchecked((ulong)pid * 2654435761 + (ulong)ym * 40503);
        x ^= x >> 13;
        x = unchecked(x * 1274126177);
        x ^= x >> 16;
        return (x % 1000003) / 1000003.0;
    }

    /// <summary>指定省近 12 个事实月份的非空月均, 无近月记录时退化为全部非空月均.</summary>
    private double ProvinceRecentMean(int pid)
    {
        var monthIndex = _factMaxYm / 100 * 12 + _factMaxYm % 100 - 1 - 11;
        var start = monthIndex / 12 * 100 + monthIndex % 12 + 1;
        double recentSum = 0, allSum = 0;
        var recentCount = 0; var allCount = 0;
        foreach (var (key, amount) in _provinceMonth)
        {
            if (key.ProvinceId != pid) continue;
            allSum += amount; allCount++;
            if (key.Year * 100 + key.Month >= start) { recentSum += amount; recentCount++; }
        }
        return recentCount > 0 ? recentSum / recentCount : allCount > 0 ? allSum / allCount : 0;
    }
}
