/**
 * FilePath    : pbi-utils\web\tests\bridge\contracts.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用共享样例验证 TypeScript 请求字段与响应关联契约.
 */

import { expect, it } from "vitest"
import fixtures from "../../../tests/contracts/bridge.json"
import { BridgeClient, BridgeError, type Methods, type Transport } from "@/bridge/client"
import templateQueries from "@/features/scaffold/templateQueries.json"

/** 与 C# dispatcher 共用的实际结果和错误样例, 旧基线仍允许声明字段. */
interface ContractFixture {
    id: string
    method: string
    params: unknown
    expectedField?: string
    result?: unknown
    error?: { code: string; message: string }
}

it("辅助表跨语言契约覆盖全部四项及 RefreshTime 独立分组", () => {
    const updates = fixtures.filter((fixture) => fixture.method === "scaffold.updateDraft")
    const selection = updates.find((fixture) => "section" in fixture.params && fixture.params.section === "templateQueries")
    const groups = updates.find((fixture) => "section" in fixture.params && fixture.params.section === "templateQueryGroups")
    expect(selection?.params).toMatchObject({ value: templateQueries.map((query) => query.id) })
    expect(selection?.params).toMatchObject({ value: expect.arrayContaining(["RefreshTime"]) })
    expect(groups?.params).toMatchObject({ value: { RefreshTime: "model" } })
})

it("C# 和 TS 使用相同请求字段、协议版本与响应关联契约", async () => {
    let listener: (event: MessageEvent) => void
    let currentFixture: ContractFixture
    const transport: Transport = {
        /** 保存客户端监听器用于发送结构化响应. */
        addEventListener: (_type, handler) => {
            listener = handler
        },
        /** 契约测试无需外部资源释放. */
        removeEventListener: () => undefined,
        /** 检查实际客户端输出与共享样例一致, 再发匹配 ID 的响应. */
        postMessage: (value) => {
            const message = value as {
                protocolVersion: number
                id: string
                method: string
                params: unknown
            }
            const fixture = currentFixture
            expect(message.protocolVersion).toBe(1)
            expect(message.method).toBe(fixture.method)
            expect(message.params).toEqual(fixture.params)
            expect(typeof message.id).toBe("string")
            listener(
                new MessageEvent("message", {
                    data: {
                        protocolVersion: 1,
                        id: message.id,
                        ...(fixture.error ? { error: fixture.error } : { result: fixture.result ?? { [fixture.expectedField!]: "fixture" } }),
                    },
                }),
            )
        },
    }
    const bridge = new BridgeClient(transport)
    for (const fixture of fixtures as ContractFixture[]) {
        currentFixture = fixture
        const method = fixture.method as keyof Methods
        // 按共享样例逐项请求并断言, 失败时明确定位对应契约.
        // oxlint-disable-next-line no-await-in-loop
        const request = bridge.request(method, fixture.params as Methods[typeof method]["params"])
        if (fixture.error) {
            // oxlint-disable-next-line no-await-in-loop -- 每个固定错误样例单独验证关联和稳定错误码.
            await expect(request.promise).rejects.toMatchObject(new BridgeError(fixture.error.code, fixture.error.message))
        } else {
            // oxlint-disable-next-line no-await-in-loop -- 同一传输按共享夹具串行进行实际往返.
            const result = await request.promise
            if (fixture.result !== undefined) expect(result).toEqual(fixture.result)
            if (fixture.expectedField) expect(result).toHaveProperty(fixture.expectedField)
        }
    }
    bridge.dispose()
})
