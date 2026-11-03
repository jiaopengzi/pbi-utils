/**
 * FilePath    : pbi-utils\web\tests\bridge\client.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证桥接成功, 错误, 事件, 取消, 超时与销毁行为.
 */

import { describe, expect, it, vi } from "vitest"
import { BridgeClient, hostTransport, type OperationEvent } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { hostText, setLocale } from "@/i18n"
import contractFixtures from "../../../tests/contracts/bridge.json"
import requestSchema from "../../../tests/contracts/bridge.schema.json"
import { previewProject } from "@/bridge/preview"

/** 按实际接收顺序投递宿主对话框消息, 不创建业务请求或真实授权. */
function dialogHarness() {
    let receive: (event: MessageEvent) => void = vi.fn()
    const bridge = new BridgeClient({
        postMessage: vi.fn(),
        addEventListener: (_type, listener) => {
            receive = listener
        },
        removeEventListener: vi.fn(),
    })
    const send = (message: object) => receive(new MessageEvent("message", { data: { protocolVersion: 1, ...message } }))
    const open = (id: string, titleKey = "hostDialog.closeTitle") => send({ uiDialog: { id, kind: "confirm", titleKey } })
    const close = (id: string) => send({ uiDialogClosed: { id } })
    return { bridge, open, close }
}

/** 提供可控的宿主消息门闩, 返回客户端、请求记录与独立事件/响应投递入口. */
function requestHarness() {
    let receive: (event: MessageEvent) => void = vi.fn()
    const postMessage = vi.fn()
    const bridge = new BridgeClient({
        postMessage,
        addEventListener: (_type, listener) => {
            receive = listener
        },
        removeEventListener: vi.fn(),
    })
    const send = (message: object) => receive(new MessageEvent("message", { data: { protocolVersion: 1, ...message } }))
    /** 按给定请求身份、序号及类型投递完整宿主事件, 不自行裁决业务终态. */
    function event(requestId: string, sequence: number, type: string): void {
        send({ event: { requestId, sequence, type, timestamp: new Date(0).toISOString(), payload: {} } })
    }
    return { bridge, postMessage, send, event }
}

