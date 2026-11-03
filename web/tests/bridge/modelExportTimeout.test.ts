/**
 * FilePath    : pbi-utils\web\tests\bridge\modelExportTimeout.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证表导出/统计/精确计数/目录打开的有界预算、取消排空与提交后不可取消边界.
 */
import { expect, it, vi } from "vitest"
import { BridgeClient } from "@/bridge/client"

/** 可控宿主传输只投递阶段与原请求终态, 不模拟数据文件成功. */
function harness(
    timeout = 30000,
    method: "model.exportTables" | "model.tableStatistics" | "model.tableRowCounts" | "model.openExportDirectory" = "model.exportTables",
) {
    let receive: (event: MessageEvent) => void = vi.fn()
    const sent: { id: string; method: string; params: unknown }[] = []
    const bridge = new BridgeClient(
        {
            postMessage: (message) => sent.push(message as (typeof sent)[number]),
            addEventListener: (_type, listener) => {
                receive = listener
            },
            removeEventListener: vi.fn(),
        },
        timeout,
    )
    const send = (message: object) => receive(new MessageEvent("message", { data: { protocolVersion: 1, ...message } }))
    const onPhase = vi.fn()
    const request =
        method === "model.tableStatistics"
            ? bridge.request("model.tableStatistics", { projectId: "isolated", baselineId: "baseline" }, undefined, onPhase)
            : method === "model.tableRowCounts"
              ? bridge.request("model.tableRowCounts", { projectId: "isolated", baselineId: "baseline", tables: ["销售"] }, undefined, onPhase)
              : method === "model.openExportDirectory"
                ? bridge.request("model.openExportDirectory", { id: "bound-directory-record" }, undefined, onPhase)
                : bridge.request(
                      "model.exportTables",
                      { projectId: "isolated", baselineId: "baseline", tables: ["销售"], rowLimit: null, format: "xlsx", includeLog: true, locale: "zh-cn" },
                      undefined,
                      onPhase,
                  )
    const phase = (value: string) => send({ requestPhase: { requestId: request.id, phase: value } })
    return { bridge, sent, send, phase, onPhase, request }
}

it.each(
    (["model.tableStatistics", "model.tableRowCounts", "model.openExportDirectory"] as const).flatMap((method) =>
        [
            { timeout: 100, accepted: true },
            { timeout: 30000, accepted: true },
            { timeout: 30000, accepted: false },
        ].map(({ timeout, accepted }) => ({ method, timeout, accepted })),
    ),
)("$method预算 $timeout 到期取消受理=$accepted 时仍等待原请求权威终态", async ({ method, timeout, accepted }) => {
    vi.useFakeTimers()
    const { bridge, sent, send, onPhase, request } = harness(timeout, method)
    let settled = false,
        settlements = 0
    const result = request.promise.then(
        (value) => {
            settled = true
            settlements++
            return value
        },
        (error: unknown) => {
            settled = true
            settlements++
            return error
        },
    )
    try {
        const budget = timeout === 30000 && method !== "model.tableStatistics" ? 180000 : timeout
        await vi.advanceTimersByTimeAsync(budget - 1)
        expect(sent).toHaveLength(1)
        await vi.advanceTimersByTimeAsync(2)
        expect(sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: request.id } })
        expect(settled).toBe(false)
        expect(onPhase).toHaveBeenCalledExactlyOnceWith("canceling")
        send({ id: sent[1]!.id, result: { accepted } })
        await vi.advanceTimersByTimeAsync(900000)
        expect(sent).toHaveLength(2)
        expect(settled).toBe(false)
        if (accepted) {
            send({ id: request.id, error: { code: "CANCELLED", message: "已取消" } })
            await expect(result).resolves.toMatchObject({ code: "CANCELLED" })
        } else {
            const response =
                method === "model.openExportDirectory"
                    ? { opened: true, isSimulation: false }
                    : {
                          projectId: "isolated",
                          baselineId: "baseline",
                          tables: [
                              {
                                  table: "销售",
                                  rowCount: 12,
                                  sizeBytes: method === "model.tableRowCounts" ? null : 1024,
                                  isEstimate: false,
                                  isQueryCount: method === "model.tableRowCounts",
                              },
                          ],
                          isSimulation: false,
                      }
            send({ id: request.id, result: response })
            await expect(result).resolves.toEqual(response)
        }
        expect(settled).toBe(true)
        send({ id: request.id, error: { code: "LATE_DUPLICATE", message: "迟到终态" } })
        await Promise.resolve()
        expect(settlements).toBe(1)
        expect(vi.getTimerCount()).toBe(0)
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})

