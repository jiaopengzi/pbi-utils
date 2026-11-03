/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\warnings.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证真实写入器固定警告的双语边界, 保留动态表名和未知诊断原文.
 */
import { afterEach, expect, it } from "vitest"
import { setLocale } from "@/i18n"
import { scaffoldWarning } from "@/features/scaffold/warnings"
afterEach(() => setLocale("zh-cn"))
it("用户模板外部源固定警告展示双语说明并保留未知诊断", () => {
    expect(scaffoldWarning("TEMPLATE_EXTERNAL_SOURCES_UNVERIFIED")).toContain("未执行或核验")
    setLocale("en-us")
    expect(scaffoldWarning("TEMPLATE_EXTERNAL_SOURCES_UNVERIFIED")).toContain("without execution or verification")
    expect(scaffoldWarning("UNKNOWN_TEMPLATE_SOURCE_DIAGNOSTIC")).toBe("UNKNOWN_TEMPLATE_SOURCE_DIAGNOSTIC")
})
it("模板目录重名诊断在两种语言中说明应重命名用户目录", () => {
    expect(scaffoldWarning("TEMPLATE_NAME_CONFLICT")).toContain("重命名")
    setLocale("en-us")
    expect(scaffoldWarning("TEMPLATE_NAME_CONFLICT")).toMatch(/rename/i)
})
it("英文翻译源转换, 元数据证据, 关系和已发布后处理警告", () => {
    setLocale("en-us")
    expect(scaffoldWarning("模型定义回读不表示 M/DAX 已执行")).toContain("does not mean M/DAX has executed")
    expect(scaffoldWarning("移动工程后需更新 PbiUtilsProjectRoot 参数为新的项目绝对目录")).toContain("PbiUtilsProjectRoot")
    expect(scaffoldWarning("零售样例新增独立 PbiUtilsRegionId/PbiUtilsStoreId 并精确重绑5条业务关系; 原组织ID与组织关系保持, 未虚构组织记录")).toContain(
        "exactly five business relationships",
    )
    expect(scaffoldWarning("零售产品成本/销售价格及订单单价三列按模拟精度提升为小数, 保留原对象名称和关系")).toContain("decimal types")
    expect(scaffoldWarning("项目已创建, 定义回读未完成, 请核对后继续; 不会重新发布")).toContain("will not be published again")
    expect(scaffoldWarning("项目已创建, Git 初始化状态需核验, 不会自动重试")).toContain("will not be retried automatically")
    expect(scaffoldWarning("项目已创建, Git 未初始化; 请安装或检查 Git 后在已有工程的 pbip 目录中手动初始化, 不要重复创建工程")).toContain("pbip folder")
    expect(scaffoldWarning("项目已创建, 发布回执未能保存, 下次将先核验现有产物")).toContain("Existing output will be verified first")
})
it("动态表名原样保留, 未知诊断不套用固定警告翻译", () => {
    const name = "客户:Sales"
    setLocale("en-us")
    expect(scaffoldWarning(`${name}: 示例没有组织或员工身份, 对应关联字段保持空值`)).toBe(
        `${name}: The sample has no organization or employee identities; related fields remain null.`,
    )
    expect(scaffoldWarning("未知诊断: source-specific detail")).toBe("未知诊断: source-specific detail")
    setLocale("zh-cn")
    expect(scaffoldWarning("模型定义回读不表示 M/DAX 已执行")).toBe("模型定义回读不表示 M/DAX 已执行")
})

it("模拟覆盖兼容字段和缺失日期关系的摘要支持双语并保留表名", () => {
    const compatible = "客户:Sales: 同名表已接入本轮模拟数据, 保留的模板额外数据列没有样例映射, 值为空"
    const relations = "模拟数据已复用 17 条关系; 缺少已选日期字段或日期表的 4 条关系未生成, 可配置日期字段后再建立"
    expect(scaffoldWarning(compatible)).toBe(compatible)
    setLocale("en-us")
    expect(scaffoldWarning(compatible)).toBe(
        "客户:Sales: The existing table now uses this sample data. Retained template columns without a sample mapping contain null values.",
    )
    expect(scaffoldWarning(relations)).toBe(
        "Reused 17 sample relationships. Skipped 4 relationships with missing calendar endpoints; configure the date fields before creating them.",
    )
    expect(scaffoldWarning("第三方: 模拟数据已复用 unknown 条关系")).toBe("第三方: 模拟数据已复用 unknown 条关系")
})
