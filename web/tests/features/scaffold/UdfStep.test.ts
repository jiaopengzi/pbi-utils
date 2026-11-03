/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfStep.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证精确版本选择, 显式预发行, 本地函数基线 CRUD 和确认边界.
 */
import { expect, it, vi } from "vitest"
import { mount, flushPromises, DOMWrapper, type VueWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElSelect, ElCheckbox } from "element-plus"
import UdfLibraryCard from "@/features/scaffold/UdfLibraryCard.vue"
import UdfTagDialog from "@/features/scaffold/UdfTagDialog.vue"
import { BridgeClient, type Transport } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import CodeEditor from "@/features/code/CodeEditor.vue"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import UdfPackageDetailDialog from "@/features/scaffold/UdfPackageDetailDialog.vue"
import type { UdfSelection } from "@/features/scaffold/contracts"
/** 查找组件内的固定操作按钮. */
function button(wrapper: VueWrapper, label: string) {
    return wrapper.findAll("button").find((item) => item.text() === label)!
}
/** 从居中的真实弹窗查找按钮, 不把正文同名动作当成确认. */
function dialogButton(label: string) {
    return new DOMWrapper([...document.querySelectorAll<HTMLButtonElement>(".el-dialog button")].findLast((item) => item.textContent?.trim() === label)!)
}
/** 通过共享版本弹窗执行用户切tag, 不通过组件私有函数或旧已删除控件. */
async function chooseTag(wrapper: VueWrapper, tag: string): Promise<void> {
    await wrapper.findComponent(UdfLibraryCard).find(".udf-package-version button").trigger("click")
    await flushPromises()
    await wrapper.findComponent(UdfTagDialog).findComponent(ElSelect).setValue(tag)
    await dialogButton("确认版本").trigger("click")
    await flushPromises()
}
type Request = { id: string; method: string; params: Record<string, unknown> }
/** 既有选择行为测试使用已初始化缓存; 首次未缓存由独立用例覆盖. */
class CachedMockTransport extends MockTransport {
    initialized = new Set<string>()
    override postMessage(value: unknown): void {
        const item = value as Request
        if (item.method === "udf.catalog" && !this.initialized.has(`${String(item.params.source ?? "daxlib")}/${String(item.params.sourceBaseline ?? "")}`)) {
            this.initialized.add(`${String(item.params.source ?? "daxlib")}/${String(item.params.sourceBaseline ?? "")}`)
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            super.postMessage({ ...item, id: `${item.id}-initialize`, method: "udf.catalogRefresh" })
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        super.postMessage(value)
    }
}

/** 在真实桥接协议之下暂停网络与取消响应, 验证请求排空和迟到错误. */
class CatalogTransport implements Transport {
    mock = new CachedMockTransport()
    listeners = new Set<(event: MessageEvent) => void>()
    wrapped = new Map<(event: MessageEvent) => void, (event: MessageEvent) => void>()
    held?: Request
    cancelHeld?: Request
    holdCatalog = false
    versions?: string[]
    holdMethod?: string
    catalog?: (request: Request) => { result?: unknown; error?: { code: string; message: string } } | undefined
    addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        const wrapped = (event: MessageEvent) => {
            const data = structuredClone(event.data)
            if (this.versions && data.result?.packagePage) {
                const page = data.result.packagePage
                const stable = this.versions.find((version) => !version.split("+")[0]!.includes("-"))
                const version = stable ?? this.versions[0]
                page.items = page.items.map((item: object) => Object.assign({}, item, { version }))
            }
            listener(new MessageEvent("message", { data }))
        }
        this.wrapped.set(listener, wrapped)
        this.mock.addEventListener(type, wrapped)
        this.listeners.add(listener)
    }
    removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.mock.removeEventListener(type, this.wrapped.get(listener)!)
        this.wrapped.delete(listener)
        this.listeners.delete(listener)
    }
    postMessage(value: unknown): void {
        const item = value as Request
        if (item.method === "udf.catalog" && item.params.action === "status") {
            // 使用同一显式模拟缓存的真实基线, 不以合成基线替代分页见证.
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            this.mock.postMessage(value)
            return
        }
        if (item.method === this.holdMethod) {
            this.held = item
            return
        }
        if (item.method === "udf.catalog" && this.catalog && !["packages", "icon"].includes(String(item.params.action))) {
            const response = this.catalog(item)
            if (response) this.send({ id: item.id, ...response })
            else this.held = item
            return
        }
        if (item.method === "udf.catalog" && item.params.action === "versions" && this.versions) {
            this.send({ id: item.id, result: { versions: this.versions } })
            return
        }
        if (item.method === "udf.catalog" && this.holdCatalog) {
            this.held = item
            return
        }
        if (item.method === "request.cancel") {
            this.cancelHeld = item
            return
        }
        // 模拟传输为单参数宿主协议, 不是浏览器 window.postMessage.
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        this.mock.postMessage(value)
    }
    send(value: object): void {
        for (const listener of this.listeners) listener(new MessageEvent("message", { data: { protocolVersion: 1, ...value } }))
    }
}
it("初始仅查询函数摘要, 单选和多选均保留明确历史tag, 查看才读取详情", async () => {
    const bridge = new BridgeClient(new CachedMockTransport())
    const request = vi.spyOn(bridge, "request")
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
    expect(request.mock.calls.some(([method]) => method === "udf.catalogRefresh")).toBe(false)
    expect(wrapper.findComponent(ElSelect).props("modelValue")).toBe("daxlib")
    expect(wrapper.find(".udf-source-picker").exists()).toBe(true)
    expect(wrapper.text()).not.toContain("函数前缀")
    expect(wrapper.text()).toContain("mock.sample")
    expect(request.mock.calls.filter(([, params]) => "action" in params && ["package", "versions", "function"].includes(String(params.action)))).toHaveLength(0)
    await button(wrapper, "搜索").trigger("click")
    await flushPromises()
    const sourceSelect = wrapper.findComponent(ElSelect)
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    await chooseTag(wrapper, "0.9.0")
    await flushPromises()
    expect(wrapper.props("modelValue")[0]!.version).toBe("0.9.0")
    expect(wrapper.props("modelValue")[0]?.functions).toEqual(["Mock.Sample.First", "Mock.Sample.Second"])
    expect(wrapper.props("modelValue")[0]!.version).toBe("0.9.0")
    expect(wrapper.props("modelValue")[0]!.baseline).toBe("c".repeat(64))
    const catalogCalls = request.mock.calls.filter(([method]) => method === "udf.catalog")
    expect(catalogCalls.find(([, params]) => "action" in params && params.action === "package")![1]).toMatchObject({
        action: "package",
        source: "daxlib",
        version: "1.10.0",
    })
    await sourceSelect.setValue("local")
    sourceSelect.vm.$emit("change", "local")
    await flushPromises()
    expect(wrapper.props("modelValue")).toHaveLength(1)
    await sourceSelect.setValue("daxlib")
    sourceSelect.vm.$emit("change", "daxlib")
    await flushPromises()
    expect(wrapper.find(".udf-library-card").exists()).toBe(true)
    wrapper.unmount()
    bridge.dispose()
})
it("本地新增, 基线更新和取消删除不会删除条目, 明确确认后删除", async () => {
    const bridge = new BridgeClient(new CachedMockTransport())
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    await button(wrapper, "管理本地函数").trigger("click")
    await flushPromises()
    await dialogButton("新建本地函数").trigger("click")
    await flushPromises()
    const inputs = [...document.querySelectorAll<HTMLInputElement>(".el-dialog .scaffold-form .el-input__inner")]
    await new DOMWrapper(inputs[0]!).setValue("Local.First")
    await wrapper
        .findAllComponents(CodeEditor)
        .find((editor) => editor.props("language") === "dax")!
        .setValue("() => 1")
    await dialogButton("保存").trigger("click")
    await flushPromises()
    let list = await bridge.request("udf.localList", {}).promise
    expect(list.items[0]!.definition.name).toBe("Local.First")
    const original = list.items[0]!
    await dialogButton("编辑").trigger("click")
    await flushPromises()
    await wrapper
        .findAllComponents(CodeEditor)
        .find((editor) => editor.props("language") === "dax")!
        .setValue("() => 2")
    await dialogButton("保存").trigger("click")
    await flushPromises()
    list = await bridge.request("udf.localList", {}).promise
    expect(list.items[0]!.id).toBe(original.id)
    expect(list.items[0]!.baseline).not.toBe(original.baseline)
    await dialogButton("删除").trigger("click")
    await flushPromises()
    await dialogButton("取消").trigger("click")
    await flushPromises()
    expect((await bridge.request("udf.localList", {}).promise).items).toHaveLength(1)
    await dialogButton("删除").trigger("click")
    await flushPromises()
    await dialogButton("确认删除").trigger("click")
    await flushPromises()
    expect((await bridge.request("udf.localList", {}).promise).items).toHaveLength(0)
    wrapper.unmount()
    bridge.dispose()
})
it("版本菜单按语义版本降序列出精确 tag, 最新稳定版入口保留且不自动选择 beta", async () => {
    const transport = new CatalogTransport()
    transport.versions = ["1.9.0", "2.0.0-beta.2", "1.10.0", "2.0.0-beta.10", "1.10.0-rc.1"]
    const bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    await wrapper.findComponent(UdfLibraryCard).find(".udf-package-version button").trigger("click")
    await flushPromises()
    const picker = wrapper.findComponent(UdfTagDialog).findComponent(ElSelect)
    expect(picker.findAllComponents({ name: "ElOption" }).map((item) => item.props("value"))).toEqual([
        "latest",
        "2.0.0-beta.10",
        "2.0.0-beta.2",
        "1.10.0",
        "1.10.0-rc.1",
        "1.9.0",
    ])
    expect(picker.findAllComponents({ name: "ElOption" })[0]!.props("label")).toContain("1.10.0")
    expect(picker.props("modelValue")).toBe("latest")
    wrapper.unmount()
    bridge.dispose()
})
it("第三方来源经宿主管理, 请求只携带来源 ID, 删除确认成功后仅移除同源选择且不自动切换", async () => {
    const bridge = new BridgeClient(new CachedMockTransport())
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, {
        attachTo: document.body,
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [
                {
                    source: "daxlib",
                    packageId: "mock.sample",
                    version: "1.0.0",
                    functions: ["Mock.Sample.First"],
                    baseline: "c".repeat(64),
                },
            ],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    await flushPromises()
    const initialStatus = await bridge.request("udf.catalog", { action: "status", source: "daxlib" }).promise
    await wrapper.setProps({
        modelValue: wrapper.props("modelValue").map((item) => Object.assign({}, item, { catalogBaseline: initialStatus.status?.baseline })),
    })
    await button(wrapper, "管理来源").trigger("click")
    await flushPromises()
    expect(document.body.textContent).toContain("DAXLib")
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="来源名称"]')!).setValue("Team library")
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="仓库地址"]')!).setValue("http://example.com/")
    await dialogButton("添加来源").trigger("click")
    await flushPromises()
    expect(request.mock.calls.filter(([method]) => method === "udf.sourceSave")).toHaveLength(0)
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="仓库地址"]')!).setValue("https://github.com/example/udfs")
    await dialogButton("添加来源").trigger("click")
    await flushPromises()
    const sourceResult = await bridge.request("udf.sources", {}).promise
    const custom = sourceResult.items.find((item) => item.name === "Team library")!
    expect(custom.id).toMatch(/^source:/)
    expect(custom.baseline).toMatch(/^[a-f0-9]{64}$/)
    await expect(bridge.request("udf.catalog", { action: "search", source: custom.id, sourceBaseline: "a".repeat(64) }).promise).rejects.toThrow(
        "UDF_SOURCE_BASELINE_CHANGED",
    )
    await dialogButton("完成").trigger("click")
    await flushPromises()
    const sourceSelect = wrapper.findComponent(ElSelect)
    await sourceSelect.setValue(custom.id)
    sourceSelect.vm.$emit("change", custom.id)
    await flushPromises()
    await button(wrapper, "搜索").trigger("click")
    await flushPromises()
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue").find((item) => item.source === custom.id)).toMatchObject({ sourceBaseline: custom.baseline })
    expect(wrapper.props("modelValue").every((item) => item.version !== null && item.baseline !== null)).toBe(true)
    expect(wrapper.findComponent(UdfLibraryCard).find(".udf-package-version button").attributes("disabled")).toBeUndefined()
    expect(request.mock.calls.filter(([method]) => method === "udf.catalog").every(([, params]) => !("baseUrl" in params))).toBe(true)
    await button(wrapper, "管理来源").trigger("click")
    await flushPromises()
    await dialogButton("删除").trigger("click")
    await flushPromises()
    await dialogButton("取消").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toHaveLength(2)
    await dialogButton("删除").trigger("click")
    await flushPromises()
    await dialogButton("确认删除").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue").map((item) => item.source)).toEqual(["daxlib"])
    expect(sourceSelect.props("modelValue")).toBe("")
    wrapper.unmount()
    bridge.dispose()
})
it("未启用 UDF 时仍读取本地来源登记, 启用后不自动搜索网络", async () => {
    const bridge = new BridgeClient(new CachedMockTransport()),
        request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: true, modelValue: [] } })
    await flushPromises()
    await wrapper.setProps({ disabled: false })
    expect(request.mock.calls.some(([method]) => method === "udf.catalogRefresh")).toBe(false)
    expect(
        wrapper
            .findComponent(ElSelect)
            .findAllComponents({ name: "ElOption" })
            .map((item) => item.props("label")),
    ).toEqual(["DAXLib", "本地"])
    wrapper.unmount()
    bridge.dispose()
})
it("取消接受或迟到取消失败均不提前解锁或污染下一次目录请求", async () => {
    const transport = new CatalogTransport(),
        bridge = new BridgeClient(transport)
    transport.holdCatalog = true
    const wrapper = mount(UdfStep, { props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    await button(wrapper, "搜索").trigger("click")
    await flushPromises()
    const first = transport.held!
    await new DOMWrapper(document.querySelector<HTMLButtonElement>(".operation-message button")!).trigger("click")
    await flushPromises()
    expect(button(wrapper, "搜索").attributes("disabled")).toBeDefined()
    transport.send({ id: first.id, error: { code: "CANCELLED", message: "cancelled" } })
    await flushPromises()
    await button(wrapper, "搜索").trigger("click")
    await flushPromises()
    transport.send({ id: transport.cancelHeld!.id, error: { code: "CANCEL_FAILED", message: "stale cancel error" } })
    await flushPromises()
    expect(wrapper.text()).not.toContain("stale cancel error")
    expect(button(wrapper, "搜索").attributes("disabled")).toBeDefined()
    transport.send({ id: transport.held!.id, error: { code: "CANCELLED", message: "cancelled" } })
    await flushPromises()
    expect(button(wrapper, "搜索").attributes("disabled")).toBeUndefined()
    wrapper.unmount()
    bridge.dispose()
})
it("仅有预发行标签的函数库在明确显示预发行后可浏览, 选择精确版本后可自动导入", async () => {
    const transport = new CatalogTransport()
    transport.holdCatalog = false
    transport.versions = ["1.0.0-beta.2", "1.0.0-beta.1"]
    const bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, {
        global: { stubs: { transition: false } },
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
    await wrapper.findAllComponents(ElCheckbox)[0]!.setValue(true)
    await button(wrapper, "搜索").trigger("click")
    await flushPromises()
    expect(wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').attributes("disabled")).toBeDefined()
    wrapper.findComponent(UdfLibraryCard).vm.$emit("select", true)
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([])
    wrapper.findComponent(UdfLibraryCard).vm.$emit("details")
    await flushPromises()
    expect(document.querySelector(".udf-detail-dialog .el-checkbox__original")).toBeNull()
    await new DOMWrapper(document.querySelector<HTMLButtonElement>(".udf-detail-dialog .el-dialog__headerbtn")!).trigger("click")
    await vi.waitFor(() => expect(wrapper.findComponent(UdfPackageDetailDialog).props("modelValue")).toBe(false))
    await chooseTag(wrapper, "1.0.0-beta.2")
    await flushPromises()
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")[0], document.body.textContent ?? "").toMatchObject({ version: "1.0.0-beta.2" })
    wrapper.unmount()
    bridge.dispose()
})
it("稳定标签的构建元数据含连字符时无需显示预发行即可浏览和选择函数", async () => {
    const transport = new CatalogTransport()
    transport.holdCatalog = false
    transport.versions = ["1.0.0+build-beta"]
    const bridge = new BridgeClient(transport)
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
    expect(wrapper.findAllComponents(ElCheckbox)[0]!.props("modelValue")).toBe(false)
    await button(wrapper, "搜索").trigger("click")
    await flushPromises()
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")[0]).toMatchObject({ version: "1.0.0+build-beta" })
    wrapper.unmount()
    bridge.dispose()
})

/** 为完整目录读取提供两个库, 包内容仍通过真实桥接客户端传递. */
function bulkCatalog(request: Request) {
    if (request.params.action === "search")
        return {
            result: {
                packages: [
                    { id: "Example.One", version: "1.0.0", description: "" },
                    { id: "Example.Two", version: "1.0.0", description: "" },
                ],
                total: 2,
                nextOffset: null,
            },
        }
    if (request.params.action === "versions") return { result: { versions: ["1.9.0", "2.0.0-beta", "1.10.0+build-beta"] } }
    return {
        result: {
            package: {
                id: request.params.packageId,
                version: request.params.version,
                hash: "c".repeat(64),
                description: "",
                readme: "",
                licenseFiles: { LICENSE: "Permission from original package" },
                dependencies: request.params.packageId === "Dependency" ? [] : [{ id: "Dependency", version: "1.0.0" }],
                functions: ["First", "Second"].map((name) => ({
                    name: `${String(request.params.packageId)}.${name}`,
                    expression: "() => 1",
                    description: "",
                    annotations: {},
                })),
                retrievedAt: "2026-10-20T00:00:00Z",
                fromCache: false,
            },
        },
    }
}

it("全量导入重新读取当前来源全部稳定库, 独立于搜索并在许可确认后原子替换同源选择", async () => {
    const transport = new CatalogTransport()
    transport.catalog = bulkCatalog
    const bridge = new BridgeClient(transport),
        request = vi.spyOn(bridge, "request")
    const local = await bridge.request("udf.localSave", {
        definition: { schemaVersion: 1, name: "Local.Keep", version: "1.0.0", expression: "() => 1", description: "", dependencies: [] },
    }).promise
    const kept: UdfSelection = {
        source: "local",
        packageId: local.id,
        version: "1.0.0",
        functions: ["Local.Keep"],
        baseline: local.baseline,
    }
    const wrapper = mount(UdfStep, {
        attachTo: document.body,
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [kept],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    await flushPromises()
    await wrapper.find(".udf-search input").setValue("only one search result")
    await wrapper.findAllComponents(ElCheckbox)[0]!.setValue(true)
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([kept])
    expect(document.body.textContent).toContain("Permission from original package")
    expect(document.body.textContent).toContain("Dependency@1.0.0")
    expect(request.mock.calls.find(([, params]) => "action" in params && params.action === "search")?.[1]).toMatchObject({
        query: "",
        includePrerelease: false,
        source: "daxlib",
    })
    expect(
        request.mock.calls
            .filter(([, params]) => "action" in params && params.action === "package")
            .filter(([, params]) => "packageId" in params && String(params.packageId).startsWith("Example."))
            .slice(0, 2)
            .map(([, params]) => params),
    ).toMatchObject([
        { action: "package", source: "daxlib", packageId: "Example.One", version: "1.10.0+build-beta" },
        { action: "package", source: "daxlib", packageId: "Example.Two", version: "1.10.0+build-beta" },
    ])
    await dialogButton("确认选择全部函数").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toMatchObject([
        kept,
        {
            source: "daxlib",
            packageId: "Example.One",
            version: "1.10.0+build-beta",
            functions: ["Example.One.First", "Example.One.Second"],
            baseline: "c".repeat(64),
        },
        {
            source: "daxlib",
            packageId: "Example.Two",
            version: "1.10.0+build-beta",
            functions: ["Example.Two.First", "Example.Two.Second"],
            baseline: "c".repeat(64),
        },
    ])
    wrapper.unmount()
    bridge.dispose()
})

it("全量读取后续库失败时保留原选择, 不允许确认部分导入", async () => {
    const transport = new CatalogTransport()
    transport.catalog = (request) =>
        request.params.packageId === "Example.Two" ? { error: { code: "NETWORK_FAILED", message: "second package failed" } } : bulkCatalog(request)
    const bridge = new BridgeClient(transport)
    const original: UdfSelection[] = [{ source: "daxlib", packageId: "keep", version: "1.0.0", functions: ["Keep"], baseline: null }]
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: original } })
    await flushPromises()
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual(original)
    expect(document.body.textContent).toContain("second package failed")
    expect(dialogButton("确认选择全部函数").attributes("disabled")).toBeDefined()
    wrapper.unmount()
    bridge.dispose()
})

it("全量读取取消后等待当前请求终态, 不加入先前已读取的库", async () => {
    const transport = new CatalogTransport()
    const bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    transport.catalog = (request) => (request.params.packageId === "Example.Two" ? undefined : bulkCatalog(request))
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    await flushPromises()
    await dialogButton("取消").trigger("click")
    await flushPromises()
    expect(transport.cancelHeld?.params.requestId).toBe(transport.held?.id)
    expect(button(wrapper, "选择当前来源全部函数").attributes("disabled")).toBeDefined()
    transport.send({ id: transport.held!.id, error: { code: "CANCELLED", message: "cancelled" } })
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([])
    expect(dialogButton("确认选择全部函数").attributes("disabled")).toBeDefined()
    expect(document.body.textContent).toContain("已取消")
    wrapper.unmount()
    bridge.dispose()
})

it("本地全量导入重新读取全部宿主条目并保留精确基线", async () => {
    const bridge = new BridgeClient(new CachedMockTransport())
    const local = await bridge.request("udf.localSave", {
        definition: { schemaVersion: 1, name: "Local.One", version: "1.0.0-beta", expression: "() => 1", description: "", dependencies: [] },
    }).promise
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
    const select = wrapper.findComponent(ElSelect)
    await select.setValue("local")
    select.vm.$emit("change", "local")
    await flushPromises()
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    await flushPromises()
    await dialogButton("确认选择全部函数").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([
        { source: "local", packageId: local.id, version: "1.0.0-beta", functions: ["Local.One"], baseline: local.baseline },
    ])
    wrapper.unmount()
    bridge.dispose()
})

it("自定义来源全量读取绑定宿主基线, 预发行依赖自动解析且无需开关", async () => {
    const transport = new CatalogTransport()
    transport.catalog = bulkCatalog
    const bridge = new BridgeClient(transport),
        request = vi.spyOn(bridge, "request")
    const source = await bridge.request("udf.sourceSave", { name: "Team", baseUrl: "https://github.com/example/udfs" }).promise
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
    const select = wrapper.findComponent(ElSelect)
    await select.setValue(source.id)
    select.vm.$emit("change", source.id)
    await flushPromises()
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    await flushPromises()
    expect(document.querySelector(".udf-bulk-package .el-checkbox__original")).toBeNull()
    await dialogButton("确认选择全部函数").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue").map((item) => ({ source: item.source, sourceBaseline: item.sourceBaseline }))).toEqual([
        { source: source.id, sourceBaseline: source.baseline },
        { source: source.id, sourceBaseline: source.baseline },
    ])
    expect(
        request.mock.calls
            .filter(([method, params]) => method === "udf.catalog" && "source" in params && params.source === source.id)
            .every(([, params]) => "sourceBaseline" in params && params.sourceBaseline === source.baseline),
    ).toBe(true)
    wrapper.unmount()
    bridge.dispose()
})

it("空来源明确显示无函数并禁用确认, 未启用时不能发起全量读取", async () => {
    const transport = new CatalogTransport()
    transport.catalog = () => ({ result: { packages: [], total: 0, nextOffset: null } })
    const bridge = new BridgeClient(transport),
        request = vi.spyOn(bridge, "request")
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: true, modelValue: [] } })
    await flushPromises()
    expect(button(wrapper, "选择当前来源全部函数").attributes("disabled")).toBeDefined()
    const initialReads = request.mock.calls.length
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    expect(request.mock.calls).toHaveLength(initialReads)
    await wrapper.setProps({ disabled: false })
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    await flushPromises()
    expect(document.body.textContent).toContain("当前来源没有可导入的函数")
    expect(dialogButton("确认选择全部函数").attributes("disabled")).toBeDefined()
    expect(wrapper.props("modelValue")).toEqual([])
    wrapper.unmount()
    bridge.dispose()
})