describe("类型化桥接", () => {
    it.each(['"', "\\", "中"])("日期回复的%s转义或UTF-8膨胀仍受完整4MiB限制", async (character) => {
        const { bridge, send } = requestHarness()
        try {
            const request = bridge.request("calendar.preview", {
                context: { kind: "template", templateId: "blank", templateBaseline: "b".repeat(64) },
                calendar: { kind: "simple", startYear: 2024, endYear: 2024, tableName: "Dates", overrides: [] },
                locale: "zh-cn",
            })
            send({ id: request.id, connectionRevision: 0, result: { functionExpression: character.repeat(2200000) } })
            await expect(request.promise).rejects.toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        } finally {
            bridge.dispose()
        }
    })
    it.each([
        [4 * 1024 * 1024, 0],
        [4 * 1024 * 1024 + 1, 0],
        [4 * 1024 * 1024, Number.MAX_SAFE_INTEGER],
        [4 * 1024 * 1024 + 1, Number.MAX_SAFE_INTEGER],
    ])("日期回复按实际%d字节与代际%d核验完整信封", async (size, connectionRevision) => {
        const { bridge, send } = requestHarness()
        try {
            const request = bridge.request("calendar.preview", {
                context: { kind: "template", templateId: "blank", templateBaseline: "b".repeat(64) },
                calendar: { kind: "simple", startYear: 2024, endYear: 2024, tableName: "Dates", overrides: [] },
                locale: "zh-cn",
            })
            const envelope = { protocolVersion: 1, id: request.id, connectionRevision, result: "" }
            const overhead = new TextEncoder().encode(JSON.stringify(envelope)).length
            envelope.result = "a".repeat(size - overhead)
            send(envelope)
            if (size === 4 * 1024 * 1024) await expect(request.promise).resolves.toHaveLength(size - overhead)
            else await expect(request.promise).rejects.toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        } finally {
            bridge.dispose()
        }
    })
    it("未关联的工作区消息不会自动采用, 旧请求与迟到结果被拒绝", async () => {
        const { bridge, postMessage, send } = requestHarness()
        try {
            const oldRead = bridge.request("project.inspect", {})
            const oldResult = expect(oldRead.promise).rejects.toMatchObject({ code: "STALE_CONNECTION" })
            send({ workspaceChanged: { revision: 1, requestId: "unassociated-change", project: previewProject } })
            send({ id: oldRead.id, result: previewProject, connectionRevision: 1 })
            await oldResult
            await expect(bridge.request("model.save", {}).promise).rejects.toMatchObject({ code: "STALE_CONNECTION" })
            const settings = bridge.request("ui.settings", {})
            expect(postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ connectionRevision: 0 }))
            send({ id: settings.id, result: {}, connectionRevision: 1 })
            await settings.promise
            await expect(bridge.request("project.inspect", {}).promise).rejects.toMatchObject({ code: "STALE_CONNECTION" })
            send({ workspaceChanged: { revision: 0, requestId: "late", project: null } })
            await expect(bridge.request("model.save", {}).promise).rejects.toMatchObject({ code: "STALE_CONNECTION" })
            expect(postMessage).toHaveBeenCalledTimes(2)
        } finally {
            bridge.dispose()
        }
    })

    it("本页面连接更新自身权威代际, 后续读取绑定该代际且旧结果不能回退", async () => {
        const { bridge, postMessage, send } = requestHarness()
        try {
            const previousRead = bridge.request("project.inspect", {})
            const stale = expect(previousRead.promise).rejects.toMatchObject({ code: "STALE_CONNECTION" })
            const connection = bridge.request("project.connectLocal", { processId: "42" })
            send({ workspaceChanged: { revision: 1, requestId: connection.id, project: previewProject } })
            send({ id: connection.id, result: previewProject, connectionRevision: 1 })
            await expect(connection.promise).resolves.toEqual(previewProject)
            send({ id: previousRead.id, result: previewProject, connectionRevision: 0 })
            await stale
            send({ workspaceChanged: { revision: 0, requestId: "late", project: null } })
            const read = bridge.request("project.inspect", {})
            expect(postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ connectionRevision: 1 }))
            send({ id: read.id, result: previewProject })
            await read.promise
        } finally {
            bridge.dispose()
        }
    })

    it("首次目标成功回包建立代际, 失败和无关读取不能隐式改绑", async () => {
        const { bridge, postMessage, send } = requestHarness()
        try {
            const settings = bridge.request("ui.settings", {})
            send({ id: settings.id, result: {}, connectionRevision: 8 })
            await settings.promise
            const failed = bridge.request("project.inspect", {})
            expect(postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ connectionRevision: 0 }))
            const failure = expect(failed.promise).rejects.toMatchObject({ code: "PROJECT_NOT_SELECTED" })
            send({ id: failed.id, error: { code: "PROJECT_NOT_SELECTED", message: "No project" }, connectionRevision: 8 })
            await failure
            const initial = bridge.request("project.inspect", {})
            expect(postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ connectionRevision: 0 }))
            send({ id: initial.id, result: previewProject, connectionRevision: 2 })
            await initial.promise
            const read = bridge.request("project.inspect", {})
            expect(postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ connectionRevision: 2 }))
            send({ id: read.id, result: previewProject })
            await read.promise
        } finally {
            bridge.dispose()
        }
    })
    it.each(["hello", "agent.hello"])("旧演示方法 %s 不再注册到请求契约或共享样例", (method) => {
        expect(requestSchema.properties.method.enum).not.toContain(method)
        expect(requestSchema.allOf.some((rule) => rule.if.properties.method.const === method)).toBe(false)
        expect(contractFixtures.some((fixture) => fixture.method === method)).toBe(false)
    })

    it.each(["hello", "agent.hello"])("显式预览拒绝旧演示方法 %s 且不生成事件", async (method) => {
        const transport = new MockTransport()
        const events: unknown[] = []
        const id = `retired-${method}`
        let receive: (event: MessageEvent) => void = vi.fn()
        try {
            const response = await new Promise<unknown>((resolve) => {
                receive = (event: MessageEvent) => {
                    if (event.data.event) events.push(event.data.event)
                    if (event.data.id === id) resolve(event.data)
                }
                transport.addEventListener("message", receive)
                // WebView2 的单参数传输夹具, 不是浏览器 window.postMessage.
                // oxlint-disable-next-line unicorn/require-post-message-target-origin
                transport.postMessage({ protocolVersion: 1, id, method, params: {} })
            })
            expect(response).toMatchObject({ error: { code: "UNKNOWN_METHOD" } })
            expect(events).toEqual([])
        } finally {
            transport.removeEventListener("message", receive)
        }
    })

    it("应用信息仅包含版本、WebView2 与启动目标标记, 不携带目标地址或旧诊断演示字段", async () => {
        const bridge = new BridgeClient(new MockTransport())
        try {
            const result = await bridge.request("app.info", {}).promise
            expect(Object.keys(result).toSorted()).toEqual(["hasExternalToolTarget", "version", "webView2"])
            expect(result).toEqual({ version: "browser-preview", webView2: "未连接", hasExternalToolTarget: false })
        } finally {
            bridge.dispose()
        }
    })

    it("关闭先于排队打开时忽略旧身份, 旧重复打开和关闭不覆盖当前确认", () => {
        const { bridge, open, close } = dialogHarness()
        try {
            const listener = vi.fn()
            bridge.onDialog(listener)
            close("old-dialog")
            open("current-dialog")
            open("old-dialog")
            open("current-dialog", "hostDialog.sampleOverwriteTitle")
            close("old-dialog")
            expect(listener).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ id: "current-dialog", titleKey: "hostDialog.closeTitle" }))
            const replay = vi.fn()
            bridge.onDialog(replay)
            expect(replay).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ id: "current-dialog", titleKey: "hostDialog.closeTitle" }))
        } finally {
            bridge.dispose()
        }
    })

    it("新确认替换旧确认后忽略旧打开, 当前确认关闭后不会由迟到事件重新出现", () => {
        const { bridge, open, close } = dialogHarness()
        try {
            const listener = vi.fn()
            bridge.onDialog(listener)
            open("old-dialog")
            open("new-dialog")
            open("old-dialog")
            close("new-dialog")
            open("new-dialog")
            expect(listener.mock.calls.map(([dialog]) => dialog.id)).toEqual(["old-dialog", "new-dialog"])
            const replay = vi.fn()
            bridge.onDialog(replay)
            expect(replay).not.toHaveBeenCalled()
        } finally {
            bridge.dispose()
        }
    })

    it("保留最近256个关闭身份后仍接受新确认, 销毁后的排队消息不恢复确认", () => {
        const { bridge, open, close } = dialogHarness()
        const listener = vi.fn()
        bridge.onDialog(listener)
        for (let index = 0; index < 300; index++) close("retired-" + index)
        for (let index = 44; index < 300; index++) open("retired-" + index)
        expect(listener).not.toHaveBeenCalled()
        open("fresh-dialog")
        expect(listener).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ id: "fresh-dialog" }))
        bridge.dispose()
        open("late-after-dispose")
        const replay = vi.fn()
        bridge.onDialog(replay)
        expect(replay).not.toHaveBeenCalled()
    })

    it("最近记录删除模拟只更新当前来源及账号云分区, 不断开模型且重复删除返回 false", async () => {
        const bridge = new BridgeClient(new MockTransport())
        try {
            await bridge.request("project.open", {}).promise
            const local = (await bridge.request("project.recent", {}).promise)[0]!
            await bridge.request("powerbi.serviceSignIn", { email: "first@example.com", environmentName: "GlobalCloud", remember: false }).promise
            await bridge.request("project.connectService", { modelId: "preview-service-model" }).promise
            const cloud = (await bridge.request("powerbi.serviceRecentModels", {}).promise).items[0]!
            await bridge.request("powerbi.serviceSignOut", {}).promise
            await bridge.request("powerbi.serviceSignIn", { email: "second@example.com", environmentName: "GlobalCloud", remember: false }).promise
            expect((await bridge.request("powerbi.serviceDeleteRecentModel", { id: cloud.id }).promise).deleted).toBe(false)
            await bridge.request("powerbi.serviceSignOut", {}).promise
            await bridge.request("powerbi.serviceSignIn", { email: "first@example.com", environmentName: "ChinaCloud", remember: false }).promise
            expect((await bridge.request("powerbi.serviceDeleteRecentModel", { id: cloud.id }).promise).deleted).toBe(false)
            await bridge.request("powerbi.serviceSignOut", {}).promise
            await bridge.request("powerbi.serviceSignIn", { email: "first@example.com", environmentName: "GlobalCloud", remember: false }).promise
            expect((await bridge.request("powerbi.serviceRecentModels", {}).promise).items).toHaveLength(1)
            expect((await bridge.request("project.deleteRecent", { id: local.id }).promise).deleted).toBe(true)
            expect((await bridge.request("project.deleteRecent", { id: local.id }).promise).deleted).toBe(false)
            expect(await bridge.request("project.recent", {}).promise).toEqual([])
            expect((await bridge.request("powerbi.serviceRecentModels", {}).promise).items).toHaveLength(1)
            expect((await bridge.request("powerbi.serviceDeleteRecentModel", { id: cloud.id }).promise).deleted).toBe(true)
            expect((await bridge.request("powerbi.serviceDeleteRecentModel", { id: cloud.id }).promise).deleted).toBe(false)
            expect((await bridge.request("powerbi.serviceRecentModels", {}).promise).items).toEqual([])
            expect((await bridge.request("project.inspect", {}).promise).connection?.kind).toBe("powerbi-service")
            await bridge.request("powerbi.serviceSignOut", {}).promise
            await expect(bridge.request("powerbi.serviceDeleteRecentModel", { id: cloud.id }).promise).rejects.toMatchObject({
                code: "POWER_BI_SIGN_IN_REQUIRED",
            })
        } finally {
            bridge.dispose()
        }
    })

    it("登录只接受顺序检测与认证阶段, 重复、倒退和其他方法通知不延长认证时限", async () => {
        vi.useFakeTimers()
        let receive: (event: MessageEvent) => void = vi.fn()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: (_: "message", callback: (event: MessageEvent) => void) => {
                receive = callback
            },
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport)
        try {
            const onPhase = vi.fn()
            const request = bridge.request("powerbi.serviceSignIn", { email: "preview@example.com", environmentName: "", remember: true }, undefined, onPhase)
            const rejection = expect(request.promise).rejects.toMatchObject({ code: "TIMEOUT" })
            for (const phase of [
                "discoveringEnvironment",
                "discoveringEnvironment",
                "authenticating",
                "discoveringEnvironment",
                "authenticating",
                "connecting",
            ]) {
                receive({ data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase } } } as MessageEvent)
            }
            expect(onPhase.mock.calls.flat()).toEqual(["discoveringEnvironment", "authenticating"])
            const statusPhase = vi.fn()
            const status = bridge.request("powerbi.serviceStatus", {}, undefined, statusPhase)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: status.id, phase: "authenticating" } } } as MessageEvent)
            receive({ data: { protocolVersion: 1, id: status.id, result: {} } } as MessageEvent)
            await status.promise
            expect(statusPhase).not.toHaveBeenCalled()
            await vi.advanceTimersByTimeAsync(179999)
            expect(transport.postMessage).toHaveBeenCalledTimes(2)
            await vi.advanceTimersByTimeAsync(1)
            await rejection
            expect(transport.postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ method: "request.cancel", params: { requestId: request.id } }))
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "authenticating" } } } as MessageEvent)
            expect(onPhase).toHaveBeenCalledTimes(2)
        } finally {
            bridge.dispose()
            vi.useRealTimers()
        }
    })

    it.each(["project.open", "project.openRecent", "scaffold.openDesktop"] as const)(
        "%s 给文件选择和 Desktop 启动独立 185 秒, 重复阶段不延长且迟到响应释放",
        async (method) => {
            vi.useFakeTimers()
            let receive: (event: MessageEvent) => void = vi.fn()
            const transport = {
                postMessage: vi.fn(),
                addEventListener: (_: "message", callback: (event: MessageEvent) => void) => {
                    receive = callback
                },
                removeEventListener: vi.fn(),
            }
            const bridge = new BridgeClient(transport)
            try {
                const phase = vi.fn()
                const request = bridge.request(method, method === "project.openRecent" ? { id: "saved-file-id" } : {}, undefined, phase)
                const rejection = expect(request.promise).rejects.toThrow("请求超时")
                if (method === "scaffold.openDesktop") {
                    receive({ data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "committing" } } } as MessageEvent)
                    expect(phase).not.toHaveBeenCalled()
                }
                await vi.advanceTimersByTimeAsync(184000)
                expect(transport.postMessage).toHaveBeenCalledTimes(1)
                receive({ data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "connecting" } } } as MessageEvent)
                expect(phase).toHaveBeenCalledExactlyOnceWith("connecting")
                await vi.advanceTimersByTimeAsync(184000)
                expect(transport.postMessage).toHaveBeenCalledTimes(1)
                receive({ data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "connecting" } } } as MessageEvent)
                expect(phase).toHaveBeenCalledTimes(1)
                await vi.advanceTimersByTimeAsync(1000)
                await rejection
                expect(transport.postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ method: "request.cancel", params: { requestId: request.id } }))
                receive({ data: { protocolVersion: 1, id: request.id, result: { unexpected: "late" } } } as MessageEvent)
            } finally {
                bridge.dispose()
                vi.useRealTimers()
            }
        },
    )

    it("界面偏好模拟校验非空补丁白名单, 不同字段更新保留最新其他值, 无效输入不写入", async () => {
        const bridge = new BridgeClient(new MockTransport())
        const initial = await bridge.request("ui.settings", {}).promise
        expect(initial).toMatchObject({ locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "long", isSimulation: true })
        await expect(bridge.request("ui.saveSettings", {}).promise).rejects.toMatchObject({ code: "VALIDATION_ERROR" })
        await expect(bridge.request("ui.saveSettings", { locale: null } as never).promise).rejects.toMatchObject({
            code: "VALIDATION_ERROR",
        })
        await expect(bridge.request("ui.saveSettings", { daxFormatStyle: null } as never).promise).rejects.toMatchObject({ code: "VALIDATION_ERROR" })
        await expect(
            bridge.request("ui.saveSettings", { locale: "en-us", showRecentFiles: false, daxFormatStyle: "LONG" } as never).promise,
        ).rejects.toMatchObject({
            code: "INVALID_DAX_FORMAT_STYLE",
        })
        await expect(
            bridge.request("ui.saveSettings", { locale: "en-us", showRecentFiles: "false", daxFormatStyle: "long" } as never).promise,
        ).rejects.toMatchObject({
            code: "VALIDATION_ERROR",
        })
        await expect(
            bridge.request("ui.saveSettings", { locale: "en-us", showRecentFiles: false, daxFormatStyle: "long", path: "unexpected" } as never).promise,
        ).rejects.toMatchObject({ code: "VALIDATION_ERROR" })
        expect(await bridge.request("ui.settings", {}).promise).toEqual(initial)
        expect(await bridge.request("ui.saveSettings", { daxFormatStyle: "short" }).promise).toMatchObject({ locale: "zh-cn", daxFormatStyle: "short" })
        expect(await bridge.request("ui.saveSettings", { locale: "en-us" }).promise).toMatchObject({ locale: "en-us", daxFormatStyle: "short" })
        expect(await bridge.request("ui.saveSettings", { showRecentFiles: false }).promise).toMatchObject({
            locale: "en-us",
            showRecentFiles: false,
            daxFormatStyle: "short",
        })
        bridge.dispose()
    })

    it("示例生成阶段独立关闭超时, 重复与迟到阶段不改变其他请求或恢复取消", async () => {
        vi.useFakeTimers()
        let receive: (event: MessageEvent) => void = vi.fn()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: (_: "message", listener: (event: MessageEvent) => void) => {
                receive = listener
            },
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport, 100)
        const phase = vi.fn()
        try {
            const generate = bridge.request(
                "sample.generate",
                { mode: "full", locale: "zh-cn", productCount: 1, storeCount: 1, inventoryCycle: 5, startDate: "2026-01-01", endDate: "2026-03-02" },
                undefined,
                phase,
            )
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: generate.id, phase: "generating" } } } as MessageEvent)
            await vi.advanceTimersByTimeAsync(100000)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: generate.id, phase: "generating" } } } as MessageEvent)
            expect(phase).toHaveBeenCalledExactlyOnceWith("generating")
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: generate.id, phase: "committing" } } } as MessageEvent)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: generate.id, phase: "generating" } } } as MessageEvent)
            expect(phase.mock.calls.flat()).toEqual(["generating", "committing"])
            const status = bridge.request("sample.status", {})
            const rejection = expect(status.promise).rejects.toThrow("请求超时")
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: status.id, phase: "generating" } } } as MessageEvent)
            await vi.advanceTimersByTimeAsync(100)
            await rejection
            expect(transport.postMessage).toHaveBeenCalledTimes(3)
            receive({ data: { protocolVersion: 1, id: generate.id, result: { state: { tables: [] } } } } as MessageEvent)
            await expect(generate.promise).resolves.toEqual({ state: { tables: [] } })
            const later = bridge.request("sample.status", {})
            const laterRejection = expect(later.promise).rejects.toThrow("请求超时")
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: generate.id, phase: "generating" } } } as MessageEvent)
            await vi.advanceTimersByTimeAsync(100)
            await laterRejection
        } finally {
            bridge.dispose()
            vi.useRealTimers()
        }
    })
    it("保存进入提交阶段后等待明确结果, 重复提交阶段不重复回调", async () => {
        vi.useFakeTimers()
        let receive: (event: MessageEvent) => void = vi.fn()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: (_: "message", cb: (event: MessageEvent) => void) => {
                receive = cb
            },
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport, 100)
        try {
            const save = bridge.request("model.save", {})
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: save.id, phase: "committing" } } } as MessageEvent)
            await vi.advanceTimersByTimeAsync(1000)
            expect(transport.postMessage).toHaveBeenCalledTimes(1)
            receive({ data: { protocolVersion: 1, id: save.id, result: { done: true } } } as MessageEvent)
            await expect(save.promise).resolves.toEqual({ done: true })
            const onPhase = vi.fn()
            const second = bridge.request("model.save", {}, undefined, onPhase)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: second.id, phase: "committing" } } } as MessageEvent)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: second.id, phase: "committing" } } } as MessageEvent)
            expect(onPhase).toHaveBeenCalledExactlyOnceWith("committing")
            await vi.advanceTimersByTimeAsync(1000)
            expect(transport.postMessage).toHaveBeenCalledTimes(2)
            receive({ data: { protocolVersion: 1, id: second.id, result: { done: true } } } as MessageEvent)
            await expect(second.promise).resolves.toEqual({ done: true })
        } finally {
            bridge.dispose()
            vi.useRealTimers()
        }
    })
    it("远端平铺表单提交直接使用连接预算, 重复阶段通知不能续期", async () => {
        vi.useFakeTimers()
        let receive: (event: MessageEvent) => void = vi.fn()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: (_: "message", callback: (event: MessageEvent) => void) => {
                receive = callback
            },
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport)
        try {
            const onPhase = vi.fn()
            const remote = bridge.request(
                "project.connectRemote",
                {
                    formId: "remote-context",
                    values: {
                        server: "ssas",
                        database: "model",
                        authenticationMode: "current",
                        userName: "",
                        password: "",
                        remember: false,
                        rememberPassword: false,
                        recentId: "",
                    },
                },
                undefined,
                onPhase,
            )
            const rejection = expect(remote.promise).rejects.toThrow("请求超时")
            await vi.advanceTimersByTimeAsync(1000)
            expect(transport.postMessage).toHaveBeenCalledTimes(1)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: remote.id, phase: "connecting" } } } as MessageEvent)
            expect(onPhase).toHaveBeenCalledExactlyOnceWith("connecting")
            await vi.advanceTimersByTimeAsync(29000)
            expect(transport.postMessage).toHaveBeenCalledTimes(1)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: remote.id, phase: "connecting" } } } as MessageEvent)
            expect(onPhase).toHaveBeenCalledTimes(1)
            await vi.advanceTimersByTimeAsync(1000)
            await rejection
            expect(transport.postMessage).toHaveBeenCalledTimes(2)
        } finally {
            bridge.dispose()
            vi.useRealTimers()
        }
    })
    it("阶段回调拒绝未知阶段、其他请求和已结束请求, 不影响并发请求", async () => {
        let receive: (event: MessageEvent) => void = vi.fn()
        const bridge = new BridgeClient({
            postMessage: vi.fn(),
            addEventListener: (_: "message", callback: (event: MessageEvent) => void) => {
                receive = callback
            },
            removeEventListener: vi.fn(),
        })
        const remotePhase = vi.fn()
        const servicePhase = vi.fn()
        const remote = bridge.request(
            "project.connectRemote",
            {
                formId: "remote-context",
                values: {
                    server: "ssas",
                    database: "model",
                    authenticationMode: "current",
                    userName: "",
                    password: "",
                    remember: false,
                    rememberPassword: false,
                    recentId: "",
                },
            },
            undefined,
            remotePhase,
        )
        const service = bridge.request("project.connectService", { modelId: "test" }, undefined, servicePhase)
        receive({ data: { protocolVersion: 1, requestPhase: { requestId: remote.id, phase: "unknown" } } } as MessageEvent)
        receive({ data: { protocolVersion: 1, requestPhase: { requestId: service.id, phase: "connecting" } } } as MessageEvent)
        receive({ data: { protocolVersion: 1, requestPhase: { requestId: "unknown", phase: "connecting" } } } as MessageEvent)
        expect(remotePhase).not.toHaveBeenCalled()
        expect(servicePhase).not.toHaveBeenCalled()
        receive({ data: { protocolVersion: 1, id: remote.id, result: {} } } as MessageEvent)
        await remote.promise
        receive({ data: { protocolVersion: 1, requestPhase: { requestId: remote.id, phase: "connecting" } } } as MessageEvent)
        expect(remotePhase).not.toHaveBeenCalled()
        receive({ data: { protocolVersion: 1, id: service.id, result: {} } } as MessageEvent)
        await service.promise
        bridge.dispose()
    })
    it("其他请求和非法阶段不能扩大超时预算", async () => {
        vi.useFakeTimers()
        let receive: (event: MessageEvent) => void = vi.fn()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: (_: "message", callback: (event: MessageEvent) => void) => {
                receive = callback
            },
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport)
        try {
            const request = bridge.request("doctor", {})
            const rejection = expect(request.promise).rejects.toThrow("请求超时")
            await vi.advanceTimersByTimeAsync(29000)
            receive({ data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "connecting" } } } as MessageEvent)
            await vi.advanceTimersByTimeAsync(1000)
            await rejection
        } finally {
            bridge.dispose()
            vi.useRealTimers()
        }
    })
    it("生产缺少宿主时明确失败", () => {
        expect(() => hostTransport()).toThrow("未检测到桌面宿主")
    })
    it("显式预览保留正式诊断的模拟标识与已终结取消响应", async () => {
        const bridge = new BridgeClient(new MockTransport())
        try {
            const request = bridge.request("doctor", {})
            const result = await request.promise
            expect(result.isSimulation).toBe(true)
            expect(result.diagnostic.environment.desktop).toBe("simulation")
            expect((await bridge.request("request.cancel", { requestId: request.id }).promise).accepted).toBe(false)
            expect((await bridge.request("request.cancel", { requestId: "unknown-request" }).promise).accepted).toBe(false)
        } finally {
            bridge.dispose()
        }
    })
    it("正式请求仅接收匹配身份的连续事件, 完成后的迟到事件和响应不重放", async () => {
        const { bridge, send, event } = requestHarness()
        const events: OperationEvent[] = []
        try {
            const request = bridge.request("doctor", {}, (item) => events.push(item))
            event("another-request", 1, "started")
            event(request.id, 1, "started")
            event(request.id, 3, "completed")
            event(request.id, 2, "progress")
            event(request.id, 2, "progress")
            event(request.id, 3, "completed")
            send({ id: request.id, result: [] })
            await expect(request.promise).resolves.toEqual([])
            event(request.id, 4, "failed")
            send({ id: request.id, error: { code: "LATE_FAILURE", message: "late" } })
            expect(events.map((item) => item.type)).toEqual(["started", "progress", "completed"])
            expect(events.map((item) => item.sequence)).toEqual([1, 2, 3])
            expect(events.filter((item) => ["completed", "failed", "cancelled"].includes(item.type))).toHaveLength(1)
        } finally {
            bridge.dispose()
        }
    })
    it("取消确认不代替原请求终态, 唯一取消事件及最终错误完成后忽略迟到结果", async () => {
        const { bridge, postMessage, send, event } = requestHarness()
        const events: OperationEvent[] = []
        try {
            const request = bridge.request("doctor", {}, (item) => events.push(item))
            const rejection = expect(request.promise).rejects.toMatchObject({ code: "CANCELLED" })
            const settled = vi.fn()
            void request.promise.then(
                () => settled("resolved"),
                () => settled("rejected"),
            )
            event(request.id, 1, "started")
            event(request.id, 2, "progress")
            const cancel = bridge.request("request.cancel", { requestId: request.id })
            expect(postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ id: cancel.id, params: { requestId: request.id } }))
            send({ id: cancel.id, result: { accepted: true } })
            await expect(cancel.promise).resolves.toEqual({ accepted: true })
            expect(settled).not.toHaveBeenCalled()
            event(request.id, 3, "cancelled")
            event(request.id, 3, "cancelled")
            expect(settled).not.toHaveBeenCalled()
            send({ id: request.id, error: { code: "CANCELLED", message: "已取消操作" } })
            await rejection
            expect(settled).toHaveBeenCalledExactlyOnceWith("rejected")
            event(request.id, 4, "completed")
            send({ id: request.id, result: [] })
            const repeated = bridge.request("request.cancel", { requestId: request.id })
            send({ id: repeated.id, result: { accepted: false } })
            await expect(repeated.promise).resolves.toEqual({ accepted: false })
            expect(events.map((item) => item.type)).toEqual(["started", "progress", "cancelled"])
            expect(events.filter((item) => ["completed", "failed", "cancelled"].includes(item.type))).toHaveLength(1)
        } finally {
            bridge.dispose()
        }
    })
    it("超时或销毁清理监听与等待者", async () => {
        vi.useFakeTimers()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: vi.fn(),
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport, 50)
        const request = bridge.request("doctor", {})
        const rejection = expect(request.promise).rejects.toThrow("请求超时")
        await vi.advanceTimersByTimeAsync(51)
        await rejection
        expect(transport.postMessage).toHaveBeenCalledTimes(2)
        bridge.dispose()
        expect(transport.removeEventListener).toHaveBeenCalledOnce()
        await expect(bridge.request("doctor", {}).promise).rejects.toThrow("桥接已关闭")
        vi.useRealTimers()
    })
})

