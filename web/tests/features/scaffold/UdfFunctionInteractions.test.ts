/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfFunctionInteractions.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证真实组件分页/按需详情/依赖原子选择/整数反馈与最新缓存核验, 所有来源均为明确内存替身.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, mount, flushPromises } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElMessage, ElPagination, ElSelect } from "element-plus"
import { BridgeClient, type Transport } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import UdfFunctionRow from "@/features/scaffold/UdfFunctionRow.vue"
import UdfLibraryCard from "@/features/scaffold/UdfLibraryCard.vue"
import UdfTagDialog from "@/features/scaffold/UdfTagDialog.vue"
import type { LocalUdfDefinition, UdfCatalogStatus, UdfCatalogPackage, UdfPackage, UdfSelection } from "@/features/scaffold/contracts"
import { matchUdfName } from "@/features/scaffold/udfSearch"

type Request = { id: string; method: string; params: Record<string, unknown> }
const hash = "c".repeat(64)
const cleanup: (() => void)[] = []
afterEach(() => {
    cleanup.splice(0).forEach((close) => close())
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    vi.useRealTimers()
})
/** 真实BridgeClient之下的确定性分页/包替身, 不访问网络或用户文件. */
class FunctionTransport implements Transport {
    mock = new MockTransport()
    requests: Request[] = []
    listeners = new Map<(event: MessageEvent) => void, (event: MessageEvent) => void>()
    status: UdfCatalogStatus = { initialized: false, baseline: null, retrievedAt: null, packageCount: 0, versionCount: 0, functionCount: 0 }
    holdRefresh = false
    pending?: Request
    failList = false
    unconfirmed = false
    missingOnOldTag = false
    conflict = false
    oldTagRemoved = false
    withIcon = false
    /** 捕获模拟宿主当前代, 但内部发布回执不作为用户请求成功. */
    addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        const wrapped = (event: MessageEvent) => {
            const data = event.data as { id?: string; result?: UdfCatalogStatus & { status?: UdfCatalogStatus } }
            if (data.result?.status) this.status = data.result.status
            else if (data.result?.initialized !== undefined) this.status = data.result
            if (!data.id?.startsWith("internal-")) listener(event)
        }
        this.listeners.set(listener, wrapped)
        this.mock.addEventListener(type, wrapped)
    }
    removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.mock.removeEventListener(type, this.listeners.get(listener)!)
        this.listeners.delete(listener)
    }
    send(value: object): void {
        for (const listener of this.listeners.keys()) listener(new MessageEvent("message", { data: { protocolVersion: 1, ...value } }))
    }
    /** 包内容只在显式选择/查看/切tag时可读取, 每库不超过128函数. */
    pkg(id: string, version: string): UdfPackage {
        const length = id === "SVG" ? 80 : id === "Time" ? 41 : 1
        const functions = Array.from({ length }, (_, index) => ({
            name: `${id}.Fn${String(index).padStart(3, "0")}`,
            expression: `() => ${index}\r\n// <img src=x onerror=alert(1)>`,
            description: "Description only",
            annotations: {},
        }))
        return {
            id,
            version,
            hash,
            description: "Description only",
            readme: "# Library\n<img src=https://example.invalid/track><script>alert(1)</script>",
            licenseFiles: { LICENSE: "Original license" },
            dependencies: this.conflict && id !== "Helper" ? [{ id: "Helper", version: id === "SVG" ? "1.0.0" : "2.0.0" }] : [],
            functions: this.missingOnOldTag && version === "0.9.0" ? functions.slice(0, 1) : functions,
            retrievedAt: "2026-10-23T00:00:00Z",
            fromCache: true,
            committedAt: null,
            metadata: { authors: "Original Author", tags: ["Shapes"], releaseNotes: "Changed <b>text</b>", projectUrl: null, repositoryUrl: null },
        }
    }
    postMessage(value: unknown): void {
        const request = value as Request
        this.requests.push(structuredClone(request))
        if (request.method === "udf.catalogRefresh" && this.holdRefresh) {
            this.pending = request
            return
        }
        if (request.method === "udf.catalogRefresh" && this.unconfirmed) {
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            this.mock.postMessage({ ...request, id: `internal-${request.id}` })
            this.send({ id: request.id, error: { code: "UDF_CATALOG_UNCONFIRMED", message: "unconfirmed commit" } })
            return
        }
        if (request.method === "udf.catalog") {
            const params = request.params,
                action = params.action
            if (action === "packages") {
                if (this.failList) {
                    this.send({ id: request.id, error: { code: "READ_FAILED", message: "page read failed" } })
                    return
                }
                const versions = (params.packageVersions ?? {}) as Record<string, string>
                if (this.oldTagRemoved && versions.SVG === "0.9.0") {
                    this.send({ id: request.id, error: { code: "UDF_VERSION_MISSING", message: "removed old tag" } })
                    return
                }
                const all: UdfCatalogPackage[] = ["SVG", "Time"]
                    .map((id) => {
                        const pkg = this.pkg(id, versions[id] ?? "1.0.0")
                        return {
                            id,
                            version: pkg.version,
                            hash,
                            description: pkg.description,
                            authors: "Original Author",
                            tags: ["Shapes"],
                            committedAt: null,
                            iconKey: this.withIcon ? hash : null,
                            functionCount: pkg.functions.length,
                        }
                    })
                    .filter((item) =>
                        [item.id, item.description, item.authors, ...item.tags, ...this.pkg(item.id, item.version).functions.map((fn) => fn.name)].some(
                            (text) => matchUdfName(text, String(params.query ?? "")) !== null,
                        ),
                    )
                const offset = Number(params.offset),
                    count = Math.min(Number(params.limit), 20),
                    items = all.slice(offset, offset + count)
                this.send({
                    id: request.id,
                    result: {
                        packagePage: {
                            items,
                            total: all.length,
                            nextOffset: offset + items.length < all.length ? offset + items.length : null,
                            status: this.status,
                        },
                    },
                })
                return
            }
            if (action === "versions") {
                this.send({ id: request.id, result: { versions: this.oldTagRemoved ? ["1.0.0"] : ["1.0.0", "0.9.0"] } })
                return
            }
            if (action === "icon") {
                this.send({
                    id: request.id,
                    result: {
                        icon: {
                            key: hash,
                            dataUrl: "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aKfoAAAAASUVORK5CYII=",
                        },
                    },
                })
                return
            }
            if (action === "package") {
                this.send({ id: request.id, result: { package: this.pkg(String(params.packageId), String(params.version)) } })
                return
            }
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        this.mock.postMessage(value)
    }
}
/** 先准备隔离缓存, 随后仅统计本次组件行为. */
async function fixture(transport = new FunctionTransport(), local?: LocalUdfDefinition) {
    const bridge = new BridgeClient(transport)
    await bridge.request("udf.catalogRefresh", { source: "daxlib" }).promise
    if (local) await bridge.request("udf.localSave", { definition: local }).promise
    transport.requests = []
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
    let closed = false
    const close = () => {
        if (closed) return
        closed = true
        wrapper.unmount()
        bridge.dispose()
        ElMessage.closeAll()
    }
    cleanup.push(close)
    return { transport, bridge, wrapper, close }
}
const button = (label: string) =>
    new DOMWrapper([...document.querySelectorAll<HTMLButtonElement>("button")].findLast((item) => item.textContent?.trim() === label)!)

