/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfCachedCatalog.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证目录首次引导, 显式更新, 平铺筛选与安全文本高亮.
 */
import { expect, it, vi } from "vitest"
import { DOMWrapper, mount, flushPromises } from "@vue/test-utils"
import { ElOption, ElSelect } from "element-plus"
import { markRaw } from "vue"
import { BridgeClient, type Transport } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import UdfSourceManager from "@/features/scaffold/UdfSourceManager.vue"
import UdfHighlight from "@/features/scaffold/UdfHighlight.vue"
import UdfLibraryCard from "@/features/scaffold/UdfLibraryCard.vue"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import type { UdfCatalogPackage, UdfSelection } from "@/features/scaffold/contracts"

const dialogButton = (label: string) =>
    new DOMWrapper([...document.querySelectorAll<HTMLButtonElement>(".el-dialog button")].findLast((item) => item.textContent?.trim() === label)!)

it("首次只读取本地状态, 显式更新后平铺函数卡片并保持筛选外选择", async () => {
    const bridge = new BridgeClient(new MockTransport()),
        request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, {
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [] as UdfSelection[],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    await flushPromises()
    expect(request.mock.calls.some(([method, params]) => method === "udf.catalog" && "action" in params && params.action === "packages")).toBe(true)
    expect(request.mock.calls.some(([method, params]) => method === "udf.catalog" && "action" in params && params.action === "status")).toBe(false)
    expect(wrapper.text()).toContain("更新来源")
    expect(wrapper.findAll(".udf-library-card")).toHaveLength(0)
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.findAll(".udf-library-card")).toHaveLength(1)
    await wrapper.find('.udf-library-card input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")[0]?.catalogBaseline).toBeTruthy()
    await wrapper.find(".udf-search input").setValue("<img src=x onerror=alert(1)>")
    await wrapper
        .findAll("button")
        .find((item) => item.text() === "搜索")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.findAll(".udf-library-card")).toHaveLength(0)
    expect(wrapper.props("modelValue")).toHaveLength(1)
    expect(wrapper.find("img[src=x]").exists()).toBe(false)
    expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(1)
    wrapper.unmount()
    bridge.dispose()
})