it.each(["scaffold.openDirectory"] as const)("受限打开 %s 启动后排空原请求, 不超时取消或重放", async (method) => {
    vi.useFakeTimers()
    const { bridge, postMessage, send } = requestHarness()
    const phase = vi.fn()
    try {
        const pending = bridge.request(method, {}, undefined, phase)
        const settled = pending.promise.catch((error: unknown) => error)
        send({ requestPhase: { requestId: pending.id, phase: "committing" } })
        send({ requestPhase: { requestId: pending.id, phase: "committing" } })
        await vi.advanceTimersByTimeAsync(30001)
        expect(phase).toHaveBeenCalledExactlyOnceWith("committing")
        expect(postMessage).toHaveBeenCalledTimes(1)
        send({ id: pending.id, result: { opened: true } })
        await expect(settled).resolves.toEqual({ opened: true })
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})

it.each(["udf.sourceSave", "udf.sourceDelete"] as const)("来源变更 %s 提交后等待权威结果, 不因普通超时发送取消", async (method) => {
    vi.useFakeTimers()
    let receive: (event: MessageEvent) => void = vi.fn()
    const transport = {
        postMessage: vi.fn(),
        addEventListener: (_: "message", cb: (event: MessageEvent) => void) => {
            receive = cb
        },
        removeEventListener: vi.fn(),
    }
    const bridge = new BridgeClient(transport, 100),
        onPhase = vi.fn()
    try {
        const pending = bridge.request(
            method,
            method === "udf.sourceSave"
                ? { name: "Team", baseUrl: "https://github.com/example/udfs" }
                : { id: "source:test", baseline: "baseline", confirm: true },
            undefined,
            onPhase,
        )
        const settled = pending.promise.catch((error: unknown) => error)
        receive(new MessageEvent("message", { data: { protocolVersion: 1, requestPhase: { requestId: pending.id, phase: "committing" } } }))
        await vi.advanceTimersByTimeAsync(1000)
        expect(onPhase).toHaveBeenCalledExactlyOnceWith("committing")
        expect(transport.postMessage).toHaveBeenCalledTimes(1)
        const result =
            method === "udf.sourceSave"
                ? { id: "source:test", name: "Team", baseUrl: "https://github.com/example/udfs", baseline: "saved", kind: "custom" }
                : { deleted: true }
        receive(new MessageEvent("message", { data: { protocolVersion: 1, id: pending.id, result } }))
        await expect(settled).resolves.toEqual(result)
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})

it.each(["configuration.apply", "configuration.complete", "configuration.discard", "udf.catalogRefresh"] as const)(
    "共享配置 %s 确认使用写入预算, 提交通知只发送一次且排空期间不超时取消",
    async (method) => {
        vi.useFakeTimers()
        let receive: (event: MessageEvent) => void = vi.fn()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: (_type: "message", listener: (event: MessageEvent) => void) => {
                receive = listener
            },
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport)
        try {
            const phase = vi.fn()
            const request =
                method === "udf.catalogRefresh"
                    ? bridge.request(method, {}, undefined, phase)
                    : method === "configuration.apply"
                      ? bridge.request(
                            method,
                            { targetId: "isolated-target", baselineId: "before", section: "measures", request: { measures: [] } },
                            undefined,
                            phase,
                        )
                      : bridge.request(method, { targetId: "isolated-target", baselineId: "before" }, undefined, phase)
            const settled = request.promise.catch((error: unknown) => error)
            await vi.advanceTimersByTimeAsync(30001)
            expect(transport.postMessage).toHaveBeenCalledTimes(1)
            receive(new MessageEvent("message", { data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "committing" } } }))
            receive(new MessageEvent("message", { data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "committing" } } }))
            expect(phase).toHaveBeenCalledExactlyOnceWith("committing")
            expect(vi.getTimerCount()).toBe(0)
            await vi.advanceTimersByTimeAsync(600000)
            expect(transport.postMessage).toHaveBeenCalledTimes(1)
            const result = { targetId: "isolated-target", project: { baselineId: "after", hasPendingChanges: false } }
            receive(new MessageEvent("message", { data: { protocolVersion: 1, id: request.id, result } }))
            await expect(settled).resolves.toEqual(result)
        } finally {
            bridge.dispose()
            vi.useRealTimers()
        }
    },
)

