// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleDataGenerator.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 完整移植上游示例数据生成状态, 全量编排, 产品和客户业务规则.

using System.Globalization;
using PbiUtils.Core;

namespace PbiUtils.Application.SampleData;

/// <summary>纯业务生成器; 随机序列采用 C# 随机源, 字段, 分布及全量/增量规则保留.</summary>
public sealed partial class SampleDataGenerator
{
    private readonly SampleDataOptions _options;
    private readonly SampleDataset _dataset;
    private readonly DateOnly _start;
    private readonly DateOnly _end;
    private readonly Random _random;
    private readonly IProgress<SampleDataProgress>? _progress;
    private readonly int _windowDays;
    private readonly Dictionary<int, int> _cityToProvince;
    private readonly Dictionary<int, SampleProvince> _provinceById;
    private readonly Dictionary<int, SampleCity[]> _provinceCities;
    private readonly Dictionary<int, double> _provinceCityFactor;
    private readonly List<Product> _products = [];
    private readonly List<Store> _stores = [];
    private readonly List<Customer> _customers = [];
    private readonly Dictionary<MonthKey, double> _provinceMonth = [];
    private Dictionary<int, double>? _provinceQuotaWeight;
    private Dictionary<int, double> _provinceVolumeWeight = [];
    private readonly Dictionary<int, double> _provinceStoreComp = [];
    private double _provinceVolumeScale;
    private int[][] _provinceBlocks = [];
    private ProvinceWeightModel? _provinceWeightModel;
    private int _factMinYm;
    private int _factMaxYm;
    private int _nOrders;
    private int _nItems;
    private int _nInventory;
    private int _idOrder;
    private int _idItem;
    private int _idInventory;
    private bool _used;
    private CancellationToken _cancellationToken;

    private sealed record Product(int Id, string Code, string Category, string Name, double SalePrice, double CostPrice);
    private sealed record Customer(int Id, string Code, string Name, DateOnly Birth, string Gender, DateOnly Registration, string Industry, string Profession);
    private sealed class Store
    {
        public int Id { get; init; }
        public required string Code { get; init; }
        public required string Name { get; init; }
        public required string Manager { get; init; }
        public DateOnly OpenDate { get; set; }
        public int CityId { get; init; }
        public required string City { get; init; }
        public double Lat { get; init; }
        public double Lng { get; init; }
        public DateOnly? CloseDate { get; set; }
        public int DiscountClass { get; set; }
        public double Traffic { get; set; }
    }
    private readonly record struct MonthKey(int ProvinceId, int Year, int Month);

    private static readonly double[] AgeDistribution = [0, 0.1, 0.2, 0.3, 0.3, 0.3, 0.3, 0.8, 0.8, 0.8, 0.9, 1];
    private static readonly string[] Industries = ["建筑业", "制造业", "互联网", "农业", "餐饮", "物流", "汽车"];
    private static readonly string[] Professions = ["个体户", "HR", "运营", "IT", "财务", "销售", "研发"];
    private static readonly double[] IndustryDistribution = [0.2, 0.5, 0.5, 0.8, 0.8, 1, 0.9];
    private static readonly double[] ProfessionDistribution = [0.3, 0.7, 0.6, 1, 1, 0.8, 0.1];
    private static readonly double[] UnitCountFactor = [0.7, 0.8, 1, 1.2, 1.3];
    private static readonly double[] MonthTrend = [1, 0.5, 0.9, 1, 1.2, 0.9, 0.9, 1, 1.3, 1.2, 1.1, 1];
    private static readonly double[] RegionOrderFactor = [0.6, 0.65, 0.7, 0.75, 0.8, 0.85, 0.9, 0.95, 1, 1.05, 1.1, 1.15, 1.2, 1.25, 1.3, 1.35, 1.4, 1.4, 1.35, 1.3, 1.25, 1.2, 1.15, 1.1, 1.05, 1, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.65, 0.6];
    private static readonly double[] Discounts = [1, 0.9, 0.8, 0.7, 0.6, 0.5];
    private static readonly double[] DiscountMonth = [0.95, 0.9, 1, 0.98, 0.85, 1, 0.98, 0.88, 0.8, 0.86, 0.92, 0.98];
    private static readonly double[] CustomerDistribution = [0, 0.1, 0.5, 0.6, 0.6, 0.6, 0.7, 0.7, 0.7, 0.8, 0.9, 1];
    private static readonly HashSet<string> DiscountIndustries = ["保险", "互联网", "汽车", "制造业"];
    private static readonly HashSet<string> DiscountProfessions = ["HR", "财务", "销售", "运营"];
    private static readonly double[] YearTrend = [0.88, 0.94, 1, 1.07, 1.14];
    private static readonly double[] MonthNoise = [0.97, 1.03, 0.99, 1.05, 0.95, 1.02, 0.98, 1.06, 0.93, 1.01, 0.96, 1.04, 0.99, 1.02, 0.94, 1.05, 0.98, 1.03, 0.97, 1.01, 0.95, 1.06, 0.99, 1.02, 0.96, 1.04, 0.98, 1.03, 0.94, 1.05, 0.97, 1.01, 0.99, 1.04, 0.96, 1.02, 1];
    private static readonly double[] WeekdayFactor = [1.05, 0.9, 0.9, 0.9, 0.95, 1.1, 1.2];
    private static readonly double[] ProductPopularity = [0.7, 0.74, 0.78, 0.82, 0.86, 0.9, 0.94, 0.98, 1.02, 1.06, 1.1, 1.14, 1.18, 1.22, 1.26, 1.22, 1.18, 1.14, 1.1, 1.06, 1.02, 0.98, 0.94, 0.9, 0.86, 0.82, 0.78, 0.74, 0.7];
    private static readonly double[] TargetYearBias = [-0.05, 0.06, -0.02, 0.04, -0.04];