/** 将显式模拟目录的第二轮更新变为新的稳定包, 保持真实桥接请求和组件状态机. */
class DriftingTransport implements Transport {
    mock = new MockTransport()
    revision = 0
    failRefresh = false
    failStatus = false
    missingFunction = false
    changeDependencyOnly = false
    noDependencies = false
    listeners = new Map<(event: MessageEvent) => void, (event: MessageEvent) => void>()
    addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        const wrapped = (event: MessageEvent) => {
            const data = structuredClone(event.data) as {
                id?: string
                result?: {
                    versions?: string[]
                    package?: {
                        id: string
                        hash: string
                        licenseFiles: Record<string, string>
                        functions: { name: string }[]
                        dependencies: { id: string; version: string }[]
                    }
                }
            }
            if (this.revision > 1 && !this.changeDependencyOnly && data.result?.versions) data.result.versions.push("2.0.0")
            if (this.noDependencies && data.result?.package) data.result.package.dependencies = []
            if (this.revision > 1 && data.result?.package && (!this.changeDependencyOnly || data.result.package.id === "mock.dependency")) {
                data.result.package.hash = "d".repeat(64)
                data.result.package.licenseFiles = { LICENSE: "Updated license text" }
                if (this.missingFunction) data.result.package.functions = data.result.package.functions.filter((item) => !item.name.endsWith("Second"))
            }
            listener(new MessageEvent("message", { data }))
        }
        this.listeners.set(listener, wrapped)
        this.mock.addEventListener(type, wrapped)
    }
    removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        const wrapped = this.listeners.get(listener)
        if (wrapped) this.mock.removeEventListener(type, wrapped)
        this.listeners.delete(listener)
    }
    postMessage(value: unknown): void {
        const item = value as { id: string; method: string; params: { action?: string } }
        if (
            (item.method === "udf.catalogRefresh" && this.failRefresh) ||
            (item.method === "udf.catalog" && ["status", "packages"].includes(item.params.action ?? "") && this.failStatus)
        ) {
            for (const listener of this.listeners.keys())
                listener(new MessageEvent("message", { data: { protocolVersion: 1, id: item.id, error: { code: "UPDATE_FAILED", message: "update failed" } } }))
            return
        }
        if (item.method === "udf.catalogRefresh") this.revision++
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        this.mock.postMessage(value)
    }
}
it("新稳定版刷新保留旧多选版本, 取消核验不换版, 明确审阅确认后才解除提交保护", async () => {
    const bridge = new BridgeClient(new DriftingTransport())
    const wrapper = mount(UdfStep, {
        attachTo: document.body,
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [] as UdfSelection[],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    await flushPromises()
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    const inputs = wrapper.findAll('.udf-library-card input[type="checkbox"]')
    await inputs[0]!.setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")[0]?.functions).toHaveLength(2)
    const before = JSON.parse(JSON.stringify(wrapper.props("modelValue"))) as UdfSelection[]
    expect(before[0]?.version).toBe("1.10.0")
    expect(wrapper.find(".udf-selected").text()).toContain("1.10.0")
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual(before)
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([false])
    expect(wrapper.find(".udf-selected").text()).toContain("1.10.0")
    const revalidate = () => wrapper.findAll("button").find((item) => item.text() === "重新核验所选函数")!
    await revalidate().trigger("click")
    await flushPromises()
    expect(document.body.textContent).toContain("1.10.0 → 1.10.0")
    expect(document.body.textContent).toContain("Updated license text")
    await dialogButton("取消").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual(before)
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([false])
    await revalidate().trigger("click")
    await flushPromises()
    await dialogButton("确认采用当前缓存").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")[0]?.catalogBaseline).not.toBe(before[0]?.catalogBaseline)
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([true])
    expect(wrapper.find(".udf-selected").text()).toContain("1.10.0")
    wrapper.unmount()
    bridge.dispose()
})

it("搜索高亮只插值纯文本, 正则和 HTML 字符保持字面意义", () => {
    const wrapper = mount(UdfHighlight, { props: { text: "Library <img src=x onerror=alert(1)> .*", query: "<img src=x onerror=alert(1)>" } })
    expect(wrapper.find("mark").text()).toBe("<img src=x onerror=alert(1)>")
    expect(wrapper.find("img[src=x]").exists()).toBe(false)
    wrapper.unmount()
})
it("包卡片一次表达整包选择, 搜索摘要高亮且不展开单函数块", async () => {
    const pkg: UdfCatalogPackage = {
        id: "Example.Library",
        version: "1.0.0",
        hash: "c".repeat(64),
        description: "library description",
        authors: "Author",
        tags: ["Testing"],
        committedAt: null,
        iconKey: null,
        functionCount: 2,
    }
    const wrapper = mount(UdfLibraryCard, { props: { pkg, checked: false, query: "library", disabled: false } })
    expect(wrapper.findAll('input[type="checkbox"]')).toHaveLength(1)
    expect(wrapper.findAll("mark").map((item) => item.text().toLowerCase())).toContain("library")
    await wrapper.find('input[type="checkbox"]').setValue(true)
    expect(wrapper.emitted("select")?.at(-1)).toEqual([true])
    await wrapper.setProps({ checked: true, disabled: true })
    await wrapper.find('input[type="checkbox"]').setValue(false)
    expect(wrapper.emitted("select")).toHaveLength(1)
    wrapper.unmount()
})
it("共享模型动作插槽位于已选摘要末尾, 新建入口显示随创建导入说明", async () => {
    const bridge = new BridgeClient(new MockTransport())
    const wrapper = mount(UdfStep, {
        props: { bridge: markRaw(bridge), disabled: false, modelValue: [] },
        slots: { actions: '<button data-test="model-import">Import selected UDF</button>' },
    })
    await flushPromises()
    expect(wrapper.find('.udf-selected .udf-selected-actions [data-test="model-import"]').exists()).toBe(true)
    wrapper.unmount()
    const create = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    expect(create.find(".udf-selected-actions").text()).toContain("所选函数将在创建工程时导入")
    create.unmount()
    bridge.dispose()
})

it("更新失败保留上次成功时间、旧卡片与选择, 不将失败当成新快照", async () => {
    const transport = new DriftingTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    const status = wrapper.find('[role="status"]').text(),
        cards = wrapper.findAll(".udf-library-card").map((item) => item.text())
    transport.failRefresh = true
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.find('[role="status"]').text()).toBe(status)
    expect(wrapper.findAll(".udf-library-card").map((item) => item.text())).toEqual(cards)
    expect(wrapper.props("modelValue")).toEqual([])
    wrapper.unmount()
    bridge.dispose()
})
it("登记 Git 来源发送协议和引用但不更新来源或联网读取包", async () => {
    const bridge = new BridgeClient(new MockTransport()),
        request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    expect(wrapper.findComponent(ElSelect).props("modelValue")).toBe("daxlib")
    await wrapper
        .findAll("button")
        .find((item) => item.text() === "管理来源")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.findComponent(UdfSourceManager).findComponent(ElSelect).exists()).toBe(false)
    expect(document.body.textContent).toContain("不需要安装 Git")
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="来源名称"]')!).setValue("Public Git library")
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="仓库地址"]')!).setValue("https://gitee.com/example/udfs?ref=main")
    await dialogButton("添加来源").trigger("click")
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "udf.sourceSave")).toHaveLength(0)
    expect(document.querySelector<HTMLInputElement>('[aria-label="来源名称"]')!.value).toBe("Public Git library")
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="仓库地址"]')!).setValue("https://gitlab.com/Team/Subgroup/Udfs.git/")
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="Git 分支 / 标签 / 提交 (可选)"]')!).setValue("main.lock")
    await dialogButton("添加来源").trigger("click")
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "udf.sourceSave")).toHaveLength(0)
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="Git 分支 / 标签 / 提交 (可选)"]')!).setValue("main")
    await new DOMWrapper(
        [...document.querySelectorAll<HTMLButtonElement>(".el-dialog button")].find((item) => item.textContent?.trim() === "添加来源")!,
    ).trigger("click")
    await flushPromises()
    expect(request.mock.calls.find(([method]) => method === "udf.sourceSave")?.[1]).toMatchObject({
        protocol: "git",
        gitRef: "main",
        baseUrl: "https://gitlab.com/Team/Subgroup/Udfs.git",
    })
    expect(document.body.textContent).toContain("Public Git library")
    expect(
        wrapper
            .findComponent(ElSelect)
            .findAllComponents(ElOption)
            .map((item) => item.props("label")),
    ).toContain("Public Git library")
    expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(0)
    expect(
        request.mock.calls.filter(
            ([method, params]) => method === "udf.catalog" && "action" in params && !["status", "packages"].includes(String(params.action)),
        ),
    ).toHaveLength(0)
    wrapper.unmount()
    bridge.dispose()
})

