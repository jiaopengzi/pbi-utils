/**
 * FilePath    : pbi-utils\web\tests\bridge\measureBatchBudget.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 核验度量值批次在字符上限内仍可能超过真实 UTF-8 保存信封, 以及正常边界.
 */
import { expect, it } from "vitest"
import { measureBatchAllowed, measureBatchRequest } from "@/bridge/measureBatchBudget"
import type { MeasureChange } from "@/bridge/client"

it("中文正文与说明分别可传输但合并保存超64KiB时拒绝, 同字符数 ASCII 说明可通过", () => {
    const expression = "\n    //" + "中".repeat(20000) + "\n    1 + 1"
    const item: MeasureChange = { action: "update", table: "销售", name: "销售额", expression, description: "说".repeat(2000) }
    expect(measureBatchRequest("baseline", [item]).value.length).toBeLessThan(30000)
    expect(measureBatchAllowed("baseline", [item])).toBe(false)
    expect(measureBatchAllowed("baseline", [{ ...item, description: "s".repeat(2000) }])).toBe(true)
})

it("空批次、超过128项及value超过30000字符拒绝, 普通保存保留准确基线和属性", () => {
    const item: MeasureChange = { action: "update", table: "销售", name: "销售额", description: "修改" }
    expect(measureBatchAllowed("baseline", [])).toBe(false)
    expect(
        measureBatchAllowed(
            "baseline",
            Array.from({ length: 129 }, () => item),
        ),
    ).toBe(false)
    expect(measureBatchAllowed("baseline", [{ ...item, description: "a".repeat(30000) }])).toBe(false)
    expect(measureBatchAllowed("baseline", [item])).toBe(true)
    expect(JSON.parse(measureBatchRequest("baseline", [item]).value)).toEqual({ baselineId: "baseline", items: [item] })
})
