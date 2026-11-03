/**
 * FilePath    : pbi-utils\web\tests\bridge\dataReset.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证固定无参重置的确认预算和原有自定义超时, 不允许调用方指定删除路径.
 */
import { afterEach, expect, it, vi } from "vitest"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
afterEach(() => vi.useRealTimers())

it("重置允许两轮确认的有界等待, 控制回复仍严格匹配原请求", async () => {
    vi.useFakeTimers()
    let receive: (event: MessageEvent) => void = vi.fn()
    const bridge = new BridgeClient({
        postMessage: vi.fn(),
        addEventListener: (_type, handler) => {
            receive = handler
        },
        removeEventListener: vi.fn(),
    })
    try {
        const request = bridge.request("app.resetUserData", {})
        const resolved = vi.fn()
        void request.promise.then(resolved)
        await vi.advanceTimersByTimeAsync(360000)
        expect(resolved).not.toHaveBeenCalled()
        receive(new MessageEvent("message", { data: { protocolVersion: 1, id: request.id, result: { scheduled: false } } }))
        await expect(request.promise).resolves.toEqual({ scheduled: false })
    } finally {
        bridge.dispose()
    }
})

it("自定义短超时仍生效, 模拟传输拒绝任何路径或确认绕过参数", async () => {
    vi.useFakeTimers()
    const bridge = new BridgeClient({ postMessage: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn() }, 10)
    try {
        const request = bridge.request("app.resetUserData", {})
        const rejected = expect(request.promise).rejects.toMatchObject({ code: "TIMEOUT" })
        await vi.advanceTimersByTimeAsync(10)
        await rejected
    } finally {
        bridge.dispose()
    }
    const mock = new MockTransport()
    const responses: MessageEvent[] = []
    const receiveMock = (event: MessageEvent) => responses.push(event)
    mock.addEventListener("message", receiveMock)
    try {
        const bad = { path: "C:/outside", confirmed: true }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 原始外部输入进入本地模拟传输, 不放宽类型化 bridge 的无参契约.
        mock.postMessage({ protocolVersion: 1, id: "bad-reset", method: "app.resetUserData", params: bad })
        expect(responses[0]?.data).toMatchObject({ id: "bad-reset", error: { code: "VALIDATION_ERROR" } })
    } finally {
        mock.removeEventListener("message", receiveMock)
    }
})
