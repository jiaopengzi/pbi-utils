/**
 * FilePath    : pbi-utils\web\tests\bridge\calendarBudget.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 日历大包可发送, 旁支内容仍受原信封上限保护.
 */
import { expect, it, vi } from "vitest"
import { BridgeClient, type Transport } from "@/bridge/client"
import { calendarEnvelopeAllowed } from "@/bridge/calendarBudget"

it("仅精确日期预览方法允许有界calendar分段大包, 目标和语言仍计入64KiB", () => {
    const calendar = { kind: "simple", startYear: 2024, endYear: 2024, tableName: "Dates", overrides: [{ name: "x".repeat(70000) }] }
    const params = { context: { kind: "model", targetId: "target", baselineId: "baseline" }, calendar, locale: "zh-cn" }
    const size = new TextEncoder().encode(JSON.stringify({ method: "calendar.preview", params })).length
    expect(calendarEnvelopeAllowed("calendar.preview", params, size)).toBe(true)
    expect(calendarEnvelopeAllowed("calendar.previewOther", params, size)).toBe(false)
    expect(calendarEnvelopeAllowed("calendar.preview", { ...params, locale: "x".repeat(70000) }, size + 70000)).toBe(false)
    expect(calendarEnvelopeAllowed("calendar.preview", params, 4 * 1024 * 1024 + 1)).toBe(false)
})

/** 即时响应实际客户端的传输, 返回已经发送的消息. */
function harness() {
    let receive: (event: MessageEvent) => void = vi.fn()
    const sent: unknown[] = []
    const transport: Transport = {
        addEventListener: (_type, listener) => {
            receive = listener
        },
        removeEventListener: vi.fn(),
        postMessage: (value) => {
            sent.push(value)
            receive(new MessageEvent("message", { data: { protocolVersion: 1, id: (value as { id: string }).id, result: {} } }))
        },
    }
    return { bridge: new BridgeClient(transport), sent }
}

it("模型日期的大覆盖集合使用受限通道而不是64KiB通用额度", async () => {
    const { bridge, sent } = harness()
    try {
        const overrides = Array.from({ length: 1500 }, (_, index) => ({
            date: new Date(Date.UTC(2020, 0, index + 1)).toISOString().slice(0, 10),
            isWorkday: true,
            name: "业务覆盖",
        }))
        await expect(
            bridge.request("configuration.apply", {
                targetId: "target",
                baselineId: "baseline",
                section: "calendar",
                request: { calendar: { kind: "full", startYear: 2020, endYear: 2025, tableName: "Dates", overrides } },
            }).promise,
        ).resolves.toEqual({})
        expect(new TextEncoder().encode(JSON.stringify(sent[0])).length).toBeGreaterThan(65536)
    } finally {
        bridge.dispose()
    }
})

it("真实桥接客户端允许只读日期预览的大覆盖输入", async () => {
    const { bridge, sent } = harness()
    try {
        await expect(
            bridge.request("calendar.preview", {
                context: { kind: "template", templateId: "blank", templateBaseline: "a".repeat(64) },
                calendar: {
                    kind: "simple",
                    startYear: 2024,
                    endYear: 2024,
                    tableName: "Dates",
                    overrides: Array.from({ length: 1500 }, () => ({ date: "2024-01-01", isWorkday: true, name: "覆盖" })),
                },
                locale: "zh-cn",
            }).promise,
        ).resolves.toEqual({})
        expect(new TextEncoder().encode(JSON.stringify(sent[0])).length).toBeGreaterThan(65536)
    } finally {
        bridge.dispose()
    }
})

it("日历大包不能夹带超额的其他功能段", async () => {
    const { bridge, sent } = harness()
    try {
        await expect(
            bridge.request("configuration.apply", {
                targetId: "target",
                baselineId: "baseline",
                section: "calendar",
                request: {
                    calendar: { kind: "full", startYear: 2024, endYear: 2024, tableName: "Dates", overrides: [] },
                    periods: {
                        dateTable: "Dates",
                        dateColumn: "Dates",
                        measureTable: "Measures",
                        baseMeasures: ["x".repeat(70000)],
                        templates: ["YTD"],
                        folder: "",
                        numberDigits: 2,
                        percentDigits: 2,
                    },
                },
            }).promise,
        ).rejects.toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        expect(sent).toHaveLength(0)
    } finally {
        bridge.dispose()
    }
})