    /// <summary>创建独立任务; 配置和基础资源非法时在写入前失败.</summary>
    /// <param name="options">经统一范围校验的配置.</param>
    /// <param name="dataset">外部加载的基础地理及姓名数据.</param>
    /// <param name="progress">可空进度接收器, 阶段键沿用上游.</param>
    public SampleDataGenerator(SampleDataOptions options, SampleDataset dataset, IProgress<SampleDataProgress>? progress = null)
    {
        _options = options;
        _dataset = dataset;
        (_start, _end) = options.Validate();
        _windowDays = _end.DayNumber - _start.DayNumber;
        _random = options.Seed is { } seed ? new Random(seed) : new Random();
        _progress = progress;
        if (dataset.Provinces.Count == 0 || dataset.Cities.Count == 0 || dataset.FirstNames.Count == 0 || dataset.LastNames.Count == 0)
            throw new AppException("SAMPLE_RESOURCE_INVALID", "示例基础地理或姓名资源为空.", 3);
        _cityToProvince = dataset.Cities.ToDictionary(c => c.CityId, c => c.ProvinceId);
        _provinceById = dataset.Provinces.ToDictionary(p => p.ProvinceId);
        _provinceCities = dataset.Cities.GroupBy(c => c.ProvinceId).ToDictionary(g => g.Key, g => g.OrderBy(c => CityOrderFactor(c.CityId)).ThenBy(c => c.CityId).ToArray());
        _provinceCityFactor = _provinceCities.ToDictionary(p => p.Key, p => p.Value.Average(c => CityOrderFactor(c.CityId)));
    }

    /// <summary>全量依次生成 D00–D03 和 T00–T06; 错误与取消由调用方回滚 staging.</summary>
    /// <param name="storage">受限流式表存储.</param>
    /// <param name="cancellationToken">每个维度行, 门店营业日及订单检查取消.</param>
    /// <returns>所有业务表实际行数.</returns>
    public SampleGenerationResult GenerateAll(ISampleTableStorage storage, CancellationToken cancellationToken = default)
    {
        Begin(cancellationToken);
        Report(0, "stageStart");
        WriteDimensions(storage);
        Report(30, "stageDimensions");
        GenerateProducts();
        WriteRows(storage, SampleTables.FileProduct, _products.Select(p => new[] { I(p.Id), p.Code, p.Category, p.Name, F(p.SalePrice), F(p.CostPrice) }));
        Report(35, "stageProducts");
        GenerateStores();
        WriteRows(storage, SampleTables.FileStore, _stores.Select(s => new[] { I(s.Id), s.Code, s.Name, s.Manager, D(s.OpenDate), I(s.CityId), s.City, F(s.Lat), F(s.Lng), s.CloseDate is { } close ? D(close) : "" }));
        Report(45, "stageStores");
        GenerateCustomers();
        WriteRows(storage, SampleTables.FileCustomer, _customers.Select(c => new[] { I(c.Id), c.Code, c.Name, D(c.Birth), c.Gender, D(c.Registration), c.Industry, c.Profession }));
        Report(55, "stageCustomers");
        GenerateOrders(storage, false, 0);
        Report(90, "stageOrders");
        GenerateSaleTargets(storage);
        Report(100, "stageDone");
        return Result();
    }

