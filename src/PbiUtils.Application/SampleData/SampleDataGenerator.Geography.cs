// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleDataGenerator.Geography.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 完整移植省份五梯队, 门店配额, 分层城市和营业日期规则.

namespace PbiUtils.Application.SampleData;

public sealed partial class SampleDataGenerator
{
    // 门店配额温和分层, 单店量级承担主要梯队差距; 两者相乘约为 50/22/16/9/3 的份额.
    private static readonly string[][] ProvinceTiers =
    [
        ["广东", "北京", "上海", "重庆", "天津", "四川", "浙江", "江苏"],
        ["福建", "山东", "安徽", "台湾", "河北"],
        ["贵州", "陕西", "山西", "河南", "云南", "湖北", "湖南"],
        ["新疆", "海南", "黑龙江", "辽宁", "内蒙古", "广西", "江西", "香港"],
        ["吉林", "甘肃", "西藏", "宁夏", "青海", "澳门"],
    ];
    private static readonly double[] ProvinceQuotaBase = TierBase([1.18, 1.15, 1.12, 1.10], true);
    private static readonly double[] ProvinceVolumeBase = TierBase([1.2, 1.67, 1.81, 2.05], false);
    private const double ProvinceTierSpread = 0.08;
    private static readonly (string Code, string Manager, int CityId, string City, double Lat, double Lng)[] FixedStores =
    [
        ("SC_0001", "焦阿大", 110000, "北京", 39.904989, 116.405285),
        ("SC_0002", "焦阿二", 120000, "天津", 39.125596, 117.190182),
        ("SC_0003", "焦阿三", 310000, "上海", 31.231706, 121.472644),
        ("SC_0004", "焦阿四", 500000, "重庆", 29.533155, 106.504962),
        ("SC_0005", "焦阿五", 710000, "台湾", 25.044332, 121.509062),
        ("SC_0006", "焦阿六", 810000, "香港", 22.320048, 114.173355),
        ("SC_0007", "焦阿七", 820000, "澳门", 22.198951, 113.54909),
    ];

    /// <summary>本次省份双权重模型, 同一次任务所有门店共享且只初始化一次.</summary>
    internal sealed record ProvinceWeightModel(IReadOnlyDictionary<int, double> Quota, IReadOnlyDictionary<int, double> Volume,
        IReadOnlyDictionary<int, double> CityFactor, double VolumeScale, IReadOnlyList<int[]> Blocks);

    /// <summary>返回实际复用的省份权重模型; 初始化仅消费一次随机序列.</summary>
    internal ProvinceWeightModel ProvinceWeights
    {
        get { EnsureProvinceWeights(); return _provinceWeightModel!; }
    }

    /// <summary>由相邻比值自末梯队向前计算基准, 可按省数加权归一为 1.</summary>
    private static double[] TierBase(double[] ratios, bool normalize)
    {
        var values = new double[5];
        values[^1] = 1;
        for (var t = values.Length - 2; t >= 0; t--) values[t] = values[t + 1] * ratios[t];
        if (normalize)
        {
            var weighted = Enumerable.Range(0, 5).Sum(t => ProvinceTiers[t].Length * values[t]);
            var members = ProvinceTiers.Sum(t => t.Length);
            for (var t = 0; t < values.Length; t++) values[t] *= members / weighted;
        }
        return values;
    }

    /// <summary>懒初始化本次五梯队权重, 梯队内洗牌, 交界同时上探/滑落后统一量级.</summary>
    private void EnsureProvinceWeights()
    {
        if (_provinceQuotaWeight is not null) return;
        var nameTier = ProvinceTiers.SelectMany((tier, t) => tier.Select(name => (name, t))).ToDictionary(v => v.name, v => v.t);
        var blocks = Enumerable.Range(0, 5).Select(_ => new List<int>()).ToArray();
        foreach (var p in _dataset.Provinces)
            if (nameTier.TryGetValue(p.Short2, out var tier)) blocks[tier].Add(p.ProvinceId);
        foreach (var block in blocks)
            for (var i = block.Count - 1; i > 0; i--)
            {
                var j = (int)((i + 1) * _random.NextDouble());
                (block[i], block[j]) = (block[j], block[i]);
            }
        Dictionary<int, double> quota = [];
        Dictionary<int, double> volume = [];
        for (var t = 0; t < blocks.Length; t++)
            for (var j = 0; j < blocks[t].Count; j++)
            {
                var factor = blocks[t].Count <= 1 ? 1 : 1 + ProvinceTierSpread * (0.5 - j / (double)(blocks[t].Count - 1));
                quota[blocks[t][j]] = ProvinceQuotaBase[t] * factor;
                volume[blocks[t][j]] = ProvinceVolumeBase[t] * factor;
            }
        _provinceBlocks = [.. blocks.Select(b => b.ToArray())];
        for (var t = 0; t + 1 < blocks.Length; t++)
        {
            var upper = blocks[t];
            var lower = blocks[t + 1];
            if (upper.Count == 0 || lower.Count < 2) continue;
            var promoted = lower[0];
            var demoted = upper[^1];
            var up = 1 - ProvinceTierSpread * 0.5 * _random.NextDouble();
            var down = 1 + ProvinceTierSpread * (1 + 0.5 * _random.NextDouble());
            quota[promoted] = ProvinceQuotaBase[t] * up;
            volume[promoted] = ProvinceVolumeBase[t] * up;
            quota[demoted] = ProvinceQuotaBase[t + 1] * down;
            volume[demoted] = ProvinceVolumeBase[t + 1] * down;
            _provinceBlocks[t][^1] = promoted;
            _provinceBlocks[t + 1][0] = demoted;
        }
        var sumQuota = quota.Values.Sum();
        var sumEffective = quota.Sum(p => p.Value / _provinceCityFactor[p.Key]);
        var sumEffectiveVolume = quota.Sum(p => p.Value / _provinceCityFactor[p.Key] * volume[p.Key]);
        if (sumQuota == 0 || sumEffectiveVolume == 0) throw new InvalidOperationException("省份资源未覆盖销售梯队.");
        var scale = quota.Count / sumQuota;
        foreach (var pid in quota.Keys) quota[pid] *= scale;
        _provinceQuotaWeight = quota;
        _provinceVolumeWeight = volume;
        _provinceVolumeScale = sumEffective / sumEffectiveVolume;
        _provinceWeightModel = new(quota, volume, _provinceCityFactor, _provinceVolumeScale, _provinceBlocks);
    }