it("切回来源时状态读取失败立即撤销旧有效事实, 保留原选择但阻止提交", async () => {
    const transport = new DriftingTransport(),
        bridge = new BridgeClient(transport)
    const status = await bridge.request("udf.catalogRefresh", { source: "daxlib" }).promise
    const selection: UdfSelection = {
        source: "daxlib",
        packageId: "mock.sample",
        version: "1.10.0",
        functions: ["Mock.Sample.First"],
        baseline: "c".repeat(64),
        catalogBaseline: status.baseline,
    }
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [selection] } })
    await flushPromises()
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([true])
    const source = wrapper.findComponent(ElSelect)
    await source.setValue("local")
    source.vm.$emit("change", "local")
    await flushPromises()
    transport.failStatus = true
    await source.setValue("daxlib")
    source.vm.$emit("change", "daxlib")
    await flushPromises()
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([false])
    expect(wrapper.props("modelValue")).toEqual([selection])
    wrapper.unmount()
    bridge.dispose()
})
it("刷新后精确版本与内容完全相同可直接保留选择并更新目录基线", async () => {
    const transport = new DriftingTransport()
    transport.changeDependencyOnly = true
    transport.noDependencies = true
    const bridge = new BridgeClient(transport)
    const status = await bridge.request("udf.catalogRefresh", { source: "daxlib" }).promise
    const wrapper = mount(UdfStep, {
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [
                {
                    source: "daxlib",
                    packageId: "mock.sample",
                    version: "1.10.0",
                    functions: ["Mock.Sample.First"],
                    baseline: "c".repeat(64),
                    catalogBaseline: status.baseline,
                },
            ] as UdfSelection[],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    await flushPromises()
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")[0]?.catalogBaseline).not.toBe(status.baseline)
    expect(wrapper.props("modelValue")[0]?.version).toBe("1.10.0")
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([true])
    wrapper.unmount()
    bridge.dispose()
})
it("新目录缺少已选函数时核验预览显示缺失并禁用确认, 原选择不部分改变", async () => {
    const transport = new DriftingTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, {
        attachTo: document.body,
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [] as UdfSelection[],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    await flushPromises()
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    await wrapper.findAll('.udf-library-card input[type="checkbox"]')[0]!.setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")[0]?.functions).toHaveLength(2)
    const original = JSON.parse(JSON.stringify(wrapper.props("modelValue"))) as UdfSelection[]
    transport.missingFunction = true
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    await wrapper
        .findAll("button")
        .find((item) => item.text() === "重新核验所选函数")!
        .trigger("click")
    await flushPromises()
    const missing = new DOMWrapper(document.querySelector<HTMLElement>('.udf-revalidation-entry section[role="alert"]')!)
    expect(missing.get("h4").text()).toBe("当前缓存缺少的函数")
    expect(missing.findAll("ol > li").map((item) => item.text())).toEqual(["Mock.Sample.Second"])
    expect(dialogButton("确认采用当前缓存").attributes("disabled")).toBeDefined()
    expect(wrapper.props("modelValue")).toEqual(original)
    wrapper.unmount()
    bridge.dispose()
})

it("根包未变但精确依赖内容变化时保持过期保护, 核验必须展示递归依赖当前许可和基线", async () => {
    const transport = new DriftingTransport()
    transport.changeDependencyOnly = true
    const bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, {
        attachTo: document.body,
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [] as UdfSelection[],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    await flushPromises()
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    await wrapper.findAll('.udf-library-card input[type="checkbox"]')[0]!.setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")[0]?.functions).toHaveLength(2)
    const original = JSON.parse(JSON.stringify(wrapper.props("modelValue"))) as UdfSelection[]
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual(original)
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([false])
    await wrapper
        .findAll("button")
        .find((item) => item.text() === "重新核验所选函数")!
        .trigger("click")
    await flushPromises()
    expect(document.body.textContent).toContain("mock.dependency @ 1.0.0")
    expect(document.body.textContent).toContain("Updated license text")
    expect(document.body.textContent).toContain("d".repeat(64))
    expect(document.body.textContent).toContain("没有可核验的更新前完整依赖闭包")
    await dialogButton("确认采用当前缓存").trigger("click")
    await flushPromises()
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([true])
    wrapper.unmount()
    bridge.dispose()
})

it("可选 UDF 初始关闭但缓存已初始化时启用立即显示本地卡片, 不要求再次联网更新", async () => {
    const bridge = new BridgeClient(new MockTransport())
    await bridge.request("udf.catalogRefresh", { source: "daxlib" }).promise
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: true, modelValue: [] } })
    await flushPromises()
    await wrapper.setProps({ disabled: false })
    await flushPromises()
    expect(wrapper.findAll(".udf-library-card")).toHaveLength(1)
    expect(wrapper.find('.udf-library-card input[type="checkbox"]').attributes("disabled")).toBeUndefined()
    expect(request.mock.calls.filter(([method]) => method === "udf.catalogRefresh")).toHaveLength(0)
    wrapper.unmount()
    bridge.dispose()
})