it("初始按包读取摘要, 选择读取整包, 更改分页大小不清除整包选择", async () => {
    const { wrapper, transport, close } = await fixture()
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(2)
    expect(transport.requests.filter((item) => item.params.action === "packages").map((item) => item.params.offset)).toEqual([0])
    expect(transport.requests.some((item) => ["versions", "package"].includes(String(item.params.action)))).toBe(false)
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")[0]?.functions).toEqual(transport.pkg("SVG", "1.0.0").functions.map((fn) => fn.name))
    for (const size of [100, 200, 500]) {
        wrapper.findComponent(ElPagination).vm.$emit("update:page-size", size)
        // oxlint-disable-next-line no-await-in-loop -- 用户顺序切换分页大小, 同一状态不并发写入.
        await flushPromises()
        expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(2)
        expect(wrapper.findComponent(ElPagination).props("currentPage")).toBe(1)
        expect(wrapper.props("modelValue")[0]?.functions).toHaveLength(80)
    }
    close()
})

it("函数名、包名和描述搜索返回完整包, 搜索隐藏选择保持且不会刷新来源", async () => {
    const { wrapper, transport, close } = await fixture()
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    await wrapper.find(".udf-search input").setValue("sfn079")
    await button("搜索").trigger("click")
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(1)
    expect(wrapper.findComponent(UdfLibraryCard).props("pkg").id).toBe("SVG")
    await wrapper.find(".udf-search input").setValue("Time")
    await button("搜索").trigger("click")
    await flushPromises()
    expect(wrapper.findComponent(UdfLibraryCard).props("pkg").id).toBe("Time")
    expect(wrapper.props("modelValue")[0]?.functions).toHaveLength(80)
    await wrapper.find(".udf-search input").setValue("Description only")
    await button("搜索").trigger("click")
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(2)
    expect(transport.requests.some((item) => item.method === "udf.catalogRefresh")).toBe(false)
    close()
})

