/**
 * FilePath    : pbi-utils\web\tests\features\model\measureFieldOptions.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证官方预定义格式覆盖、双语标签及文件夹建议, 不改变模型格式标识或目录原文.
 */
import { expect, it } from "vitest"
import { displayFolderOptions, measureFormatPresets } from "@/features/model/measureFieldOptions"
import { measureFieldMessages } from "@/i18n/measureFieldMessages"

it("覆盖官方命名数字与日期时间格式, 两种语言均有名称且实际格式标识唯一", () => {
    const predefined = [
        "General Number",
        "Currency",
        "Fixed",
        "Standard",
        "Percent",
        "Scientific",
        "Yes/No",
        "True/False",
        "On/Off",
        "General Date",
        "Long Date",
        "Medium Date",
        "Short Date",
        "Long Time",
        "Medium Time",
        "Short Time",
    ]
    for (const value of predefined) {
        const preset = measureFormatPresets.find((item) => item.value === value)
        expect(preset, value).toBeDefined()
        for (const locale of ["zh-cn", "en-us"] as const) {
            const messages: Record<string, string> = measureFieldMessages[locale]
            expect(messages[preset!.labelKey], `${locale}: ${value}`).toBeTruthy()
        }
    }
    expect(new Set(measureFormatPresets.map((item) => item.value)).size).toBe(measureFormatPresets.length)
    expect(measureFormatPresets.some((item) => item.value === "")).toBe(true)
    for (const value of ["#,##0", "0.00", "yyyy-mm-dd", "hh:nn:ss"]) expect(measureFormatPresets.some((item) => item.value === value)).toBe(true)
})

it("多位置和层级路径提供全部实际目录, 重复及大小写变体沿用首个原文", () => {
    const values = ["财务\\收入;经营\\收入", "财务\\收入", "Finance\\Sales", "finance\\sales;Finance\\Costs", ""] as const
    const options = displayFolderOptions(values)
    expect(new Set(options)).toEqual(new Set(["财务", "财务\\收入", "经营", "经营\\收入", "Finance", "Finance\\Sales", "Finance\\Costs"]))
    expect(options.filter((value) => value.toLowerCase() === "finance\\sales")).toHaveLength(1)
    expect(values[0]).toBe("财务\\收入;经营\\收入")
    expect(values[3]).toBe("finance\\sales;Finance\\Costs")
})

it("忽略空层级和空位置但保留实际目录内的空格, 同名叶子仍按完整路径区分", () => {
    expect(new Set(displayFolderOptions([";\\; ", "财务\\\\ 收入 \\明细;经营\\明细"]))).toEqual(
        new Set(["财务", "财务\\ 收入 ", "财务\\ 收入 \\明细", "经营", "经营\\明细"]),
    )
    expect(displayFolderOptions([])).toEqual([])
})
