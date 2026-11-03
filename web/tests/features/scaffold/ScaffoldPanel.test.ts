/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\ScaffoldPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用真实品牌控件验证步骤导航, 一次创建和取消排空边界.
 */
import { beforeEach, expect, it, vi } from "vitest"
import { mount, flushPromises, DOMWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElSelect, ElCheckbox } from "element-plus"
import UdfLibraryCard from "@/features/scaffold/UdfLibraryCard.vue"
import UdfTagDialog from "@/features/scaffold/UdfTagDialog.vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { hostText, setLocale } from "@/i18n"
import * as messageUi from "@/ui/message"
import ScaffoldPanel from "@/features/scaffold/ScaffoldPanel.vue"
import NewPanel from "@/app/NewPanel.vue"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import { previewProject } from "@/bridge/preview"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import type { ConfigurationContext, ProjectSnapshot } from "@/bridge/client"
import type { Transport } from "@/bridge/client"

// 真实 Element Plus 五步配置会保留已访问控件, 全套并行运行需给受控门闩和完整 DOM 渲染留出有限时间.
vi.setConfig({ testTimeout: 15000 })
const sentMessages = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
beforeEach(() => sentMessages.mockClear())
/** 读取实际发送给浮动消息的原文, 保留发送时语言和类型, 不把切语言当成重发. */
function notices(type?: "warning" | "error" | "success" | "info"): string[] {
    return sentMessages.mock.calls.filter((call) => !type || call[1] === type).map(([message]) => message)
}

type Request = { id: string; method: string; params: Record<string, unknown> }
/** 保留模拟正常分发, 对需验证取消和提交的单个请求设置受控门闩. */
class ControlledTransport implements Transport {
    mock = new MockTransport()
    requests: Request[] = []
    held?: Request
    hold = ""
    holdCancel = false
    cancelHeld?: Request
    listeners = new Set<(event: MessageEvent) => void>()
    responses = new Map<string, unknown>()
    /** 同时订阅真实模拟响应和受控响应. */
    addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.mock.addEventListener(type, listener)
        this.listeners.add(listener)
    }
    /** 释放两条受控传输通道. */
    removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.mock.removeEventListener(type, listener)
        this.listeners.delete(listener)
    }
    /** 暂停目标请求, 取消接受本身不完成原请求. */
    postMessage(value: unknown): void {
        const request = value as Request
        this.requests.push(request)
        if (this.responses.has(request.method)) {
            this.send({ id: request.id, result: this.responses.get(request.method) })
            return
        }
        if (request.method === this.hold) {
            this.held = request
            return
        }
        if (request.method === "request.cancel") {
            if (this.holdCancel) {
                this.cancelHeld = request
                return
            }
            this.send({ id: request.id, result: { accepted: true } })
            return
        }
        // 模拟传输为单参数宿主协议, 不是浏览器 window.postMessage.
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        this.mock.postMessage(value)
    }
    /** 发送协议终态或提交边界事件, 不自动提升业务事实. */
    send(value: object): void {
        for (const listener of this.listeners) listener(new MessageEvent("message", { data: { protocolVersion: 1, ...value } }))
    }
}

/** 查找固定文案动作按钮, 缺失时抛出便于定位. */
function button(wrapper: ReturnType<typeof mount>, label: string) {
    return wrapper.findAll("button").find((item) => item.text() === label)!
}