it("查看才读取整包详情, README安全渲染Markdown且复制精确完整代码", async () => {
    const { wrapper, transport, close } = await fixture()
    const originalWriteText = navigator.clipboard.writeText
    // 只模拟当前复制动作, 清理后恢复公共桩的默认拒绝边界.
    const write = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    cleanup.push(() => {
        write.mockRestore()
        expect(navigator.clipboard.writeText).toBe(originalWriteText)
    })
    wrapper.findComponent(UdfLibraryCard).vm.$emit("details")
    await flushPromises()
    expect(transport.requests.filter((item) => item.params.action === "package")).toHaveLength(1)
    const dialog = new DOMWrapper(document.querySelector<HTMLElement>(".udf-detail-dialog")!)
    expect(dialog.text()).toContain("Original Author")
    expect(dialog.text()).toContain("<img src=https://example.invalid/track>")
    expect(dialog.find(".udf-markdown h1").text()).toBe("Library")
    expect(dialog.find(".udf-markdown img,.udf-markdown script").exists()).toBe(false)
    await dialog.find(".code-toolbar button").trigger("click")
    expect(write).toHaveBeenCalledWith(transport.pkg("SVG", "1.0.0").functions[0]!.expression)
    close()
    vi.restoreAllMocks()
})

it("包卡片版本始终显示精确版本, 候选预发行禁止勾选且保留版本入口", async () => {
    const pkg: UdfCatalogPackage = {
        id: "Pkg",
        version: "1.2.3",
        hash,
        description: "Description",
        authors: "Author",
        tags: [],
        committedAt: null,
        iconKey: null,
        functionCount: 2,
    }
    const wrapper = mount(UdfLibraryCard, { props: { pkg, query: "", checked: false, disabled: false } })
    cleanup.push(() => wrapper.unmount())
    expect(wrapper.find(".udf-package-version button").text()).toBe("1.2.3")
    await wrapper.setProps({ pkg: { ...pkg, version: "2.0.0-beta" }, candidate: true })
    expect(wrapper.find(".udf-package-version button").text()).toBe("2.0.0-beta")
    expect(wrapper.find<HTMLInputElement>('input[type="checkbox"]').element.disabled).toBe(true)
    await wrapper.find(".udf-package-version button").trigger("click")
    expect(wrapper.emitted("versions")).toHaveLength(1)
})

