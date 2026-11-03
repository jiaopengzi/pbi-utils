/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\bridgeBoundary.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地函数写入进入提交后等待真实终态, 超时不得丢弃业务回执.
 */
import { expect, it, vi } from "vitest"
import { BridgeClient } from "@/bridge/client"

it.each(["udf.localSave", "udf.localDelete"] as const)("%s 提交后超过原超时仍接收成功及失败回执", async (method) => {
    vi.useFakeTimers()
    let receive: (event: MessageEvent) => void = vi.fn()
    const postMessage = vi.fn()
    const bridge = new BridgeClient(
        {
            postMessage,
            addEventListener: (_type, listener) => {
                receive = listener
            },
            removeEventListener: vi.fn(),
        },
        50,
    )
    /** 投递当前请求的权威协议消息, 不创建实际本地文件. */
    function send(value: object): void {
        receive(new MessageEvent("message", { data: { protocolVersion: 1, ...value } }))
    }
    try {
        const onPhase = vi.fn()
        const pending =
            method === "udf.localSave"
                ? bridge.request(
                      method,
                      { definition: { schemaVersion: 1, name: "Local.Test", version: "1.0.0", expression: "() => 1", description: "", dependencies: [] } },
                      undefined,
                      onPhase,
                  )
                : bridge.request(method, { id: "bound-id", baseline: "bound-baseline", confirm: true }, undefined, onPhase)
        let settled = false
        const terminal = pending.promise.then((value) => {
            settled = true
            return value
        })
        send({ requestPhase: { requestId: pending.id, phase: "committing" } })
        await vi.advanceTimersByTimeAsync(1000)
        expect(settled).toBe(false)
        expect(postMessage).toHaveBeenCalledTimes(1)
        expect(onPhase).toHaveBeenCalledExactlyOnceWith("committing")
        const authoritative =
            method === "udf.localSave"
                ? {
                      id: "new-stable-id",
                      baseline: "committed",
                      definition: { schemaVersion: 1, name: "Local.Test", version: "1.0.0", expression: "() => 1", description: "", dependencies: [] },
                  }
                : { deleted: true }
        send({ id: pending.id, result: authoritative })
        await expect(terminal).resolves.toEqual(authoritative)
        const failing =
            method === "udf.localSave"
                ? bridge.request(method, {
                      definition: { schemaVersion: 1, name: "Local.Other", version: "1.0.0", expression: "() => 2", description: "", dependencies: [] },
                  })
                : bridge.request(method, { id: "second", baseline: "baseline", confirm: true })
        const failure = failing.promise.then(
            () => "unexpected",
            (cause: unknown) => cause,
        )
        send({ requestPhase: { requestId: failing.id, phase: "committing" } })
        await vi.advanceTimersByTimeAsync(1000)
        send({ id: failing.id, error: { code: "UDF_COMMIT_FAILED", message: "write failed" } })
        expect(await failure).toMatchObject({ code: "UDF_COMMIT_FAILED" })
        expect(postMessage).toHaveBeenCalledTimes(2)
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})