it.each([100, 30000])("导出选择预算 %d 到期仅取消一次, 原 Promise 等待唯一终态", async (timeout) => {
    vi.useFakeTimers()
    const { bridge, sent, send, onPhase, request } = harness(timeout)
    let settled = false
    const result = request.promise.then(
        (value) => {
            settled = true
            return value
        },
        (error: unknown) => {
            settled = true
            return error
        },
    )
    try {
        const budget = timeout === 30000 ? 180000 : timeout
        await vi.advanceTimersByTimeAsync(budget - 1)
        expect(sent).toHaveLength(1)
        await vi.advanceTimersByTimeAsync(2)
        expect(sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: request.id } })
        expect(onPhase).toHaveBeenCalledExactlyOnceWith("canceling")
        send({ id: sent[1]!.id, result: { accepted: true } })
        await vi.advanceTimersByTimeAsync(900000)
        expect(sent).toHaveLength(2)
        expect(settled).toBe(false)
        send({ id: request.id, error: { code: "CANCELLED", message: "已取消" } })
        await expect(result).resolves.toMatchObject({ code: "CANCELLED" })
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})

it("首次 exporting 进入 600 秒预算, 重复阶段不延长且超时后迟到阶段不重置", async () => {
    vi.useFakeTimers()
    const { bridge, sent, send, phase, onPhase, request } = harness()
    const result = request.promise.catch((error: unknown) => error)
    try {
        await vi.advanceTimersByTimeAsync(179999)
        phase("exporting")
        await vi.advanceTimersByTimeAsync(599999)
        expect(sent).toHaveLength(1)
        phase("exporting")
        await vi.advanceTimersByTimeAsync(2)
        expect(sent).toHaveLength(2)
        expect(onPhase.mock.calls).toEqual([["exporting"], ["canceling"]])
        send({ id: sent[1]!.id, result: { accepted: true } })
        phase("exporting")
        await vi.advanceTimersByTimeAsync(600001)
        expect(sent).toHaveLength(2)
        send({ id: request.id, error: { code: "CANCELLED", message: "已取消" } })
        await expect(result).resolves.toMatchObject({ code: "CANCELLED" })
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})

it.each(["model.exportTables", "model.openExportDirectory"] as const)("%s committing 清除时限并排空, 迟到 exporting 不再开启取消计时", async (method) => {
    vi.useFakeTimers()
    const { bridge, sent, send, phase, onPhase, request } = harness(100, method)
    let settled = false
    const result = request.promise.catch((error: unknown) => error)
    void result.then(() => {
        settled = true
    })
    try {
        phase("committing")
        phase("exporting")
        phase("committing")
        await vi.advanceTimersByTimeAsync(900000)
        expect(sent).toHaveLength(1)
        expect(settled).toBe(false)
        expect(onPhase).toHaveBeenCalledExactlyOnceWith("committing")
        const response =
            method === "model.openExportDirectory"
                ? { opened: true, isSimulation: false }
                : {
                      fileName: "销售.xlsx",
                      rowCount: 0,
                      format: "xlsx",
                      rowLimit: null,
                      locationWarning: null,
                      tables: [{ table: "销售", rowCount: 0 }],
                      isSimulation: false,
                  }
        send({ id: request.id, result: response })
        await expect(result).resolves.toEqual(response)
        expect(settled).toBe(true)
        expect(vi.getTimerCount()).toBe(0)
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})