it("官方目录同时展示和搜索本地函数, 选择携带本地精确ID与基线", async () => {
    const { wrapper, bridge, close } = await fixture(new FunctionTransport(), {
        schemaVersion: 1,
        name: "Local.Custom",
        version: "1.2.3",
        expression: "() => 9",
        description: "# Local documentation",
        dependencies: [],
    })
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(2)
    expect(wrapper.findAllComponents(UdfFunctionRow)).toHaveLength(1)
    expect(wrapper.find('[data-function="Local.Custom"]').exists()).toBe(true)
    await wrapper.find(".udf-search input").setValue("lcus")
    await button("搜索").trigger("click")
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(0)
    expect(wrapper.findAllComponents(UdfFunctionRow)).toHaveLength(1)
    await wrapper.find('[data-function="Local.Custom"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    const local = (await bridge.request("udf.localList", {}).promise).items[0]!
    expect(wrapper.props("modelValue")).toEqual([
        { source: "local", packageId: local.id, version: "1.2.3", functions: ["Local.Custom"], baseline: local.baseline },
    ])
    close()
})

it("防抖查询在读取中保留输入焦点和可编辑状态, 排空后仅展示最新查询", async () => {
    class HeldFunctions extends FunctionTransport {
        held?: Request
        hold = false
        override postMessage(value: unknown): void {
            const request = value as Request
            if (this.hold && request.params?.action === "packages") {
                this.held = request
                return
            }
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            super.postMessage(value)
        }
        release(): void {
            const request = this.held!
            this.hold = false
            this.held = undefined
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            super.postMessage(request)
        }
    }
    const transport = new HeldFunctions()
    const { wrapper, close } = await fixture(transport)
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] })
    const input = wrapper.find<HTMLInputElement>(".udf-search input")
    input.element.focus()
    const initialReads = transport.requests.filter((request) => request.params.action === "packages").length
    await input.setValue("SVG.Fn07")
    await vi.advanceTimersByTimeAsync(179)
    expect(transport.requests.filter((request) => request.params.action === "packages")).toHaveLength(initialReads)
    transport.hold = true
    await vi.advanceTimersByTimeAsync(1)
    expect(transport.held?.params.query).toBe("SVG.Fn07")
    expect(input.element.disabled).toBe(false)
    expect(document.activeElement).toBe(input.element)
    await input.setValue("Time.Fn040")
    transport.release()
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(2)
    await vi.advanceTimersByTimeAsync(180)
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard).map((row) => row.props("pkg").id)).toEqual(["Time"])
    expect(document.activeElement).toBe(input.element)
    close()
})

it("输入法组合期间不搜索, 结束后防抖一次, 切源与卸载清除待查询", async () => {
    const { wrapper, transport, close } = await fixture()
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] })
    const input = wrapper.find(".udf-search input")
    const reads = () => transport.requests.filter((request) => request.params.action === "packages").length
    const initial = reads()
    await input.trigger("compositionstart")
    await input.setValue("Time")
    await vi.advanceTimersByTimeAsync(300)
    expect(reads()).toBe(initial)
    await input.trigger("compositionend")
    await vi.advanceTimersByTimeAsync(180)
    await flushPromises()
    expect(reads()).toBe(initial + 1)
    await input.setValue("SVG")
    await wrapper.findComponent(ElSelect).setValue("local")
    wrapper.findComponent(ElSelect).vm.$emit("change", "local")
    await flushPromises()
    const before = reads()
    close()
    await vi.advanceTimersByTimeAsync(500)
    expect(reads()).toBe(before)
})

