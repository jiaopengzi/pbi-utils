// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleTables.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 完整保留上游 11 张 CSV 的固定文件名和字段顺序.

namespace PbiUtils.Application.SampleData;

/// <summary>与上游 Access 字段及 PBIP 分区一致的 CSV 结构.</summary>
public static class SampleTables
{
    public const string FileProduct = "T00_产品表.csv";
    public const string FileStore = "T01_门店表.csv";
    public const string FileCustomer = "T02_客户表.csv";
    public const string FileInventory = "T03_入库信息表.csv";
    public const string FileOrder = "T04_订单主表.csv";
    public const string FileOrderItem = "T05_订单子表.csv";
    public const string FileSaleTarget = "T06_销售目标表.csv";
    public const string FileRegion = "D00_大区表.csv";
    public const string FileProvince = "D01_省份表.csv";
    public const string FileCity = "D02_城市表.csv";
    public const string FileDistrict = "D03_区县表.csv";
    public const string AutoId = "F_00_自动编号";

    public static IReadOnlyList<string> HeaderProduct { get; } = Array.AsReadOnly<string>([AutoId, "F_01_产品编号", "F_02_产品分类", "F_03_产品名称", "F_04_产品销售价格", "F_05_产品成本价格"]);
    public static IReadOnlyList<string> HeaderStore { get; } = Array.AsReadOnly<string>([AutoId, "F_01_门店编号", "F_02_门店名称", "F_03_门店负责人", "F_04_开店日期", "F_05_城市ID", "F_06_城市", "F_07_纬度", "F_08_经度", "F_09_关店日期"]);
    public static IReadOnlyList<string> HeaderCustomer { get; } = Array.AsReadOnly<string>([AutoId, "F_01_客户编号", "F_02_客户名称", "F_03_客户生日", "F_04_客户性别", "F_05_注册日期", "F_06_客户行业", "F_07_客户职业"]);
    public static IReadOnlyList<string> HeaderInventory { get; } = Array.AsReadOnly<string>([AutoId, "F_01_入库产品编号", "F_02_入库产品数量", "F_03_入库门店编号", "F_04_入库日期"]);
    public static IReadOnlyList<string> HeaderOrder { get; } = Array.AsReadOnly<string>([AutoId, "F_01_订单编号", "F_02_门店编号", "F_03_下单日期", "F_04_送货日期", "F_05_客户编号", "F_06_销售渠道"]);
    public static IReadOnlyList<string> HeaderOrderItem { get; } = Array.AsReadOnly<string>([AutoId, "F_01_订单编号", "F_02_产品编号", "F_03_产品销售价格", "F_04_折扣比例", "F_05_产品销售数量", "F_06_产品销售金额"]);
    public static IReadOnlyList<string> HeaderSaleTarget { get; } = Array.AsReadOnly<string>([AutoId, "F_01_省ID", "F_02_省简称", "F_03_月份", "F_04_销售目标"]);
    public static IReadOnlyList<string> HeaderRegion { get; } = Array.AsReadOnly<string>([AutoId, "F_01_大区ID", "F_02_大区", "F_03_大区负责人", "F_04_办公地城市ID", "F_05_办公地城市", "F_06_纬度", "F_07_经度"]);
    public static IReadOnlyList<string> HeaderProvince { get; } = Array.AsReadOnly<string>([AutoId, "F_01_大区ID", "F_02_省ID", "F_03_省全称", "F_04_省简称1", "F_05_省简称2", "F_06_纬度", "F_07_经度"]);
    public static IReadOnlyList<string> HeaderCity { get; } = Array.AsReadOnly<string>([AutoId, "F_01_省ID", "F_02_城市ID", "F_03_城市", "F_04_纬度", "F_05_经度"]);
    public static IReadOnlyList<string> HeaderDistrict { get; } = Array.AsReadOnly<string>([AutoId, "F_01_城市ID", "F_02_区县ID", "F_03_区县", "F_04_纬度", "F_05_经度"]);

    public static IReadOnlyList<string> AllFiles { get; } = Array.AsReadOnly<string>([FileProduct, FileStore, FileCustomer, FileInventory, FileOrder, FileOrderItem, FileSaleTarget, FileRegion, FileProvince, FileCity, FileDistrict]);

    /// <summary>获取固定表的完整表头, 未知文件名拒绝.</summary>
    /// <param name="fileName">上游固定文件名.</param>
    /// <returns>字段顺序不可变的表头.</returns>
    public static IReadOnlyList<string> Header(string fileName) => fileName switch
    {
        FileProduct => HeaderProduct,
        FileStore => HeaderStore,
        FileCustomer => HeaderCustomer,
        FileInventory => HeaderInventory,
        FileOrder => HeaderOrder,
        FileOrderItem => HeaderOrderItem,
        FileSaleTarget => HeaderSaleTarget,
        FileRegion => HeaderRegion,
        FileProvince => HeaderProvince,
        FileCity => HeaderCity,
        FileDistrict => HeaderDistrict,
        _ => throw new ArgumentException("未知示例数据表.", nameof(fileName)),
    };
}