    /// <summary>生成唯一 XYZ店 门店名, 字母抽样保留上游零位银行家舍入.</summary>
    private string[] RandomStoreNames(int count)
    {
        HashSet<string> seen = [];
        List<string> names = [];
        while (names.Count < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var name = Letter(RoundInt(_random.NextDouble() * 25)) + Letter(RoundInt(_random.NextDouble() * 25)) + Letter(RoundInt(_random.NextDouble() * 25)) + "店";
            if (seen.Add(name)) names.Add(name);
        }
        return [.. names];
    }

    /// <summary>先保留七个直辖市和港澳台门店, 其余按省配额及省内分位选城市.</summary>
    private void GenerateStores()
    {
        var names = RandomStoreNames(_options.StoreCount);
        for (var k = 0; k < Math.Min(_options.StoreCount, 7); k++)
        {
            var (Code, Manager, CityId, City, Lat, Lng) = FixedStores[k];
            _stores.Add(new() { Id = k + 1, Code = Code, Name = names[k], Manager = Manager, CityId = CityId, City = City, Lat = Lat, Lng = Lng });
        }
        if (_options.StoreCount >= 8)
        {
            var (sequence, ideal) = ProvinceStoreSequence(_options.StoreCount - 7);
            Dictionary<int, int> assigned = [];
            for (var i = 8; i <= _options.StoreCount; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var sj = _random.NextDouble();
                var pid = sequence[i - 8];
                var cities = _provinceCities[pid];
                var count = assigned.GetValueOrDefault(pid);
                var q = (count + 0.5) / Math.Max(ideal[pid], 1);
                var c = cities[RoundInt(Math.Min(q, 0.999) * (cities.Length - 1))];
                assigned[pid] = count + 1;
                _stores.Add(new()
                {
                    Id = i,
                    Code = "SC_" + Pad(i, 4),
                    Name = names[i - 1],
                    Manager = RandomName(0.66, sj),
                    CityId = c.CityId,
                    City = c.Name,
                    Lat = Round(c.Lat + _random.NextDouble() * 0.05, 6),
                    Lng = Round(c.Lng + _random.NextDouble() * 0.05, 6)
                });
            }
        }
        AssignStratifiedDates();
        AssignStoreTraffic();
        AssignProvinceStoreComp();
        AssignDiscountClasses();
    }

    /// <summary>随机舍入含固定店的总配额并交错轮转, 确保随机店总数准确.</summary>
    private (int[] Sequence, Dictionary<int, double> Ideal) ProvinceStoreSequence(int count)
    {
        EnsureProvinceWeights();
        var pids = _provinceQuotaWeight!.Keys.Order().ToArray();
        var weights = pids.Select(pid => _provinceQuotaWeight[pid] / _provinceCityFactor[pid]).ToArray();
        var sum = weights.Sum();
        var fixedCounts = new int[pids.Length];
        foreach (var (Code, Manager, CityId, City, Lat, Lng) in FixedStores)
            if (_cityToProvince.TryGetValue(CityId, out var pid))
            {
                var index = Array.IndexOf(pids, pid);
                if (index >= 0) fixedCounts[index]++;
            }
        var ideal = pids.Select((_, i) => Math.Max((count + 7) * weights[i] / sum - fixedCounts[i], 0.1)).ToArray();
        var targets = ideal.Select(StochasticFloor).ToArray();
        var total = targets.Sum();
        while (total != count)
        {
            var best = 0;
            var bestGap = double.PositiveInfinity;
            for (var i = 0; i < targets.Length; i++)
            {
                var gap = Math.Abs(ideal[i] - targets[i]);
                if (((total > count && targets[i] > 0) || total < count) && gap < bestGap) { best = i; bestGap = gap; }
            }
            var delta = total > count ? -1 : 1;
            targets[best] += delta;
            total += delta;
        }
        List<(double Slot, int Index)> entries = [];
        for (var i = 0; i < targets.Length; i++)
            for (var k = 0; k < targets[i]; k++)
                entries.Add(((k + 0.5) / targets[i] + _random.NextDouble() * 0.5 / targets[i], i));
        var sequence = entries.OrderBy(e => e.Slot).ThenBy(e => e.Index).Select(e => pids[e.Index]).ToArray();
        if (sequence.Length > 1)
        {
            var offset = RoundInt(_random.NextDouble() * sequence.Length);
            sequence = [.. sequence.Skip(offset), .. sequence.Take(offset)];
        }
        return (sequence, pids.Select((pid, i) => (pid, ideal: ideal[i])).ToDictionary(p => p.pid, p => p.ideal));
    }