it("防抖到时遇到正在核验的选择会排空后继续查询, 不丢输入或选择", async () => {
    class HeldPackage extends FunctionTransport {
        held?: Request
        override postMessage(value: unknown): void {
            const request = value as Request
            if (request.params?.action === "package" && !this.held) {
                this.held = request
                return
            }
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            super.postMessage(value)
        }
        release(): void {
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            super.postMessage(this.held!)
        }
    }
    const transport = new HeldPackage()
    const { wrapper, close } = await fixture(transport)
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] })
    await wrapper.find(".udf-search input").setValue("Time.Fn040")
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await vi.advanceTimersByTimeAsync(180)
    expect(transport.held?.params.packageId).toBe("SVG")
    transport.release()
    await flushPromises()
    await vi.advanceTimersByTimeAsync(180)
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard).map((row) => row.props("pkg").id)).toEqual(["Time"])
    expect(wrapper.props("modelValue")[0]?.functions).toEqual(transport.pkg("SVG", "1.0.0").functions.map((fn) => fn.name))
    close()
})

it("同一包图标只读取和解码一次, 函数行共用短对象URL, 切来源后回收", async () => {
    const create = vi.fn((_blob: Blob) => "blob:udf-verified-icon"),
        revoke = vi.fn((_url: string) => undefined)
    const OriginalURL = URL
    vi.stubGlobal(
        "URL",
        class extends OriginalURL {
            static createObjectURL = create
            static revokeObjectURL = revoke
        },
    )
    const transport = new FunctionTransport()
    transport.withIcon = true
    const { wrapper, close } = await fixture(transport)
    expect(transport.requests.filter((item) => item.params.action === "icon")).toHaveLength(1)
    expect(create).toHaveBeenCalledOnce()
    expect(wrapper.html()).not.toContain("data:image/png;base64")
    expect(wrapper.find('img[src="blob:udf-verified-icon"]').attributes("loading")).toBe("lazy")
    wrapper.findComponent(ElPagination).vm.$emit("update:current-page", 2)
    await flushPromises()
    expect(transport.requests.filter((item) => item.params.action === "icon")).toHaveLength(1)
    const source = wrapper.findComponent(ElSelect)
    await source.setValue("local")
    source.vm.$emit("change", "local")
    await flushPromises()
    expect(revoke).toHaveBeenCalledWith("blob:udf-verified-icon")
    close()
})

it("整包历史 tag 替换该版本全部函数, 切源与重开保留精确版本和选择", async () => {
    const { wrapper, bridge, transport, close } = await fixture()
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    transport.missingOnOldTag = true
    await wrapper.findComponent(UdfLibraryCard).find(".udf-package-version button").trigger("click")
    await flushPromises()
    await wrapper.findComponent(UdfTagDialog).findComponent(ElSelect).setValue("0.9.0")
    await button("确认版本").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")[0]).toMatchObject({ version: "0.9.0", baseline: hash, functions: ["SVG.Fn000"] })
    const source = wrapper.findComponent(ElSelect)
    await source.setValue("local")
    source.vm.$emit("change", "local")
    await flushPromises()
    await source.setValue("daxlib")
    source.vm.$emit("change", "daxlib")
    await flushPromises()
    expect(wrapper.findComponent(UdfLibraryCard).props("pkg").version).toBe("0.9.0")
    expect(wrapper.findComponent(UdfLibraryCard).find(".udf-package-version button").text()).toBe("0.9.0")
    const saved = JSON.parse(JSON.stringify(wrapper.props("modelValue"))) as UdfSelection[]
    wrapper.unmount()
    const reopened = mount(UdfStep, { attachTo: document.body, props: { bridge: markRaw(bridge), disabled: false, modelValue: saved } })
    await flushPromises()
    expect(reopened.findComponent(UdfLibraryCard).props("pkg").version).toBe("0.9.0")
    expect(reopened.findComponent(UdfLibraryCard).props("checked")).toBe(true)
    reopened.unmount()
    close()
})

it("本页批量的依赖冲突整批拒绝, 不保留先成功的库", async () => {
    const transport = new FunctionTransport()
    transport.conflict = true
    const { wrapper, close } = await fixture(transport)
    wrapper.findComponent(ElPagination).vm.$emit("update:page-size", 200)
    await flushPromises()
    await button("选择本页函数包").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([])
    expect(document.body.textContent).toContain("依赖预览缺失、循环、版本冲突")
    close()
})