it.each(["a", "中", '"\\\n'])("配置多行%s累计超出完整UTF8信封时立即排空, 不发送或留下pending/timer", async (character) => {
    vi.useFakeTimers()
    const transport = { postMessage: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn() }
    const bridge = new BridgeClient(transport)
    let busy = true
    try {
        const measures = Array.from({ length: 3 }, (_, index) => ({
            name: `Isolated${index}`,
            value: character.repeat(30000),
            description: "",
            displayFolder: "",
            dataCategory: "Uncategorized",
            enabled: true,
        }))
        const request = bridge.request("configuration.apply", { targetId: "isolated", baselineId: "baseline", section: "measures", request: { measures } })
        const result = request.promise
            .finally(() => {
                busy = false
            })
            .catch((error: unknown) => error)
        const failure = await result
        expect(failure).toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        expect(busy).toBe(false)
        expect(transport.postMessage).not.toHaveBeenCalled()
        expect(Reflect.get(bridge, "pending").size).toBe(0)
        expect(vi.getTimerCount()).toBe(0)
        await vi.advanceTimersByTimeAsync(180001)
        expect(transport.postMessage).not.toHaveBeenCalled()
        setLocale("en-us")
        expect(hostText((failure as Error).message)).toMatch(/64 KiB.*Shorten/)
        expect(hostText("ISOLATED_UNKNOWN_CODE")).toBe("ISOLATED_UNKNOWN_CODE")
    } finally {
        bridge.dispose()
        vi.useRealTimers()
        setLocale("zh-cn")
    }
})

