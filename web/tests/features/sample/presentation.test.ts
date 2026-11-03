/**
 * FilePath    : pbi-utils\web\tests\features\sample\presentation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证示例日期和参数边界, 防止无效日历、夏令时和增量截止日漂移.
 */
import { expect, it } from "vitest"
import { dateMillis, defaultOptions, followingDate, optionsError } from "@/features/sample/presentation"

it("日历严格识别闰日, 截止日跨年且不受时区日差影响", () => {
    expect(dateMillis("2024-02-29")).toBe(Date.UTC(2024, 1, 29))
    expect(dateMillis("2025-02-29")).toBeNaN()
    expect(dateMillis("2026-04-31")).toBeNaN()
    expect(dateMillis("2026-2-01")).toBeNaN()
    expect(dateMillis("0000-01-01")).toBeNaN()
    expect(followingDate("2024-02-28")).toBe("2024-02-29")
    expect(followingDate("2026-12-31")).toBe("2027-01-01")
    expect(followingDate("9999-12-31")).toBe("")
    expect(followingDate("bad date")).toBe("")
})
it("保留上游默认数量与 60 天边界, 数值必须为范围内整数", () => {
    const defaults = defaultOptions(new Date(2026, 9, 14, 0, 15))
    expect(defaults).toMatchObject({ locale: "zh-cn", productCount: 666, storeCount: 55, inventoryCycle: 22, startDate: "2022-05-28", endDate: "2026-10-14" })
    expect(dateMillis(defaults.endDate) - dateMillis(defaults.startDate)).toBe(1600 * 86400000)
    const options = { ...defaults, startDate: "2026-01-01", endDate: "2026-03-02" }
    expect(optionsError(options)).toBe("")
    expect(optionsError({ ...options, endDate: "2026-03-01" })).toBe("dateError")
    expect(optionsError({ ...options, startDate: "2026-03-02" })).toBe("dateError")
    expect(optionsError({ ...options, productCount: 0 })).toBe("productsError")
    expect(optionsError({ ...options, storeCount: 1.5 })).toBe("storesError")
    expect(optionsError({ ...options, inventoryCycle: 181 })).toBe("inventoryError")
    expect(optionsError({ ...options, productCount: 10000, storeCount: 1, inventoryCycle: 5 })).toBe("")
    expect(optionsError({ ...options, startDate: "0060-01-01", endDate: "0060-03-02" })).toBe("dateYearError")
    expect(optionsError({ ...options, startDate: "9986-01-01", endDate: "9986-03-02" })).toBe("dateYearError")
})