it("删除全部模板度量后页面往返不恢复默认项, 宿主记忆的空集也保持为空", async () => {
    setLocale("zh-cn")
    const transport = new ControlledTransport()
    const catalog = new ScaffoldMock().handle("scaffold.templates", {}) as Record<string, unknown>
    transport.responses.set("scaffold.templates", { ...catalog, defaultMeasures: [] })
    const bridge = markRaw(new BridgeClient(transport))
    let wrapper = mount(ScaffoldPanel, { props: { bridge } })
    try {
        await flushPromises()
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        await wrapper.get("#section-scaffold-measures [data-optional] input").setValue(true)
        expect(wrapper.findAll(".scaffold-measures tbody tr")).toHaveLength(0)
        wrapper.unmount()
        transport.responses.set("scaffold.templates", catalog)
        wrapper = mount(ScaffoldPanel, { props: { bridge } })
        await flushPromises()
        expect(wrapper.findAll(".scaffold-measures tbody tr")).toHaveLength(0)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

/** 逐步查看全部可选配置, 保留默认跳过后到达最终创建步骤. */
function visitAll(wrapper: ReturnType<typeof mount>): Promise<void> {
    return ["template", "sample", "measures", "calendar", "udfs"].reduce(
        (previous, id) =>
            previous.then(async () => {
                await wrapper.get(`[data-step="scaffold-${id}"]`).trigger("click")
                await flushPromises()
            }),
        Promise.resolve(),
    )
}

it.each([
    [
        "zh-cn",
        "选择目录",
        "创建项目",
        "打开项目目录",
        "Power BI Desktop 打开",
        "创建新项目",
        "项目创建后的模板度量值、日期与时间智能和 UDF, 请打开模型后在模型模块中管理。",
    ],
    [
        "en-us",
        "Choose directory",
        "Create project",
        "Open project directory",
        "Open in Power BI Desktop",
        "Create new project",
        "After creating the project, open its model and manage template measures, date and time intelligence, and UDFs in the Model module.",
    ],
] as const)("%s 创建后显示模型管理说明和固定打开操作, 不读取创建配置或重复创建", async (locale, choose, create, directory, desktop, another, guidance) => {
    setLocale(locale)
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, choose).trigger("click")
        await flushPromises()
        await button(wrapper, create).trigger("click")
        await flushPromises()
        expect(wrapper.get(".scaffold-actions").find("button").text()).toBe(directory)
        expect(button(wrapper, choose)).toBeUndefined()
        expect(button(wrapper, desktop).classes()).toContain("el-button--primary")
        expect(button(wrapper, another)).toBeDefined()
        expect(wrapper.text()).toContain(guidance)
        expect(wrapper.find(".el-steps").exists()).toBe(false)
        expect(wrapper.find(".scaffold-form").exists()).toBe(false)
        expect(wrapper.find('[id^="section-scaffold-"]').exists()).toBe(false)
        expect(transport.requests.some((item) => item.method.startsWith("configuration."))).toBe(false)
        await button(wrapper, directory).trigger("click")
        await flushPromises()
        await button(wrapper, desktop).trigger("click")
        await flushPromises()
        expect(wrapper.emitted("connected")).toEqual([[expect.objectContaining({ connection: expect.objectContaining({ kind: "desktop" }) })]])
        expect(await bridge.request("project.inspect", {}).promise).toEqual(wrapper.emitted("connected")![0]![0])
        expect(transport.requests.filter((item) => item.method === "scaffold.openDirectory").map((item) => item.params)).toEqual([{}])
        expect(transport.requests.filter((item) => item.method === "scaffold.openDesktop").map((item) => item.params)).toEqual([{}])
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
        expect(transport.requests.some((item) => item.method === "project.open")).toBe(false)
        await button(wrapper, another).trigger("click")
        await flushPromises()
        expect(button(wrapper, choose)).toBeDefined()
        expect(button(wrapper, desktop)).toBeUndefined()
        expect(button(wrapper, create).attributes("disabled")).toBeDefined()
    } finally {
        wrapper.unmount()
        bridge.dispose()
        setLocale("zh-cn")
    }
})

it.each([
    ["scaffold.openDirectory", "打开项目目录"],
    ["scaffold.openDesktop", "Power BI Desktop 打开"],
] as const)("%s 在途保持互斥, 打开失败保留已创建目标和重试入口", async (method, label) => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        transport.hold = method
        await button(wrapper, label).trigger("click")
        await flushPromises()
        transport.send({ requestPhase: { requestId: transport.held!.id, phase: "committing" } })
        await flushPromises()
        expect(document.querySelector(".operation-messages")?.textContent).toContain(
            method === "scaffold.openDesktop" ? "正在通过 Power BI Desktop" : "正在处理",
        )
        expect(button(wrapper, "打开项目目录").attributes("disabled")).toBeDefined()
        expect(button(wrapper, "创建新项目").attributes("disabled")).toBeDefined()
        await button(wrapper, label).trigger("click")
        expect(transport.requests.filter((item) => item.method === method)).toHaveLength(1)
        transport.send({ id: transport.held!.id, error: { code: "OPEN_FAILED", message: "Open failed" } })
        await flushPromises()
        expect(notices().join("\n")).toContain("Open failed")
        expect(wrapper.emitted("connected")).toBeUndefined()
        expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
        expect(button(wrapper, label).attributes("disabled")).toBeUndefined()
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("当前模型待保存时只阻止 Desktop 连接, 目录和已创建工程仍保留", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge), canConnect: false } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(button(wrapper, "打开项目目录").attributes("disabled")).toBeUndefined()
        expect(button(wrapper, "Power BI Desktop 打开").attributes("disabled")).toBeDefined()
        await button(wrapper, "Power BI Desktop 打开").trigger("click")
        expect(transport.requests.some((item) => item.method === "scaffold.openDesktop")).toBe(false)
        await wrapper.setProps({ canConnect: true })
        expect(button(wrapper, "Power BI Desktop 打开").attributes("disabled")).toBeUndefined()
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it.each(["cancelled", "disposed"] as const)("Desktop 连接 %s 保留创建目标且不发布新连接", async (outcome) => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    let disposed = false
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        transport.hold = "scaffold.openDesktop"
        await button(wrapper, "Power BI Desktop 打开").trigger("click")
        await flushPromises()
        const pending = transport.held!
        transport.send({ requestPhase: { requestId: pending.id, phase: "connecting" } })
        await flushPromises()
        expect(document.querySelector(".operation-messages")?.textContent).toContain("正在通过 Power BI Desktop 打开文件并连接模型")
        if (outcome === "cancelled") {
            const cancel = [...document.querySelectorAll<HTMLButtonElement>(".operation-messages button")].find((item) => item.textContent === "取消")!
            expect(cancel.disabled).toBe(false)
            cancel.click()
            await flushPromises()
            expect(button(wrapper, "创建新项目").attributes("disabled")).toBeDefined()
            transport.send({ id: pending.id, error: { code: "CANCELLED", message: "cancelled" } })
            await flushPromises()
            expect(button(wrapper, "Power BI Desktop 打开").attributes("disabled")).toBeUndefined()
            expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
        } else {
            wrapper.unmount()
            disposed = true
            transport.send({ id: pending.id, result: { opened: true, project: previewProject } })
            await flushPromises()
        }
        expect(wrapper.emitted("connected")).toBeUndefined()
        expect(transport.requests.filter((item) => item.method === "request.cancel").map((item) => item.params)).toContainEqual({ requestId: pending.id })
    } finally {
        if (!disposed) wrapper.unmount()
        bridge.dispose()
    }
})