it("全量函数超过当前单包上限时整批拒绝, 不静默取前128个", async () => {
    const transport = new CatalogTransport()
    transport.catalog = (request) => {
        if (request.params.action !== "package") return bulkCatalog(request)
        const result = bulkCatalog(request) as { result: { package: { functions: object[] } } }
        result.result.package.functions = Array.from({ length: 129 }, (_, index) => ({
            name: `Example.Function${index}`,
            expression: "() => 1",
            description: "",
            annotations: {},
        }))
        return result
    }
    const bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    await button(wrapper, "选择当前来源全部函数").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([])
    expect(document.body.textContent).toContain("未导入部分结果")
    expect(dialogButton("确认选择全部函数").attributes("disabled")).toBeDefined()
    wrapper.unmount()
    bridge.dispose()
})

it("取消请求先发出但来源保存已提交时保留宿主权威成功, 不将真实写入当成取消", async () => {
    const transport = new CatalogTransport(),
        bridge = new BridgeClient(transport)
    const wrapper = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: [] } })
    await flushPromises()
    await button(wrapper, "管理来源").trigger("click")
    await flushPromises()
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="来源名称"]')!).setValue("Committed source")
    await new DOMWrapper(document.querySelector<HTMLInputElement>('[aria-label="仓库地址"]')!).setValue("https://github.com/example/udfs")
    transport.holdMethod = "udf.sourceSave"
    await dialogButton("添加来源").trigger("click")
    await flushPromises()
    await new DOMWrapper(document.querySelector<HTMLButtonElement>(".operation-message button")!).trigger("click")
    await flushPromises()
    transport.send({ requestPhase: { requestId: transport.held!.id, phase: "committing" } })
    transport.send({
        id: transport.held!.id,
        result: {
            id: `source:${"a".repeat(32)}`,
            name: "Committed source",
            baseUrl: "https://github.com/example/udfs",
            baseline: "b".repeat(64),
            kind: "custom",
        },
    })
    await flushPromises()
    expect(document.querySelector<HTMLInputElement>('[aria-label="来源名称"]')!.value).toBe("")
    expect(document.body.textContent).toContain("Committed source")
    wrapper.unmount()
    bridge.dispose()
})

it("已选函数按包展示有序列表, 保留精确版本且逐项可读", async () => {
    const bridge = new BridgeClient(new CachedMockTransport())
    const wrapper = mount(UdfStep, {
        props: {
            bridge: markRaw(bridge),
            disabled: false,
            modelValue: [
                {
                    source: "daxlib",
                    packageId: "Library.One",
                    version: "1.0.0-beta",
                    functions: ["Library.One.LongFunction", "Library.One.Second"],
                    baseline: "c".repeat(64),
                    catalogBaseline: "c".repeat(64),
                },
                { source: "local", packageId: "local-one", version: "2.0.0", functions: ["Local.Third"], baseline: "d".repeat(64) },
            ],
        },
    })
    await flushPromises()
    const lists = wrapper.findAll(".udf-selected ol")
    expect(lists).toHaveLength(2)
    expect(lists[0]!.findAll("li").map((row) => row.text())).toEqual(["Library.One.LongFunction", "Library.One.Second"])
    expect(lists[1]!.attributes("start")).toBe("3")
    expect(wrapper.find(".udf-selected").text()).toContain("1.0.0-beta")
    expect(wrapper.find(".udf-selected").text()).not.toContain("LongFunction, Library")
    wrapper.unmount()
    bridge.dispose()
})
