/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfCacheRefresh.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证缓存已提交但旧代清理失败的事实在只读核验后仍对用户可见.
 */
import { expect, it } from "vitest"
import { mount, flushPromises } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient, type Transport } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import UdfStep from "@/features/scaffold/UdfStep.vue"

/** 仅将模拟已提交刷新回执替换为清理错误; 后续核验仍读取真实模拟当前缓存. */
class CleanupFailureTransport implements Transport {
    private mock = new MockTransport()
    private listeners = new Map<(event: MessageEvent) => void, (event: MessageEvent) => void>()
    private refreshes = new Set<string>()
    calls: { method: string; params: Record<string, unknown> }[] = []

    /** failVerification 仅控制提交后只读状态的失败, 不影响缓存更新本身. */
    constructor(private failVerification: boolean) {}

    /** 转发当前模拟状态, 缓存更新后的清理错误不还原已更新事实. */
    addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        const wrapped = (event: MessageEvent) => {
            const data = event.data as { id?: string; result?: unknown }
            if (data.id && this.refreshes.has(data.id) && data.result)
                listener(
                    new MessageEvent("message", {
                        data: {
                            protocolVersion: 1,
                            id: data.id,
                            error: { code: "UDF_CATALOG_CLEANUP_FAILED", message: "函数缓存已更新, 旧缓存清理失败 (IOException)." },
                        },
                    }),
                )
            else listener(event)
        }
        this.listeners.set(listener, wrapped)
        this.mock.addEventListener(type, wrapped)
    }

    /** 卸载只移除本次注册的观察者. */
    removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.mock.removeEventListener(type, this.listeners.get(listener)!)
        this.listeners.delete(listener)
    }

    /** 失败核验只模拟读取失败, 刷新请求始终先由产品模拟器完成真实内存提交. */
    postMessage(value: unknown): void {
        const request = value as { id: string; method: string; params: Record<string, unknown> }
        this.calls.push(request)
        if (request.method === "udf.catalogRefresh") this.refreshes.add(request.id)
        if (this.failVerification && this.refreshes.size && request.method === "udf.catalog" && request.params.action === "status") {
            for (const listener of this.listeners.keys())
                listener(
                    new MessageEvent("message", {
                        data: { protocolVersion: 1, id: request.id, error: { code: "UDF_CATALOG_INVALID", message: "当前缓存核验读取失败" } },
                    }),
                )
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin -- 这是类型化宿主模拟入口, 不是浏览器 window.postMessage.
        this.mock.postMessage(value)
    }
}

it.each([false, true])("已更新但清理失败在只读核验后保留原错误, 核验失败=%s", async (failVerification) => {
    const transport = new CleanupFailureTransport(failVerification)
    const bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    try {
        await flushPromises()
        await wrapper.find('[data-test="udf-refresh"]').trigger("click")
        await flushPromises()
        expect(document.body.textContent).toContain("函数缓存已更新, 但旧缓存清理失败")
        expect(transport.calls.filter((item) => item.method === "udf.catalogRefresh")).toHaveLength(1)
        expect(transport.calls.some((item) => item.method === "udf.catalog" && item.params.action === "status")).toBe(true)
        if (failVerification) {
            expect(document.body.textContent).toContain("当前缓存核验读取失败")
            expect(wrapper.find(".udf-stale").text()).toContain("函数缓存已更新, 但旧缓存清理失败")
            expect(wrapper.find(".udf-stale").text()).not.toContain("提交结果尚未确认")
            expect(wrapper.find('[data-test="udf-refresh"]').attributes("disabled")).toBeDefined()
        } else expect(wrapper.findAll(".udf-library-card")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})