    /// <summary>绑定一次任务与取消令牌, 防止对同一生成器重放.</summary>
    private void Begin(CancellationToken cancellationToken)
    {
        if (_used) throw new InvalidOperationException("每个示例生成器只能执行一次.");
        _used = true;
        _cancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>返回实际维度数量与本次事实新增数量.</summary>
    private SampleGenerationResult Result() => new(_products.Count, _stores.Count, _customers.Count, _nInventory, _nOrders, _nItems);

    /// <summary>上报固定阶段键与百分比.</summary>
    private void Report(double percent, string stage) => _progress?.Report(new(percent, stage));

    /// <summary>输出完整地理维度, 自动编号按资源行序从 1 开始.</summary>
    private void WriteDimensions(ISampleTableStorage storage)
    {
        WriteRows(storage, SampleTables.FileRegion, _dataset.Regions.Select((r, i) => new[] { I(i + 1), I(r.RegionId), r.Name, r.Manager, I(r.CityId), r.CityName, F(r.Lat), F(r.Lng) }));
        WriteRows(storage, SampleTables.FileProvince, _dataset.Provinces.Select((p, i) => new[] { I(i + 1), I(p.RegionId), I(p.ProvinceId), p.Full, p.Short1, p.Short2, F(p.Lat), F(p.Lng) }));
        WriteRows(storage, SampleTables.FileCity, _dataset.Cities.Select((c, i) => new[] { I(i + 1), I(c.ProvinceId), I(c.CityId), c.Name, F(c.Lat), F(c.Lng) }));
        WriteRows(storage, SampleTables.FileDistrict, _dataset.Districts.Select((d, i) => new[] { I(i + 1), I(d.CityId), I(d.DistrictId), d.Name, F(d.Lat), F(d.Lng) }));
    }

    /// <summary>创建含表头的表并流式写入, 任何写入错误直接传播.</summary>
    private void WriteRows(ISampleTableStorage storage, string file, IEnumerable<string[]> rows)
    {
        using var writer = storage.Create(file, SampleTables.Header(file));
        foreach (var row in rows)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            writer.Write(row);
        }
    }

    /// <summary>产品售价对数正态抽样并截断 [1000, 30000], 分类由价格分位映射 A–J.</summary>
    private void GenerateProducts()
    {
        HashSet<string> seen = [];
        for (var i = 1; i <= _options.ProductCount; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var z = Normal();
            var sale = Math.Clamp(Math.Exp(8.699514748210192 + 0.85 * z), 1000, 30000);
            var category = Letter(RoundInt(StandardNormalCdf(z) * 9)) + "类";
            var ratio = _random.NextDouble();
            var cost = sale * (ratio >= 0.28 ? ratio : 0.18);
            string name;
            do { name = "产品" + RandomProductCode(); } while (!seen.Add(name));
            _products.Add(new(i, "SKU_" + Pad(i, 6), category, name, Round(sale, 2), Round(cost, 2)));
        }
    }

