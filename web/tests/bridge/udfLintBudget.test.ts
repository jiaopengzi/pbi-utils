/**
 * FilePath    : pbi-utils\web\tests\bridge\udfLintBudget.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 UDF 清理预估含完整连接代际, 精确字节边界不允许不可发送的删除批次.
 */
import { expect, it } from "vitest"
import { udfLintDeleteAllowed, udfLintDeleteRequest } from "@/bridge/udfLintBudget"
import type { UdfLintResult } from "@/bridge/client"

it("最大安全代际下完整65536字节可发送, 多一字节禁止而不清除原名称", () => {
    const value: UdfLintResult = {
        projectId: "p",
        baselineId: "b".repeat(64),
        complete: true,
        totalCount: 128,
        unused: Array.from({ length: 128 }, (_, index) => `Ns.F${index}${"中".repeat(160)}`),
    }
    const bytes = () =>
        new TextEncoder().encode(
            JSON.stringify({
                protocolVersion: 1,
                id: "0".repeat(36),
                method: "model.edit",
                params: udfLintDeleteRequest(value),
                connectionRevision: Number.MAX_SAFE_INTEGER,
            }),
        ).length
    let remaining = 65536 - bytes()
    value.unused = value.unused.map((name) => {
        const added = Math.min(200 - name.length, remaining)
        remaining -= added
        return name + "a".repeat(added)
    })
    expect(remaining).toBe(0)
    expect(bytes()).toBe(65536)
    expect(udfLintDeleteAllowed(value)).toBe(true)
    value.unused[value.unused.length - 1] += "a"
    expect(bytes()).toBe(65537)
    expect(udfLintDeleteAllowed(value)).toBe(false)
    expect(value.unused).toHaveLength(128)
})