it("手动更新立即明确反馈, 小数百分比显示整数, 权威成功仅提示一次", async () => {
    const { wrapper, transport, close } = await fixture()
    transport.holdRefresh = true
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(document.querySelector(".operation-message")?.textContent).toContain("正在拉取最新函数库")
    transport.send({ event: { requestId: transport.pending!.id, sequence: 1, type: "scaffoldProgress", payload: { percent: 12.987, canCancel: true } } })
    await flushPromises()
    expect(document.querySelector('[role="progressbar"]')?.getAttribute("aria-valuenow")).toBe("12")
    expect(document.querySelector(".operation-message")?.textContent).not.toContain("12.987")
    transport.holdRefresh = false
    // oxlint-disable-next-line unicorn/require-post-message-target-origin
    transport.mock.postMessage(transport.pending!)
    await flushPromises()
    expect(document.querySelector(".operation-message")).toBeNull()
    expect(document.querySelectorAll(".el-message--success")).toHaveLength(1)
    expect(document.body.textContent).toContain("函数库更新成功")
    close()
})

it("缓存发布成功后列表失败明确区分, 不残留可操作旧行", async () => {
    const { wrapper, transport, close } = await fixture()
    transport.failList = true
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(0)
    expect(document.body.textContent).toContain("缓存操作已成功, 但列表读取失败")
    close()
})

it("新缓存已移除旧tag时保留旧选择供核验, 不用旧tag阻塞整个最新函数页", async () => {
    const { wrapper, transport, close } = await fixture()
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    await wrapper.findComponent(UdfLibraryCard).find(".udf-package-version button").trigger("click")
    await flushPromises()
    await wrapper.findComponent(UdfTagDialog).findComponent(ElSelect).setValue("0.9.0")
    await button("确认版本").trigger("click")
    await flushPromises()
    const original = JSON.parse(JSON.stringify(wrapper.props("modelValue"))) as UdfSelection[]
    transport.oldTagRemoved = true
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.findAllComponents(UdfLibraryCard)).toHaveLength(2)
    expect(wrapper.props("modelValue")).toEqual(original)
    expect(wrapper.emitted("validity")?.at(-1)).toEqual([false])
    const latestQuery = transport.requests.findLast((item) => item.params.action === "packages")!
    expect(latestQuery.params.packageVersions).not.toMatchObject({ SVG: "0.9.0" })
    close()
})

it("未确认提交仅只读核验当前状态, 不重放更新或读取历史", async () => {
    const { wrapper, transport, close } = await fixture()
    transport.unconfirmed = true
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(transport.requests.filter((item) => item.method === "udf.catalogRefresh")).toHaveLength(1)
    expect(transport.requests.some((item) => item.params.action === "status")).toBe(true)
    expect(transport.requests.some((item) => item.params.action === "history")).toBe(false)
    expect(transport.requests.some((item) => item.method === "udf.catalogRollback")).toBe(false)
    expect(document.body.textContent).toContain("已通过只读核验取得当前缓存状态")
    close()
})

it("仅保留当前缓存入口, 刷新仍保留包版本和本地函数", async () => {
    const { wrapper, bridge, transport, close } = await fixture()
    const local = await bridge.request("udf.localSave", {
        definition: { schemaVersion: 1, name: "Local.Custom", version: "1.0.0", expression: "() => 9", description: "", dependencies: [] },
    }).promise
    await wrapper.find('[data-test="udf-refresh"]').trigger("click")
    await flushPromises()
    expect(wrapper.text()).not.toContain("缓存历史")
    expect(transport.requests.some((item) => item.params.action === "history")).toBe(false)
    const tags = await bridge.request("udf.catalog", { action: "versions", packageId: "SVG" }).promise
    expect(tags.versions).toContain("0.9.0")
    expect(await bridge.request("udf.localGet", { id: local.id }).promise).toEqual(local)
    close()
})

