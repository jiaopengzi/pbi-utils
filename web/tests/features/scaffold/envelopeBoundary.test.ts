/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\envelopeBoundary.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 完整桥接信封 UTF-8 预算包含转义和元数据, 超限立即失败且保留草稿事实.
 */
import { afterEach, expect, it, vi } from "vitest"
import { BridgeClient, type Transport } from "@/bridge/client"
import { hostText, setLocale } from "@/i18n"
import { measureError, saveDraft } from "@/features/scaffold/draft"
import type { DraftIdentity } from "@/features/scaffold/contracts"
type Envelope = { protocolVersion: number; id: string; method: string; params: Record<string, unknown>; connectionRevision: number }
/** 返回真实客户端和即时确认传输, 仅记录已实际发送的消息. */
function harness(connectionRevision = 0) {
    let receive: (event: MessageEvent) => void = vi.fn()
    const messages: Envelope[] = []
    const transport: Transport = {
        addEventListener: (_type, listener) => {
            receive = listener
        },
        removeEventListener: vi.fn(),
        postMessage: (value: unknown) => {
            const envelope = value as Envelope
            messages.push(envelope)
            receive(
                new MessageEvent("message", {
                    data: {
                        protocolVersion: 1,
                        connectionRevision,
                        id: envelope.id,
                        result: envelope.method === "request.cancel" ? { accepted: true } : { draftId: "d".repeat(36), revision: messages.length },
                    },
                }),
            )
        },
    }
    return { bridge: new BridgeClient(transport), messages }
}
afterEach(() => {
    vi.useRealTimers()
    setLocale("zh-cn")
})

it("合法32KiB引号值在完整信封超限时即时失败, 保留输入和最后已确认草稿版本", async () => {
    vi.useFakeTimers()
    const { bridge, messages } = harness()
    const item = { name: "LongText", value: '"'.repeat(32768), dataCategory: "Uncategorized", enabled: true, description: "", displayFolder: "" }
    let saved: DraftIdentity | undefined
    try {
        expect(measureError([item])).toBeUndefined()
        await expect(
            saveDraft(bridge, undefined, {}, { measures: [item] }, (value) => {
                saved = value
            }),
        ).rejects.toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        expect(messages.map((message) => message.params.section)).toEqual(["base", "measures"])
        expect(saved).toEqual({ draftId: "d".repeat(36), revision: 2 })
        expect(item.value).toHaveLength(32768)
        expect(vi.getTimerCount()).toBe(0)
        await vi.advanceTimersByTimeAsync(60000)
        expect(messages.map((message) => message.method)).not.toContain("request.cancel")
        await expect(bridge.request("request.cancel", { requestId: "a-valid-request" }).promise).resolves.toEqual({ accepted: true })
    } finally {
        bridge.dispose()
    }
})
it.each([
    [65536, 0],
    [65537, 0],
    [65536, Number.MAX_SAFE_INTEGER],
    [65537, Number.MAX_SAFE_INTEGER],
])("完整信封精确%d字节边界, 连接代际%d计入预算", async (limit, connectionRevision) => {
    vi.useFakeTimers()
    const { bridge, messages } = harness(connectionRevision)
    // 连接代际只能由当前目标的权威回复建立, 无关 app.info 不能改变工作区绑定.
    await bridge.request("project.inspect", {}).promise
    messages.length = 0
    const params = { section: "base", value: { text: "" } }
    const overhead = new TextEncoder().encode(
        JSON.stringify({ protocolVersion: 1, id: "0".repeat(36), method: "scaffold.updateDraft", params, connectionRevision }),
    ).length
    params.value.text = "a".repeat(limit - overhead)
    try {
        const request = bridge.request("scaffold.updateDraft", params)
        if (limit === 65536) {
            await expect(request.promise).resolves.toMatchObject({ revision: 1 })
            expect(new TextEncoder().encode(JSON.stringify(messages[0])).length).toBe(65536)
        } else {
            await expect(request.promise).rejects.toMatchObject({ code: "MESSAGE_TOO_LARGE" })
            expect(messages).toHaveLength(0)
        }
        expect(vi.getTimerCount()).toBe(0)
    } finally {
        bridge.dispose()
    }
})
it.each(["x".repeat(32768), "中".repeat(10922)])("32KiB业务范围内普通ASCII和中文按UTF8实际预算正常发送", async (value) => {
    const { bridge, messages } = harness()
    try {
        const item = { name: "Text", value, dataCategory: "Uncategorized", enabled: true, description: "", displayFolder: "" }
        expect(measureError([item])).toBeUndefined()
        await expect(bridge.request("scaffold.updateDraft", { section: "measure", value: { index: 0, item } }).promise).resolves.toMatchObject({ revision: 1 })
        expect(messages).toHaveLength(1)
        expect(new TextEncoder().encode(JSON.stringify(messages[0])).length).toBeLessThanOrEqual(65536)
    } finally {
        bridge.dispose()
    }
})
it("本地UDF转义和多字段累加也受同一完整信封预算约束, 错误可双语显示", async () => {
    vi.useFakeTimers()
    const { bridge, messages } = harness()
    try {
        const definition = { schemaVersion: 1 as const, name: "Local.Test", version: "1.0.0", expression: '"'.repeat(32768), description: "", dependencies: [] }
        const escaped = await bridge.request("udf.localSave", { definition }).promise.catch((cause: unknown) => cause)
        expect(escaped).toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        const combined = {
            ...definition,
            expression: "a".repeat(32000),
            description: "中".repeat(2000),
            dependencies: Array.from({ length: 80 }, (_, index) => ({
                source: "local",
                packageId: `id-${index}`,
                version: "1.0.0",
                function: `Ns.${"a".repeat(197)}`,
                baseline: "b".repeat(64),
            })),
        }
        await expect(bridge.request("udf.localSave", { definition: combined }).promise).rejects.toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        expect(messages).toHaveLength(0)
        expect(vi.getTimerCount()).toBe(0)
        setLocale("en-us")
        expect(hostText((escaped as Error).message)).toContain("64 KiB")
        expect(hostText((escaped as Error).message)).not.toMatch(/[\u4e00-\u9fff]/)
    } finally {
        bridge.dispose()
    }
})
