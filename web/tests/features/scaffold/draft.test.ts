/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\draft.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证宿主草稿串行版本, 跳过和分项更新边界.
 */
import { expect, it, vi } from "vitest"
import { saveDraft, measureError } from "@/features/scaffold/draft"

it("先保存 base 再保存各可选项, 跳过传 null 且每次消费前一版本", async () => {
    const calls: unknown[] = []
    const request = vi.fn((_method, params) => {
        calls.push(params)
        return { promise: Promise.resolve({ draftId: "bound", revision: calls.length }) }
    })
    const saved = await saveDraft(
        { request } as never,
        undefined,
        { templateId: "blank" },
        { measures: null, sample: null, calendar: null, periods: null, udfs: null },
    )
    expect(calls).toEqual([
        { section: "base", value: { templateId: "blank" } },
        ...["measures", "sample", "calendar", "periods", "udfs"].map((section, index) => ({ draftId: "bound", revision: index + 1, section, value: null })),
    ])
    expect(saved).toEqual({ draftId: "bound", revision: 6 })
})
it("大度量集合逐条发送, 不突破桥接消息预算", async () => {
    const calls: { section: string; value: unknown }[] = []
    const request = vi.fn((_method, params) => {
        calls.push(params)
        return { promise: Promise.resolve({ draftId: "bound", revision: calls.length }) }
    })
    const items = Array.from({ length: 3 }, (_, index) => ({
        name: String(index),
        value: "x".repeat(20000),
        dataCategory: "Uncategorized",
        enabled: true,
        description: "",
        displayFolder: "",
    }))
    await saveDraft({ request } as never, undefined, {}, { measures: items })
    expect(calls.map((call) => call.section)).toEqual(["base", "measures", "measure", "measure", "measure"])
    expect(calls.every((call) => new TextEncoder().encode(JSON.stringify(call)).length < 49152)).toBe(true)
})
it("批量日期段使用受限大消息一次更新, 不拆成逐日部分草稿", async () => {
    const calls: { section: string; value: unknown }[] = []
    const request = vi.fn((_method, params) => {
        calls.push(params)
        return { promise: Promise.resolve({ draftId: "bound", revision: calls.length }) }
    })
    const calendar = {
        kind: "simple",
        startYear: 2020,
        endYear: 2025,
        tableName: "Calendar",
        fields: ["Dates", "Status"],
        overrides: Array.from({ length: 1500 }, (_, index) => ({ date: String(index), isWorkday: true, name: "公司工作日" })),
    }
    await saveDraft({ request } as never, undefined, {}, { calendar })
    expect(calls.map((call) => call.section)).toEqual(["base", "calendar"])
    expect(calls[1]?.value).toBe(calendar)
})
it("重复名称和非法分类在创建前定位", () => {
    const item = { name: "Revenue", value: "1", dataCategory: "Uncategorized", enabled: true, description: "", displayFolder: "" }
    expect(measureError([item, { ...item }])).toEqual({ key: "duplicate", row: 2 })
    expect(measureError([{ ...item, dataCategory: "guess" }])).toEqual({ key: "category", row: 1 })
})
it("中途失败也保留最后宿主版本, 不继续写后续项", async () => {
    let revision = 0
    const saved: unknown[] = []
    const request = vi.fn((_method, _params) => ({
        promise: ++revision === 3 ? Promise.reject(new Error("cancelled")) : Promise.resolve({ draftId: "bound", revision }),
    }))
    await expect(
        saveDraft({ request } as never, undefined, {}, { measures: null, sample: null, calendar: null }, (value) => saved.push(value)),
    ).rejects.toThrow("cancelled")
    expect(saved).toEqual([
        { draftId: "bound", revision: 1 },
        { draftId: "bound", revision: 2 },
    ])
    expect(request).toHaveBeenCalledTimes(3)
})