    /// <summary>产品名后缀为 3 位字母数字混合, 至少各一个, 名称唯一性由调用方保证.</summary>
    private string RandomProductCode()
    {
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var code = "";
            var letter = false;
            var digit = false;
            for (var n = 0; n < 3; n++)
            {
                var v = RoundInt(_random.NextDouble() * 35);
                code += v < 26 ? Letter(v) : I(v - 26);
                letter |= v < 26;
                digit |= v >= 26;
            }
            if (letter && digit) return code;
        }
    }

    /// <summary>客户数量按未关店门店营业天数生成; 年龄, 性别, 行业与职业沿用上游分布.</summary>
    private void GenerateCustomers()
    {
        var halfWindow = _windowDays / 2.0;
        var id = 0;
        for (var i = 0; i < _stores.Count; i++)
        {
            var s = _stores[i];
            var count = s.CloseDate is null ? (int)((_end.DayNumber - s.OpenDate.DayNumber) * (1.2 + _random.NextDouble())) : 0;
            for (var k = 1; k <= count; k++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                id++;
                var sj = _random.NextDouble();
                var age = AgeDistribution[id % 12];
                var birth = _end.AddDays(-(7500 + RoundInt((age + _random.NextDouble()) * 7000)));
                var registration = _start.AddDays(RoundInt((age + _random.NextDouble()) * halfWindow));
                _customers.Add(new(id, "CC_" + Pad(id, 7), RandomName(0.8, sj), birth, sj >= 0.8 ? "女" : "男", registration,
                    Industries[RoundInt(_random.NextDouble() * IndustryDistribution[i % 7] * 6)], Professions[RoundInt(_random.NextDouble() * ProfessionDistribution[i % 7] * 6)]));
            }
        }
    }

    /// <summary>按相同随机分位选姓与名, 阈值控制二字或三字姓名.</summary>
    private string RandomName(double twoCharacterThreshold, double sj)
    {
        var first = _dataset.FirstNames;
        var last = _dataset.LastNames;
        var a = first[RoundInt((first.Count - 1) * sj)];
        var b = first[RoundInt((first.Count - 1) * (1 - sj))];
        return last[RoundInt((last.Count - 1) * sj)] + a + (sj < twoCharacterThreshold ? "" : b);
    }

    /// <summary>Box-Muller 标准正态抽样, 无需密码学随机源.</summary>
    private double Normal()
    {
        var u1 = _random.NextDouble();
        var u2 = _random.NextDouble();
        if (u1 <= 0) u1 = 1e-12;
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>标准正态累计概率, 用收敛到双精度精度的误差函数级数保持分类分位规则.</summary>
    internal static double StandardNormalCdf(double z)
    {
        // erf(x) = 2x exp(-x²)/√π × Σ (2x²)^n/(1×3×...×(2n+1)).
        // 这里的 x 来自 Box-Muller, 数值范围有限; 正项级数避免相消并按双精度收敛.
        var x = Math.Abs(z) / Math.Sqrt(2);
        double term = 1, sum = 1;
        for (var n = 1; n <= 512; n++)
        {
            term *= 2 * x * x / (2 * n + 1);
            var next = sum + term;
            if (next == sum) break;
            sum = next;
        }
        var erf = Math.Clamp(2 * x * Math.Exp(-x * x) / Math.Sqrt(Math.PI) * sum, 0, 1);
        return 0.5 * (1 + Math.CopySign(erf, z));
    }

    /// <summary>城市订单量系数向 1 压缩一半, 保持相对排序与均值.</summary>
    private static double CityOrderFactor(int cityId) => 1 + (RegionOrderFactor[cityId % RegionOrderFactor.Length] - 1) * 0.5;
    /// <summary>银行家舍入, 与上游 VBA 数值规则一致.</summary>
    internal static double Round(double value, int places) { var scale = Math.Pow(10, Math.Max(0, places)); return Math.Round(value * scale, MidpointRounding.ToEven) / scale; }
    /// <summary>零位银行家舍入并转换整数, 溢出时明确失败.</summary>
    internal static int RoundInt(double value) => checked((int)Math.Round(value, MidpointRounding.ToEven));
    /// <summary>非负期望的无偏随机向下舍入, 支持低于 0.5 的稀疏出单.</summary>
    private int StochasticFloor(double value) { if (value <= 0) return 0; var floor = Math.Floor(value); return checked((int)floor + (_random.NextDouble() < value - floor ? 1 : 0)); }
    /// <summary>文化无关整数格式.</summary>
    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    /// <summary>文化无关最短十进制格式, 禁用科学计数以兼容 CSV 模型分区.</summary>
    private static string F(double value) => value.ToString("0.###############################", CultureInfo.InvariantCulture);
    /// <summary>CSV 统一日期格式.</summary>
    private static string D(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    /// <summary>业务编号零填充, 超过宽度时保留全部数字.</summary>
    private static string Pad(int value, int width) => value.ToString("D" + I(width), CultureInfo.InvariantCulture);
    /// <summary>从 A 起偏移的大写字母, 调用方限定 0–25.</summary>
    private static string Letter(int value) => ((char)('A' + value)).ToString();
}