    /// <summary>按省分组; 省内按既有文件行序, 与上游增量重建一致.</summary>
    private Dictionary<int, int[]> GroupStoresByProvince() => Enumerable.Range(0, _stores.Count)
        .GroupBy(i => _cityToProvince.GetValueOrDefault(_stores[i].CityId, -1)).ToDictionary(g => g.Key, g => g.ToArray());

    /// <summary>按省分层分配开店日; 单店中段, 两店抗互补, 多店均匀分层.</summary>
    private void AssignStratifiedDates()
    {
        var span = Math.Max(_windowDays - 28, 1);
        foreach (var (_, list) in GroupStoresByProvince().OrderBy(g => g.Key))
        {
            var pair = _random.NextDouble();
            for (var j = 0; j < list.Length; j++)
            {
                var q = list.Length switch { 1 => 0.4 + 0.2 * _random.NextDouble(), 2 => j == 0 ? pair / 2 : 1 - pair / 2, _ => (j + _random.NextDouble()) / list.Length };
                var open = _end.AddDays(-(RoundInt(q * span) + 28));
                var store = _stores[list[j]];
                store.OpenDate = open;
                var closeCandidate = open.AddDays(550 + RoundInt(4320 * _random.NextDouble()));
                if (closeCandidate <= _end) store.CloseDate = closeCandidate;
            }
        }
    }

    /// <summary>同省客流在 [0.75, 1.25] 铺开, 均值恒为 1, 单店恒为 1.</summary>
    private void AssignStoreTraffic()
    {
        foreach (var list in GroupStoresByProvince().Values)
            for (var j = 0; j < list.Length; j++) _stores[list[j]].Traffic = list.Length == 1 ? 1 : 0.75 + 0.5 * j / (list.Length - 1);
    }

    /// <summary>用总配额份额/实际门店数反向吸收门店取整的量级偏差.</summary>
    private void AssignProvinceStoreComp()
    {
        EnsureProvinceWeights();
        var sum = _provinceQuotaWeight!.Sum(p => p.Value / _provinceCityFactor[p.Key]);
        foreach (var (pid, list) in GroupStoresByProvince())
            _provinceStoreComp[pid] = _stores.Count * (_provinceQuotaWeight!.GetValueOrDefault(pid) / _provinceCityFactor.GetValueOrDefault(pid, 1)) / sum / list.Length;
    }

    /// <summary>按同省营业天数 x 城市系数 x 客流排名均衡折扣; 少于三店全部中档.</summary>
    private void AssignDiscountClasses()
    {
        foreach (var group in GroupStoresByProvince().Values)
        {
            var list = group.OrderByDescending(i => StoreExpectedVolume(_stores[i])).ThenBy(i => _stores[i].Id).ToArray();
            for (var rank = 0; rank < list.Length; rank++)
                _stores[list[rank]].DiscountClass = list.Length < 3 ? 10 + rank * 7 % 21 : BalancedStoreClass(rank);
        }
    }

    /// <summary>估算可从 T01 重建的门店相对规模, 不包含随机省权重.</summary>
    private double StoreExpectedVolume(Store store)
    {
        var end = store.CloseDate is { } close && close < _end ? close : _end;
        return Math.Max(0, end.DayNumber - store.OpenDate.DayNumber) * CityOrderFactor(store.CityId) * store.Traffic;
    }

    /// <summary>四家一组分配一深折, 两中档, 一溢价的确定性类别.</summary>
    private static int BalancedStoreClass(int rank) => (rank % 4) switch { 0 => rank / 4 % 10, 3 => 31 + rank / 4 % 9, _ => 10 + (rank / 4 * 2 + rank % 2) % 21 };

    /// <summary>订单量省权重 x 量级归一 x 门店数取整补偿, 未知省份为 1.</summary>
    private double ProvinceBaseFactor(Store store)
    {
        var model = ProvinceWeights;
        if (!_cityToProvince.TryGetValue(store.CityId, out var pid) || !model.Volume.TryGetValue(pid, out var volume)) return 1;
        return volume * model.VolumeScale * _provinceStoreComp.GetValueOrDefault(pid, 1);
    }
}