it("本页全选在第二包等待期间取消, 迟到成功也不发布部分选择", async () => {
    class HeldSecondPackage extends FunctionTransport {
        held?: Request
        override postMessage(value: unknown): void {
            const request = value as Request
            if (request.params?.action === "package" && request.params.packageId === "Time") {
                this.held = request
                return
            }
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            super.postMessage(value)
        }
    }
    const transport = new HeldSecondPackage(),
        { wrapper, close } = await fixture(transport)
    await button("选择本页函数包").trigger("click")
    await flushPromises()
    expect(transport.held?.params.packageId).toBe("Time")
    expect(wrapper.props("modelValue")).toEqual([])
    await new DOMWrapper(document.querySelector<HTMLButtonElement>(".operation-message button")!).trigger("click")
    await flushPromises()
    transport.send({ id: transport.held!.id, result: { package: transport.pkg("Time", "1.0.0") } })
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([])
    expect(document.querySelector(".operation-message")).toBeNull()
    close()
})

it("详情与卡片精确hash不一致时拒绝打开, 不改变已有选择", async () => {
    class ChangedPackage extends FunctionTransport {
        override pkg(id: string, version: string): UdfPackage {
            return { ...super.pkg(id, version), hash: "d".repeat(64) }
        }
    }
    const { wrapper, close } = await fixture(new ChangedPackage())
    wrapper.findComponent(UdfLibraryCard).vm.$emit("details")
    await flushPromises()
    expect(document.querySelector(".udf-detail-dialog")).toBeNull()
    expect(wrapper.props("modelValue")).toEqual([])
    close()
})

it("包搜索隐藏本地函数后选择整包, 保留本地精确选择", async () => {
    const { wrapper, close } = await fixture(new FunctionTransport(), {
        schemaVersion: 1,
        name: "Local.Keep",
        version: "1.0.0",
        expression: "() => 1",
        description: "",
        dependencies: [],
    })
    await wrapper.find('[data-function="Local.Keep"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    const local = wrapper.props("modelValue")[0]!
    await wrapper.find(".udf-search input").setValue("SVG")
    await button("搜索").trigger("click")
    await flushPromises()
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")).toHaveLength(2)
    expect(wrapper.props("modelValue")[0]).toEqual(local)
    expect(wrapper.props("modelValue")[1]?.functions).toHaveLength(80)
    close()
})

it("明确选择预发行版本开启显示, 关闭显示只移除查询覆盖并保留已选版本", async () => {
    class BetaVersions extends FunctionTransport {
        override postMessage(value: unknown): void {
            const request = value as Request
            if (request.params?.action === "versions") {
                this.send({ id: request.id, result: { versions: ["1.0.0", "2.0.0-beta"] } })
                return
            }
            // oxlint-disable-next-line unicorn/require-post-message-target-origin
            super.postMessage(value)
        }
    }
    const { wrapper, transport, close } = await fixture(new BetaVersions())
    await wrapper.findComponent(UdfLibraryCard).find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    await wrapper.findComponent(UdfLibraryCard).find(".udf-package-version button").trigger("click")
    await flushPromises()
    await wrapper.findComponent(UdfTagDialog).findComponent(ElSelect).setValue("2.0.0-beta")
    expect(document.querySelector(".udf-tag-dialog .el-checkbox__original")).toBeNull()
    await button("确认版本").trigger("click")
    await flushPromises()
    const before = JSON.parse(JSON.stringify(wrapper.props("modelValue"))) as UdfSelection[]
    expect(before[0]).toMatchObject({ version: "2.0.0-beta" })
    const display = wrapper.find<HTMLInputElement>(".udf-search input[type=checkbox]")
    expect(display.element.checked).toBe(true)
    await display.setValue(false)
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual(before)
    const query = transport.requests.findLast((request) => request.params.action === "packages")!
    expect(query.params.includePrerelease).toBe(false)
    expect(query.params.packageVersions).not.toHaveProperty("SVG")
    expect(wrapper.findComponent(UdfLibraryCard).props("pkg").version).toBe("1.0.0")
    close()
})