it("小配置和对话控制通道共用信封预算, 合法小包可继续发送且超限回复不发送值", async () => {
    vi.useFakeTimers()
    let receive: (event: MessageEvent) => void = vi.fn()
    const transport = {
        postMessage: vi.fn((value: unknown) => {
            const message = value as { id: string }
            receive(new MessageEvent("message", { data: { protocolVersion: 1, id: message.id, result: { accepted: true } } }))
        }),
        addEventListener: (_type: "message", listener: (event: MessageEvent) => void) => {
            receive = listener
        },
        removeEventListener: vi.fn(),
    }
    const bridge = new BridgeClient(transport)
    try {
        await expect(
            bridge.request("ui.dialogRespond", { dialogId: "isolated", action: "confirm", values: { value: "中".repeat(30000) } }).promise,
        ).rejects.toMatchObject({ code: "MESSAGE_TOO_LARGE" })
        expect(transport.postMessage).not.toHaveBeenCalled()
        await expect(bridge.request("configuration.context", { target: "current" }).promise).resolves.toEqual({ accepted: true })
        await expect(
            bridge.request("configuration.apply", { targetId: "isolated", baselineId: "before", section: "measures", request: { measures: [] } }).promise,
        ).resolves.toEqual({ accepted: true })
        await expect(bridge.request("ui.dialogRespond", { dialogId: "isolated", action: "cancel", values: {} }).promise).resolves.toEqual({ accepted: true })
        expect(transport.postMessage).toHaveBeenCalledTimes(3)
        expect(Reflect.get(bridge, "pending").size).toBe(0)
        expect(vi.getTimerCount()).toBe(0)
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})

it.each([100, 30000])("配置确认阶段超时%d仍有界, 只在提交前发送取消", async (timeoutMs) => {
    vi.useFakeTimers()
    const transport = { postMessage: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn() }
    const bridge = new BridgeClient(transport, timeoutMs)
    try {
        const request = bridge.request("configuration.apply", {
            targetId: "isolated",
            baselineId: "before",
            section: "measures",
            request: { measures: [] },
        })
        const result = request.promise.catch((error: unknown) => error)
        const budget = timeoutMs === 30000 ? 180000 : timeoutMs
        await vi.advanceTimersByTimeAsync(budget - 1)
        expect(transport.postMessage).toHaveBeenCalledTimes(1)
        await vi.advanceTimersByTimeAsync(2)
        await expect(result).resolves.toMatchObject({ code: "TIMEOUT" })
        expect(transport.postMessage).toHaveBeenLastCalledWith(expect.objectContaining({ method: "request.cancel", params: { requestId: request.id } }))
    } finally {
        bridge.dispose()
        vi.useRealTimers()
    }
})

it.each(["configuration.apply", "configuration.complete", "configuration.discard"] as const)(
    "共享配置 %s 保留显式测试超时且提交后解除原时限",
    async (method) => {
        vi.useFakeTimers()
        let receive: (event: MessageEvent) => void = vi.fn()
        const transport = {
            postMessage: vi.fn(),
            addEventListener: (_type: "message", listener: (event: MessageEvent) => void) => {
                receive = listener
            },
            removeEventListener: vi.fn(),
        }
        const bridge = new BridgeClient(transport, 100)
        try {
            const params =
                method === "configuration.apply"
                    ? { targetId: "isolated-target", baselineId: "before", section: "measures" as const, request: { measures: [] } }
                    : { targetId: "isolated-target", baselineId: "before" }
            const phase = vi.fn()
            const request = bridge.request(method, params, undefined, phase)
            const settled = request.promise.catch((error: unknown) => error)
            receive(new MessageEvent("message", { data: { protocolVersion: 1, requestPhase: { requestId: request.id, phase: "committing" } } }))
            await vi.advanceTimersByTimeAsync(1001)
            expect(phase).toHaveBeenCalledExactlyOnceWith("committing")
            expect(transport.postMessage).toHaveBeenCalledTimes(1)
            receive(new MessageEvent("message", { data: { protocolVersion: 1, id: request.id, error: { code: "SAVE_FAILED", message: "isolated failure" } } }))
            await expect(settled).resolves.toMatchObject({ code: "SAVE_FAILED", message: "isolated failure" })
        } finally {
            bridge.dispose()
            vi.useRealTimers()
        }
    },
)
