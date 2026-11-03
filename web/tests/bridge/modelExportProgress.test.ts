/**
 * FilePath    : pbi-utils\web\tests\bridge\modelExportProgress.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 核验导出进度仅投递同一在途请求的合法单调计数, 不扩大超时或重放终态.
 */
import { expect, it, vi } from "vitest"
import { BridgeClient } from "@/bridge/client"
import { readFileSync } from "node:fs"
import { resolve } from "node:path"
import { parseModelExportProgress } from "@/bridge/modelExportProgress"
import type { ModelTableExportProgress } from "@/bridge/protocol"

/** 使用可控真实类型化客户端, 事件只描述计数而不模拟文件发布成功. */
function setup() {
    let receive: (event: MessageEvent) => void = vi.fn()
    const sent: { id: string; method: string }[] = []
    const bridge = new BridgeClient(
        {
            postMessage: (message) => sent.push(message as (typeof sent)[number]),
            addEventListener: (_type, listener) => {
                receive = listener
            },
            removeEventListener: vi.fn(),
        },
        100,
    )
    const onProgress = vi.fn()
    const request = bridge.request(
        "model.exportTables",
        {
            projectId: "isolated",
            baselineId: "baseline",
            tables: ["one", "two"],
            rowLimit: 100,
            format: "xlsx",
            includeLog: true,
            locale: "zh-cn",
        },
        undefined,
        undefined,
        onProgress,
    )
    const send = (value: object) => receive(new MessageEvent("message", { data: { protocolVersion: 1, ...value } }))
    const value = (patch: object = {}) => ({
        requestId: request.id,
        kind: "tableExport",
        stage: "reading",
        completedTables: 0,
        totalTables: 2,
        tableIndex: 1,
        rowsRead: 0,
        ...patch,
    })
    return { bridge, sent, request, send, value, onProgress }
}

it("按原请求投递真实计数, 重复/倒退/外来/越界事件不覆盖最新进度", async () => {
    const h = setup()
    try {
        h.send({ requestProgress: h.value() })
        h.send({ requestProgress: h.value({ rowsRead: 2000 }) })
        expect(h.onProgress).toHaveBeenCalledTimes(2)
        for (const patch of [
            {},
            { rowsRead: 1000 },
            { requestId: "other" },
            { kind: "other" },
            { totalTables: 3 },
            { rowsRead: -1 },
            { rowsRead: 1.5 },
            { rowsRead: Number.MAX_SAFE_INTEGER + 1 },
            { completedTables: 3 },
            { tableIndex: 0 },
            { stage: "done" },
            { unknown: true },
        ])
            h.send({ requestProgress: h.value({ rowsRead: 2000, ...patch }) })
        expect(h.onProgress).toHaveBeenCalledTimes(2)
        h.send({ requestProgress: h.value({ stage: "verifying", rowsRead: 2000 }) })
        h.send({ requestProgress: h.value({ completedTables: 1, tableIndex: 2 }) })
        h.send({ requestProgress: h.value({ stage: "verifying", completedTables: 0, tableIndex: 1, rowsRead: 2500 }) })
        expect(h.onProgress).toHaveBeenCalledTimes(4)
        h.send({ id: h.request.id, result: {} })
        await h.request.promise
        h.send({ requestProgress: h.value({ completedTables: 1, tableIndex: 2, rowsRead: 4000 }) })
        expect(h.onProgress).toHaveBeenCalledTimes(4)
    } finally {
        h.bridge.dispose()
        await h.request.promise.catch(() => undefined)
    }
})

it("进度不会延长固定预算, 超时取消后继续等原请求终态并忽略进度", async () => {
    vi.useFakeTimers()
    const h = setup()
    const settled = vi.fn()
    const result = h.request.promise.catch(settled)
    try {
        await vi.advanceTimersByTimeAsync(70)
        h.send({ requestProgress: h.value() })
        await vi.advanceTimersByTimeAsync(31)
        expect(h.sent).toHaveLength(2)
        expect(h.sent[1]?.method).toBe("request.cancel")
        h.send({ requestProgress: h.value({ rowsRead: 2000 }) })
        expect(h.onProgress).toHaveBeenCalledTimes(1)
        expect(settled).not.toHaveBeenCalled()
        h.send({ id: h.request.id, error: { code: "CANCELLED", message: "操作已取消" } })
        await result
        expect(settled).toHaveBeenCalledTimes(1)
    } finally {
        h.bridge.dispose()
        vi.useRealTimers()
    }
})

it("机器契约样例和类型化解析器共享固定字段及阶段, 提交后不再接收进度", async () => {
    const schema = JSON.parse(readFileSync(resolve("../tests/contracts/bridge.schema.json"), "utf8"))
    const examples = JSON.parse(readFileSync(resolve("../tests/contracts/bridge-progress.json"), "utf8")) as {
        requestProgress: ModelTableExportProgress
    }[]
    const definition = schema.$defs.modelTableExportProgress.properties.requestProgress
    let previous: ModelTableExportProgress | undefined
    for (const { requestProgress } of examples) {
        expect(Object.keys(requestProgress).toSorted()).toEqual([...definition.required].toSorted())
        expect(definition.properties.stage.enum).toContain(requestProgress.stage)
        const parsed = parseModelExportProgress(requestProgress, 2, previous)
        expect(parsed).toEqual(requestProgress)
        previous = parsed
    }
    const h = setup()
    try {
        h.send({ requestProgress: h.value() })
        h.send({ requestPhase: { requestId: h.request.id, phase: "committing" } })
        h.send({ requestProgress: h.value({ rowsRead: 5000 }) })
        expect(h.onProgress).toHaveBeenCalledTimes(1)
    } finally {
        h.bridge.dispose()
        await h.request.promise.catch(() => undefined)
    }
})