it.each([false, true])("未核验创建结果 projectCreated=%s 不授予打开入口", async (projectCreated) => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        transport.hold = "scaffold.create"
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        transport.send({
            id: transport.held!.id,
            result: {
                planId: "partial",
                state: "partial",
                projectCreated,
                verified: false,
                gitInitialized: false,
                projectPath: "C:/Mock/Partial.pbip",
                tables: [],
                warnings: [],
            },
        })
        await flushPromises()
        if (projectCreated) {
            const actions = ["打开项目目录", "Power BI Desktop 打开"].map((label) => button(wrapper, label))
            for (const action of actions) expect(action.attributes("disabled")).toBeDefined()
            await Promise.all(actions.map((action) => action.trigger("click")))
        } else {
            expect(button(wrapper, "选择目录")).toBeDefined()
            expect(button(wrapper, "创建项目")).toBeDefined()
            expect(button(wrapper, "Power BI Desktop 打开")).toBeUndefined()
        }
        expect(transport.requests.some((item) => item.method === "scaffold.openDirectory" || item.method === "scaffold.openDesktop")).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("创建成功和页面重挂只保留完成事实, 不依赖已创建配置回读", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    let wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(button(wrapper, "打开项目目录").attributes("disabled")).toBeUndefined()
        expect(button(wrapper, "Power BI Desktop 打开").attributes("disabled")).toBeUndefined()
        expect(button(wrapper, "重新核验工程")).toBeUndefined()
        expect(wrapper.find(".configuration-recovery").exists()).toBe(false)
        expect(wrapper.find(".scaffold-form").exists()).toBe(false)
        wrapper.unmount()
        wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
        await flushPromises()
        expect(button(wrapper, "Power BI Desktop 打开").attributes("disabled")).toBeUndefined()
        expect(notices("info")).toContain("浏览器模拟, 未下载或写入真实文件")
        expect(button(wrapper, "打开项目目录").attributes("disabled")).toBeUndefined()
        expect(wrapper.find(".el-steps").exists()).toBe(false)
        expect(wrapper.find(".scaffold-form").exists()).toBe(false)
        expect(button(wrapper, "重新核验工程")).toBeUndefined()
        expect(transport.requests.some((item) => item.method.startsWith("configuration."))).toBe(false)
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("从模板度量值步骤创建后结束编辑, 只能打开工程或显式开始新项目", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        await wrapper.get("#section-scaffold-measures [data-optional] input").setValue(true)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("RetainedDraft")
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(wrapper.find(".scaffold-result").exists()).toBe(false)
        expect(wrapper.get(".scaffold-created-target").text()).toContain("MyPowerBIProject.pbip")
        expect(wrapper.find(".el-steps").exists()).toBe(false)
        expect(wrapper.find(".scaffold-form").exists()).toBe(false)
        expect(button(wrapper, "写入模板度量值")).toBeUndefined()
        expect(button(wrapper, "继续保存")).toBeUndefined()
        expect(button(wrapper, "放弃修改")).toBeUndefined()
        expect(button(wrapper, "创建新项目")).toBeDefined()
        expect(transport.requests.some((request) => request.method.startsWith("configuration."))).toBe(false)
        expect(transport.requests.filter((request) => request.method === "scaffold.create")).toHaveLength(1)
        await button(wrapper, "创建新项目").trigger("click")
        await flushPromises()
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        expect((wrapper.get(".scaffold-measures tbody tr input").element as HTMLInputElement).value).toBe("RetainedDraft")
        expect((wrapper.get("#section-scaffold-measures [data-optional] input").element as HTMLInputElement).checked).toBe(true)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("模型模板入口使用与新建相同的六字段配置, 草稿在模型页签往返后保留", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(ModelPanel, { props: { bridge, project: structuredClone(previewProject), initialTab: "templates" } })
    try {
        await flushPromises()
        expect(wrapper.find(".scaffold-measures").exists()).toBe(true)
        expect(wrapper.find('section[id$="-measures"] [data-optional]').exists()).toBe(false)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("ModelDraft")
        await wrapper.get("#tab-tables").trigger("click")
        await wrapper.get("#tab-templates").trigger("click")
        expect((wrapper.get(".scaffold-measures tbody tr input").element as HTMLInputElement).value).toBe("ModelDraft")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("可选步骤初始跳过, 模板六字段平铺且可编辑, 切语言保留值", async () => {
    setLocale("zh-cn")
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    const switches = wrapper.findAll('[data-optional] input[role="switch"]')
    expect(switches).toHaveLength(3)
    expect(switches.every((node) => !(node.element as HTMLInputElement).checked && node.attributes("aria-label") === "启用此步骤")).toBe(true)
    await wrapper.get('[data-test="project-name"] input').setValue("KeepMe")
    await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
    expect(wrapper.findAll(".scaffold-measures tbody tr")).toHaveLength(6)
    setLocale("en-us")
    await wrapper.get('[data-step="scaffold-template"]').trigger("click")
    expect((wrapper.get('[data-test="project-name"] input').element as HTMLInputElement).value).toBe("KeepMe")
    wrapper.unmount()
    bridge.dispose()
    setLocale("zh-cn")
})
it("选择目录后直接创建且没有前后按钮, 往返保留编辑与同目标防重放", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await button(wrapper, "选择目录").trigger("click")
    await flushPromises()
    expect(button(wrapper, "创建项目").attributes("disabled")).toBeUndefined()
    expect(button(wrapper, "上一步")).toBeUndefined()
    expect(button(wrapper, "下一步")).toBeUndefined()
    expect(wrapper.findAll(".ui-step-button").map((item) => item.attributes("data-step"))).toEqual([
        "scaffold-template",
        "scaffold-sample",
        "scaffold-measures",
        "scaffold-calendar",
        "scaffold-udfs",
    ])
    expect(wrapper.find(".el-steps").exists()).toBe(true)
    expect(wrapper.find(".scaffold-preview").exists()).toBe(false)
    expect(wrapper.findAll("button").some((item) => item.text() === "预览项目")).toBe(false)
    await button(wrapper, "创建项目").trigger("click")
    await flushPromises()
    expect(wrapper.find(".scaffold-result").exists()).toBe(false)
    expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
    const methods = transport.requests.map((item) => item.method)
    expect(methods.indexOf("scaffold.preview")).toBeGreaterThan(methods.lastIndexOf("scaffold.updateDraft"))
    expect(methods.indexOf("scaffold.create")).toBeGreaterThan(methods.indexOf("scaffold.preview"))
    expect(button(wrapper, "创建项目")).toBeUndefined()
    expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
    await button(wrapper, "创建新项目").trigger("click")
    await flushPromises()
    await wrapper.get('[data-test="project-name"] input').setValue("AnotherProject")
    await wrapper.get('[data-step="scaffold-udfs"]').trigger("click")
    expect(button(wrapper, "创建项目").attributes("disabled")).toBeUndefined()
    await wrapper.get('[data-step="scaffold-template"]').trigger("click")
    await wrapper.get('[data-test="project-name"] input').setValue("MYPOWERBIPROJECT")
    await wrapper.get('[data-step="scaffold-udfs"]').trigger("click")
    expect(button(wrapper, "创建项目").attributes("disabled")).toBeDefined()
    wrapper.unmount()
    bridge.dispose()
})

it.each(["sample", "measures", "calendar", "udfs"])("不要求访问其余步骤, 可以从%s直接创建", async (id) => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await wrapper.get(`[data-step="scaffold-${id}"]`).trigger("click")
        await flushPromises()
        expect(button(wrapper, "创建项目").attributes("disabled")).toBeUndefined()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
        expect(wrapper.find(".scaffold-result").exists()).toBe(false)
        expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("关闭的可选步骤保留坏草稿且不校验或提交, 重新开启后定位对应行和字段", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { attachTo: document.body, props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        const enabled = wrapper.get("#section-scaffold-measures [data-optional] input")
        await enabled.setValue(true)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("")
        await enabled.setValue(false)
        await wrapper.get('[data-step="scaffold-template"]').trigger("click")
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(transport.requests.find((item) => item.method === "scaffold.updateDraft" && item.params.section === "measures")?.params.value).toBeNull()
        expect(wrapper.find(".scaffold-result").exists()).toBe(false)
        expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
        await button(wrapper, "创建新项目").trigger("click")
        await flushPromises()
        await wrapper.get('[data-test="project-name"] input').setValue("NextTarget")
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        await wrapper.get("#section-scaffold-measures [data-optional] input").setValue(true)
        await wrapper.get('[data-step="scaffold-template"]').trigger("click")
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        const field = wrapper.get('[data-scaffold-field="name"][data-scaffold-row="1"]')
        expect(wrapper.get('[data-step="scaffold-measures"]').attributes("aria-current")).toBe("step")
        expect(field.get(".scaffold-field__error").text()).toContain("名称")
        expect(field.get("input").attributes("aria-invalid")).toBe("true")
        expect(document.activeElement).toBe(field.get("input").element)
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
        await field.get("input").setValue("Fixed")
        expect(wrapper.find(".scaffold-field__error").exists()).toBe(false)
        expect(field.get("input").attributes("aria-invalid")).toBeUndefined()
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("远端精确预发行无需额外授权开关即可选择并创建", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { attachTo: document.body, props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await wrapper.get('[data-step="scaffold-udfs"]').trigger("click")
        // 首次进入先排空本地来源和状态读取, 再按真实可用控件启用可选步骤.
        await flushPromises()
        await wrapper.get("#section-scaffold-udfs [data-optional] input").setValue(true)
        await flushPromises()
        const udf = wrapper.getComponent(UdfStep)
        // 新来源必须显式建立完整缓存, 平铺卡片替代旧函数库下拉入口.
        await button(wrapper, "更新来源").trigger("click")
        await flushPromises()
        await udf.findComponent(UdfLibraryCard).find(".udf-package-version button").trigger("click")
        await flushPromises()
        const dialog = udf.findComponent(UdfTagDialog)
        await dialog.findComponent(ElSelect).setValue("1.1.0-beta")
        await new DOMWrapper(
            [...document.querySelectorAll<HTMLButtonElement>(".udf-tag-dialog button")].find((item) => item.textContent?.trim() === "确认版本")!,
        ).trigger("click")
        await flushPromises()
        expect(udf.findAllComponents(ElCheckbox)[0]!.props("modelValue")).toBe(true)
        await udf.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
        await flushPromises()
        expect(udf.props("modelValue")[0]).toMatchObject({ version: "1.1.0-beta" })
        expect(document.querySelector(".udf-tag-dialog .el-checkbox__original")).toBeNull()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})
it("内部准备取消接受后保持步骤与导航锁, 原请求排空后不继续创建", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await button(wrapper, "选择目录").trigger("click")
    await flushPromises()
    transport.hold = "scaffold.preview"
    await visitAll(wrapper)
    await button(wrapper, "创建项目").trigger("click")
    await flushPromises()
    const cancel = document.querySelector<HTMLButtonElement>(".operation-message .operation-feedback__cancel")!
    await new DOMWrapper(cancel).trigger("click")
    await flushPromises()
    expect(wrapper.attributes("aria-busy")).toBe("true")
    expect(wrapper.get('[data-step="scaffold-udfs"]').attributes("aria-disabled")).toBe("true")
    expect(transport.requests.filter((item) => item.method === "request.cancel")).toHaveLength(1)
    transport.send({ id: transport.held!.id, error: { code: "CANCELLED", message: "cancelled" } })
    await flushPromises()
    expect(wrapper.attributes("aria-busy")).toBe("false")
    expect(wrapper.find(".scaffold-preview").exists()).toBe(false)
    expect(transport.requests.some((item) => item.method === "scaffold.create")).toBe(false)
    wrapper.unmount()
    bridge.dispose()
})
it("提交阶段禁止取消, 已发布但核验未完保留结果并禁止重放创建", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    await flushPromises()
    await button(wrapper, "选择目录").trigger("click")
    await flushPromises()
    transport.hold = "scaffold.create"
    await visitAll(wrapper)
    await button(wrapper, "创建项目").trigger("click")
    await flushPromises()
    transport.send({ requestPhase: { requestId: transport.held!.id, phase: "committing" } })
    await flushPromises()
    expect(document.querySelector<HTMLButtonElement>(".operation-message .operation-feedback__cancel")!.disabled).toBe(true)
    transport.send({
        id: transport.held!.id,
        result: {
            planId: "controlled",
            state: "published",
            projectCreated: true,
            verified: false,
            gitInitialized: false,
            projectPath: "C:/Mock/published",
            baselineId: null,
            tables: [],
            warnings: ["verification pending"],
            errorCode: "VERIFY_FAILED",
            isSimulation: true,
        },
    })
    await flushPromises()
    expect(wrapper.get(".scaffold-result").text()).toContain("后处理尚未全部完成")
    expect(wrapper.get(".scaffold-result").text()).toContain("C:/Mock/published")
    expect(button(wrapper, "创建项目")).toBeUndefined()
    expect(button(wrapper, "重新核验工程")).toBeUndefined()
    expect(wrapper.find(".scaffold-form").exists()).toBe(false)
    expect(wrapper.find(".el-steps").exists()).toBe(false)
    expect(button(wrapper, "打开项目目录").attributes("disabled")).toBeDefined()
    expect(button(wrapper, "Power BI Desktop 打开").attributes("disabled")).toBeDefined()
    expect(transport.requests.some((item) => item.method.startsWith("configuration."))).toBe(false)
    expect(wrapper.get(".scaffold-result").text()).toContain("后处理尚未全部完成")
    wrapper.unmount()
    bridge.dispose()
})
it("本地管理和长值编辑模态锁定外层导航, 关闭后释放且不丢草稿", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        await wrapper.get('#section-scaffold-measures [data-optional] input[role="switch"]').setValue(true)
        await button(wrapper, "编辑").trigger("click")
        await flushPromises()
        expect(wrapper.attributes("aria-busy")).toBe("true")
        expect(wrapper.get('[data-step="scaffold-template"]').attributes("aria-disabled")).toBe("true")
        const cancel = [...document.querySelectorAll<HTMLButtonElement>(".el-dialog button")].find((item) => item.textContent?.trim() === "取消")!
        expect(cancel.disabled).toBe(false)
        await new DOMWrapper(cancel).trigger("click")
        await flushPromises()
        expect(wrapper.attributes("aria-busy")).toBe("false")
        await wrapper.get('[data-step="scaffold-udfs"]').trigger("click")
        // 只读初始化期间外层开关和管理按钮真实禁用, 待其排空后模拟用户操作.
        await flushPromises()
        const udfEnabled = wrapper.get('#section-scaffold-udfs [data-optional] input[role="switch"]')
        expect(udfEnabled.attributes("disabled")).toBeUndefined()
        await udfEnabled.setValue(true)
        await flushPromises()
        expect(button(wrapper, "管理本地函数").attributes("disabled")).toBeUndefined()
        await button(wrapper, "管理本地函数").trigger("click")
        await flushPromises()
        expect(wrapper.attributes("aria-busy")).toBe("true")
        const newLocal = [...document.querySelectorAll<HTMLButtonElement>(".el-dialog button")].find((item) => item.textContent?.trim() === "新建本地函数")!
        expect(newLocal.disabled).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        await flushPromises()
    }
})
it("新建步骤ID唯一, 无外层页签时键盘仍可直达任意步骤", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(NewPanel, { attachTo: document.body, props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        const ids = wrapper.findAll("[id]").map((element) => element.attributes("id"))
        expect(new Set(ids).size).toBe(ids.length)
        expect(wrapper.findAll("#tab-sample")).toHaveLength(0)
        expect(wrapper.findAll('[data-step="scaffold-sample"]')).toHaveLength(1)
        const inner = wrapper.get('[data-step="scaffold-template"]')
        await inner.trigger("click")
        await inner.trigger("keydown", { key: "ArrowRight", code: "ArrowRight" })
        await flushPromises()
        expect(wrapper.get('[data-step="scaffold-sample"]').attributes("aria-current")).toBe("step")
        expect(wrapper.find("#tab-new").exists()).toBe(false)
        expect(wrapper.get("#section-scaffold-sample").isVisible()).toBe(true)
        expect(wrapper.get('[data-step="scaffold-sample"]').attributes("aria-controls")).toBe("section-scaffold-sample")
        expect(wrapper.find(".sample-panel").exists()).toBe(false)
        expect(wrapper.find(".scaffold-panel").exists()).toBe(true)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("内部准备失败不发送创建且恢复可重试, 双击只执行一条准备链", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        transport.hold = "scaffold.preview"
        await visitAll(wrapper)
        await button(wrapper, "创建项目").trigger("click")
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(transport.requests.filter((item) => item.method === "scaffold.preview")).toHaveLength(1)
        const current = wrapper.get('[data-step="scaffold-udfs"]')
        await wrapper.get('[data-step="scaffold-calendar"]').trigger("click")
        await current.trigger("keydown", { key: "End" })
        expect(current.attributes("aria-current")).toBe("step")
        transport.send({ id: transport.held!.id, error: { code: "TEMPLATE_CHANGED", message: "template changed" } })
        await flushPromises()
        expect(wrapper.attributes("aria-busy")).toBe("false")
        expect(notices().join("\n")).toContain("template changed")
        expect(transport.requests.some((item) => item.method === "scaffold.create")).toBe(false)
        expect(button(wrapper, "创建项目").attributes("disabled")).toBeUndefined()
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("旧取消请求迟到失败不污染下一条创建操作", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        transport.hold = "scaffold.preview"
        transport.holdCancel = true
        await visitAll(wrapper)
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        const first = transport.held!
        await new DOMWrapper(document.querySelector<HTMLButtonElement>(".operation-feedback__cancel")!).trigger("click")
        await flushPromises()
        transport.send({ id: first.id, error: { code: "CANCELLED", message: "cancelled" } })
        await flushPromises()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        expect(transport.held!.id).not.toBe(first.id)
        transport.send({ id: transport.cancelHeld!.id, error: { code: "CANCEL_FAILED", message: "stale cancel error" } })
        await flushPromises()
        expect(notices().join("\n")).not.toContain("stale cancel error")
        expect(wrapper.attributes("aria-busy")).toBe("true")
        transport.send({ id: transport.held!.id, error: { code: "CANCELLED", message: "cancelled" } })
        await flushPromises()
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

/** 为目标隔离用例生成纯模拟宿主上下文, 展示路径不作为请求授权. */
function configurationContext(project: ProjectSnapshot, targetId: string): ConfigurationContext {
    return {
        targetId,
        project,
        details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
        canWrite: true,
        canGenerateSample: false,
        isCreatedTarget: false,
        isSimulation: true,
    }
}

it("Git 在创建前消失时先取消初始化, 草稿与冻结只保留重新检测后的意图", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const catalog = new ScaffoldMock().handle("scaffold.templates", {}) as Record<string, unknown>
    transport.responses.set("scaffold.templates", { ...catalog, git: { available: true } })
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        const git = wrapper.get('.scaffold-actions input[type="checkbox"]')
        await git.setValue(true)
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        transport.responses.set("scaffold.templates", { ...catalog, git: { available: false, reason: "GIT_NOT_AVAILABLE" } })
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        const base = transport.requests.find((item) => item.method === "scaffold.updateDraft" && item.params.section === "base")!
        expect(base.params.value).toMatchObject({ gitInitialize: false })
        const methods = transport.requests.map((item) => item.method)
        expect(methods.lastIndexOf("scaffold.templates")).toBeLessThan(methods.indexOf("scaffold.updateDraft"))
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("缺少 Git 时取消选择并禁用, 目录和创建仍可使用", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const catalog = new ScaffoldMock().handle("scaffold.templates", {}) as Record<string, unknown>
    transport.responses.set("scaffold.templates", { ...catalog, git: { available: false, reason: "GIT_NOT_AVAILABLE" } })
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        const git = wrapper.get('.scaffold-actions input[type="checkbox"]')
        expect(git.attributes("disabled")).toBeDefined()
        expect((git.element as HTMLInputElement).checked).toBe(false)
        expect(wrapper.get(".scaffold-git-status").text()).toContain("仍可创建")
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        expect(button(wrapper, "创建项目").attributes("disabled")).toBeUndefined()
        expect(wrapper.get(".scaffold-actions").find("button").text()).toBe("选择目录")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("新建页卸载再返回保留已创建目标与草稿, 显式新建另一个后不恢复旧目标模式", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    let wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        await wrapper.get("#section-scaffold-measures [data-optional] input").setValue(true)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("AcrossPages")
        await wrapper.get("#section-scaffold-measures [data-optional] input").setValue(false)
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        wrapper.unmount()
        wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
        await flushPromises()
        expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
        expect(button(wrapper, "创建项目")).toBeUndefined()
        expect(wrapper.find(".scaffold-form").exists()).toBe(false)
        expect(transport.requests.some((item) => item.method.startsWith("configuration."))).toBe(false)
        await button(wrapper, "创建新项目").trigger("click")
        await flushPromises()
        wrapper.unmount()
        wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
        await flushPromises()
        expect(wrapper.find(".scaffold-created-target").exists()).toBe(false)
        expect(button(wrapper, "创建项目")).toBeDefined()
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        expect((wrapper.get(".scaffold-measures tbody tr input").element as HTMLInputElement).value).toBe("AcrossPages")
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("当前模型按宿主目标隔离草稿, 同目标刷新和页面重挂保留输入并使用最新基线", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const first = { ...structuredClone(previewProject), projectId: "first", baselineId: "first-baseline" }
    const second = { ...structuredClone(previewProject), projectId: "second", baselineId: "second-baseline" }
    transport.responses.set("configuration.context", configurationContext(first, "host-first"))
    let wrapper = mount(ConfigurationPanel, { props: { bridge: markRaw(bridge), project: first, section: "measures", active: true } })
    try {
        await flushPromises()
        expect(wrapper.find('section[id$="-measures"] [data-optional]').exists()).toBe(false)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("FirstDraft")
        transport.responses.set("configuration.context", configurationContext(second, "host-second"))
        await wrapper.setProps({ project: second })
        await flushPromises()
        expect((wrapper.get(".scaffold-measures tbody tr input").element as HTMLInputElement).value).not.toBe("FirstDraft")
        expect(wrapper.find('section[id$="-measures"] [data-optional]').exists()).toBe(false)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("SecondDraft")
        wrapper.unmount()
        const refreshed = { ...first, baselineId: "first-refreshed" }
        transport.responses.set("configuration.context", configurationContext(refreshed, "host-first"))
        wrapper = mount(ConfigurationPanel, { props: { bridge: markRaw(bridge), project: refreshed, section: "measures", active: true } })
        await flushPromises()
        expect((wrapper.get(".scaffold-measures tbody tr input").element as HTMLInputElement).value).toBe("FirstDraft")
        expect(wrapper.get('section[id$="-sample"] [data-optional] input').attributes("disabled")).toBeDefined()
        transport.responses.set("configuration.apply", configurationContext({ ...refreshed, hasPendingChanges: true }, "host-first"))
        await button(wrapper, "写入模板度量值").trigger("click")
        await flushPromises()
        const applied = transport.requests.filter((item) => item.method === "configuration.apply")
        expect(applied).toHaveLength(1)
        expect(applied[0]!.params).toMatchObject({ targetId: "host-first", baselineId: "first-refreshed", section: "measures" })
        expect(button(wrapper, "写入模板度量值").attributes("disabled")).toBeDefined()
        expect(notices().join("\n")).toContain("修改尚未提交")
        expect(notices().join("\n")).not.toContain("模型已提交")
        expect((wrapper.get(".scaffold-measures tbody tr input").element as HTMLInputElement).value).toBe("FirstDraft")
        expect(transport.requests.some((item) => item.method === "scaffold.create")).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

/** 用当前模型配置呈现器验证权威保存反馈, 不改组件内部状态. */
async function feedbackHarness() {
    setLocale("zh-cn")
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const project = { ...structuredClone(previewProject), projectPath: "C:/Mock/Feedback/Feedback.pbip" }
    const context = configurationContext(project, "current-feedback-target")
    transport.responses.set("configuration.context", context)
    const wrapper = mount(ConfigurationPanel, { props: { bridge: markRaw(bridge), project, section: "measures", active: true } })
    await flushPromises()
    expect(wrapper.find('section[id$="-measures"] [data-optional]').exists()).toBe(false)
    return { transport, bridge, project, context, wrapper }
}

it("当前模型配置 partial 权威返回只保留恢复提示, 切换语言和未知提交锁不改变", async () => {
    const { transport, bridge, project, context, wrapper } = await feedbackHarness()
    const message = "上次保存未完成, 请先完成保存或核验"
    const pending = {
        ...context,
        project: {
            ...project,
            hasPendingChanges: true,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, message, errorCode: "SAVE_FAILED" },
        },
    }
    try {
        transport.responses.set("configuration.apply", pending)
        await button(wrapper, "写入模板度量值").trigger("click")
        await flushPromises()
        expect(notices("error")).toEqual([])
        expect(notices("warning")).toEqual([message])
        expect(wrapper.find(".el-alert").exists()).toBe(false)
        const feedbackCount = sentMessages.mock.calls.length
        expect(button(wrapper, "写入模板度量值").attributes("disabled")).toBeDefined()
        for (const locale of ["en-us", "zh-cn"] as const) {
            setLocale(locale)
            // oxlint-disable-next-line no-await-in-loop -- 同一实例依次切换语言并验证回切, 不并发改全局语言.
            await flushPromises()
            expect(notices("error")).toEqual([])
            expect(sentMessages.mock.calls).toHaveLength(feedbackCount)
        }
        transport.responses.set("configuration.complete", {
            ...pending,
            project: { ...pending.project, saveResult: { ...pending.project.saveResult, errorCode: "EXECUTION_NEEDS_VERIFICATION" } },
        })
        await button(wrapper, "继续保存").trigger("click")
        await flushPromises()
        expect(notices("error")).toEqual([])
        expect(notices("warning")).toEqual([message])
        expect(sentMessages.mock.calls).toHaveLength(feedbackCount)
        expect(button(wrapper, "继续保存").attributes("disabled")).toBeDefined()
        expect(button(wrapper, "放弃修改").attributes("disabled")).toBeDefined()
        expect(transport.requests.filter((item) => item.method === "configuration.apply")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
        setLocale("zh-cn")
    }
})

it.each((["configuration.apply", "configuration.complete"] as const).flatMap((method) => [true, false].map((same) => ({ method, same }))))(
    "当前模型配置 $method throw 后回读, 同格式消息抑制=$same , 独立异常保留",
    async ({ method, same }) => {
        const { transport, bridge, project, context, wrapper } = await feedbackHarness()
        const recoveryMessage = "上次保存未完成, 请先完成保存或核验"
        const independentMessage = "无法发送到宿主"
        const pending = {
            ...context,
            project: {
                ...project,
                hasPendingChanges: true,
                saveResult: {
                    modelSaved: true,
                    fileSaved: false,
                    desktopSynchronized: false,
                    message: recoveryMessage,
                    errorCode: method === "configuration.complete" ? "EXECUTION_NEEDS_VERIFICATION" : "SAVE_FAILED",
                },
            },
        }
        try {
            if (method === "configuration.complete") {
                transport.responses.set("configuration.apply", {
                    ...pending,
                    project: { ...pending.project, saveResult: { ...pending.project.saveResult, errorCode: "SAVE_FAILED" } },
                })
                await button(wrapper, "写入模板度量值").trigger("click")
                await flushPromises()
            }
            transport.hold = method
            await button(wrapper, method === "configuration.apply" ? "写入模板度量值" : "继续保存").trigger("click")
            await flushPromises()
            const readsBefore = transport.requests.filter((item) => item.method === "configuration.context").length
            transport.responses.set("configuration.context", pending)
            setLocale("en-us")
            transport.send({ id: transport.held!.id, error: { code: "SAVE_FAILED", message: hostText(same ? recoveryMessage : independentMessage) } })
            await flushPromises()
            expect(transport.requests.filter((item) => item.method === "configuration.context")).toHaveLength(readsBefore + 1)
            const recoveryAtSend = method === "configuration.complete" ? recoveryMessage : hostText(recoveryMessage)
            expect(notices("warning")).toEqual([recoveryAtSend])
            expect(notices("error")).toEqual(same ? [] : [hostText(independentMessage)])
            const feedbackCount = sentMessages.mock.calls.length
            for (const locale of ["en-us", "zh-cn"] as const) {
                setLocale(locale)
                // oxlint-disable-next-line no-await-in-loop -- 同一实例依次切换语言并验证回切, 不并发改全局语言.
                await flushPromises()
                expect(wrapper.find(".el-alert").exists()).toBe(false)
                expect(sentMessages.mock.calls).toHaveLength(feedbackCount)
            }
            expect(button(wrapper, "写入模板度量值").attributes("disabled")).toBeDefined()
            if (method === "configuration.complete") {
                expect(button(wrapper, "继续保存").attributes("disabled")).toBeDefined()
                expect(button(wrapper, "放弃修改").attributes("disabled")).toBeDefined()
            }
            expect(transport.requests.filter((item) => item.method === "configuration.apply")).toHaveLength(1)
        } finally {
            wrapper.unmount()
            bridge.dispose()
            setLocale("zh-cn")
        }
    },
)

it("部分保存继续保存只调用完成接口, 未知提交禁用继续且不重新应用配置", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const pending = {
        ...structuredClone(previewProject),
        hasPendingChanges: true,
        saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "pending save", errorCode: "SAVE_FAILED" },
    }
    transport.responses.set("configuration.context", configurationContext(pending, "pending-target"))
    const wrapper = mount(ConfigurationPanel, { props: { bridge: markRaw(bridge), project: pending, section: "measures", active: true } })
    try {
        await flushPromises()
        expect(button(wrapper, "继续保存")).toBeDefined()
        transport.responses.set("configuration.complete", configurationContext({ ...pending, hasPendingChanges: false, saveResult: null }, "pending-target"))
        await button(wrapper, "继续保存").trigger("click")
        await flushPromises()
        expect(transport.requests.filter((item) => item.method === "configuration.complete")).toHaveLength(1)
        expect(transport.requests.some((item) => ["configuration.apply", "scaffold.create"].includes(item.method))).toBe(false)
        const unknown = { ...pending, baselineId: "unknown", saveResult: { ...pending.saveResult, errorCode: "EXECUTION_NEEDS_VERIFICATION" } }
        transport.responses.set("configuration.context", configurationContext(unknown, "pending-target"))
        await wrapper.setProps({ project: unknown })
        await flushPromises()
        expect(button(wrapper, "继续保存").attributes("disabled")).toBeDefined()
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("配置抛出保存故障后只读回读pending并保留原错, 恢复不重放apply", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const project = structuredClone(previewProject)
    transport.responses.set("configuration.context", configurationContext(project, "readback-target"))
    const wrapper = mount(ConfigurationPanel, { props: { bridge: markRaw(bridge), project, section: "measures", active: true } })
    try {
        await flushPromises()
        expect(wrapper.find('section[id$="-measures"] [data-optional]').exists()).toBe(false)
        transport.hold = "configuration.apply"
        await button(wrapper, "写入模板度量值").trigger("click")
        await flushPromises()
        const pending = {
            ...project,
            hasPendingChanges: true,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, errorCode: "SAVE_FAILED", message: "authoritative pending" },
        }
        transport.responses.set("configuration.context", configurationContext(pending, "readback-target"))
        transport.send({ id: transport.held!.id, error: { code: "SAVE_FAILED", message: "original save failure" } })
        await flushPromises()
        expect(notices().join("\n")).toContain("original save failure")
        expect(notices().join("\n")).toContain("authoritative pending")
        expect(button(wrapper, "继续保存")).toBeDefined()
        expect(button(wrapper, "写入模板度量值").attributes("disabled")).toBeDefined()
        // 首次目录、操作前检查和失败回读均只读, 中间仍只允许一次实际应用.
        expect(transport.requests.filter((item) => ["configuration.context", "configuration.apply"].includes(item.method)).map((item) => item.method)).toEqual([
            "configuration.context",
            "configuration.context",
            "configuration.apply",
            "configuration.context",
        ])
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("配置目录加载取消已接受后等待排空, 即使旧请求返回成功也不继续下一请求", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    transport.hold = "scaffold.templates"
    const wrapper = mount(ConfigurationPanel, {
        props: { bridge: markRaw(bridge), project: structuredClone(previewProject), section: "measures", active: true },
    })
    try {
        await flushPromises()
        await new DOMWrapper(document.querySelector<HTMLButtonElement>(".operation-feedback__cancel")!).trigger("click")
        await flushPromises()
        expect(wrapper.attributes("aria-busy")).toBe("true")
        const catalog = new ScaffoldMock().handle("scaffold.templates", {})
        transport.send({ id: transport.held!.id, result: catalog })
        await flushPromises()
        expect(wrapper.attributes("aria-busy")).toBe("false")
        expect(transport.requests.some((item) => item.method === "configuration.context")).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("继续保存失败后重新读取未知提交状态并禁用再次恢复", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const project = {
        ...structuredClone(previewProject),
        hasPendingChanges: true,
        saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "pending save", errorCode: "SAVE_FAILED" },
    }
    transport.responses.set("configuration.context", configurationContext(project, "recover-failed-target"))
    const wrapper = mount(ConfigurationPanel, { props: { bridge: markRaw(bridge), project, section: "measures", active: true } })
    try {
        await flushPromises()
        transport.hold = "configuration.complete"
        await button(wrapper, "继续保存").trigger("click")
        await flushPromises()
        const unknown = {
            ...project,
            saveResult: { ...project.saveResult, errorCode: "EXECUTION_NEEDS_VERIFICATION", message: "authoritative verification required" },
        }
        transport.responses.set("configuration.context", configurationContext(unknown, "recover-failed-target"))
        transport.send({ id: transport.held!.id, error: { code: "EXECUTION_NEEDS_VERIFICATION", message: "save outcome unknown" } })
        await flushPromises()
        expect(notices().join("\n")).toContain("save outcome unknown")
        expect(notices().join("\n")).toContain("authoritative verification required")
        expect(button(wrapper, "继续保存").attributes("disabled")).toBeDefined()
        expect(transport.requests.filter((item) => item.method === "configuration.complete")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("打开已创建模型被宿主保存门禁拒绝时保留完成事实, 不恢复新建页配置写入口", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await button(wrapper, "创建项目").trigger("click")
        await flushPromises()
        transport.hold = "scaffold.openDesktop"
        await button(wrapper, "Power BI Desktop 打开").trigger("click")
        await flushPromises()
        transport.send({ id: transport.held!.id, error: { code: "UNSAVED_CHANGES", message: "宿主仍有未完成保存" } })
        await flushPromises()
        expect(notices().join("\n")).toContain("宿主仍有未完成保存")
        expect(wrapper.emitted("connected")).toBeUndefined()
        expect(wrapper.find(".scaffold-created-target").exists()).toBe(true)
        expect(wrapper.find(".scaffold-form").exists()).toBe(false)
        expect(wrapper.find(".configuration-recovery").exists()).toBe(false)
        expect(button(wrapper, "写入模板度量值")).toBeUndefined()
        expect(button(wrapper, "继续保存")).toBeUndefined()
        expect(button(wrapper, "放弃修改")).toBeUndefined()
        expect(button(wrapper, "重新核验工程")).toBeUndefined()
        expect(button(wrapper, "Power BI Desktop 打开").attributes("disabled")).toBeUndefined()
        expect(button(wrapper, "打开项目目录").attributes("disabled")).toBeUndefined()
        expect(transport.requests.some((item) => item.method.startsWith("configuration."))).toBe(false)
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("模板刷新不静默接受同ID新基线, 保留其他配置并要求重新选择", async () => {
    const transport = new ControlledTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(ScaffoldPanel, { props: { bridge: markRaw(bridge) } })
    try {
        await flushPromises()
        await button(wrapper, "选择目录").trigger("click")
        await flushPromises()
        await wrapper.get('[data-test="project-name"] input').setValue("KeepProjectName")
        await wrapper.get('[data-step="scaffold-measures"]').trigger("click")
        await wrapper.get("#section-scaffold-measures [data-optional] input").setValue(true)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("KeptOnRefresh")
        await wrapper.get('[data-step="scaffold-template"]').trigger("click")
        const catalog = new ScaffoldMock().handle("scaffold.templates", {}) as Record<string, unknown>
        transport.responses.set("scaffold.templates", {
            ...catalog,
            templates: (catalog.templates as Record<string, unknown>[]).map((item) => Object.assign({}, item, { baseline: "9".repeat(64) })),
        })
        await button(wrapper, "自定义模板").trigger("click")
        await flushPromises()
        const modal = new DOMWrapper(document.querySelector(".el-dialog")!)
        await modal
            .findAll("button")
            .find((item) => item.text() === "刷新")!
            .trigger("click")
        await flushPromises()
        await modal
            .findAll("button")
            .find((item) => item.text() === "关闭")!
            .trigger("click")
        await flushPromises()
        expect(button(wrapper, "创建项目").attributes("disabled")).toBeDefined()
        expect(notices().join("\n")).toContain("模板已变化或不可用")
        expect((wrapper.get('[data-test="project-name"] input').element as HTMLInputElement).value).toBe("KeepProjectName")
        expect((wrapper.get(".scaffold-measures tbody tr input").element as HTMLInputElement).value).toBe("KeptOnRefresh")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})
