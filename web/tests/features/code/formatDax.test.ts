/**
 * FilePath    : pbi-utils\web\tests\features\code\formatDax.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证格式化调用默认发送 long, 显式 short 仍原样进入严格桥接契约.
 */
import { expect, it, vi } from "vitest"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { formatDax } from "@/features/code/formatDax"

it.each([undefined, "long", "short"] as const)("格式化样式 %s 使用明确的桥接参数且保留显式选择", async (style) => {
    const bridge = new BridgeClient(new MockTransport())
    const request = vi.spyOn(bridge, "request")
    try {
        const pending = style === undefined ? formatDax(bridge, ["DIVIDE(1,2)"]) : formatDax(bridge, ["DIVIDE(1,2)"], style)
        await pending.promise
        expect(request).toHaveBeenCalledExactlyOnceWith("dax.format", { value: '["DIVIDE(1,2)"]', style: style ?? "long" })
    } finally {
        bridge.dispose()
        request.mockRestore()
    }
})
