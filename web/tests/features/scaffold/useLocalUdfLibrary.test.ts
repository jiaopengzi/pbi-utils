/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\useLocalUdfLibrary.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地写入回执即时同步、后续读取失败保护及本页选择的原子依赖核验.
 */
import { afterEach, expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { defineComponent, h, ref, watch, type ModelRef } from "vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { useUdfState, type UdfState } from "@/composables/features/scaffold/useUdfState"
import { useUdfRequests } from "@/composables/features/scaffold/useUdfRequests"
import { useUdfCatalog } from "@/composables/features/scaffold/useUdfCatalog"
import { useLocalUdfLibrary, type LocalUdfLibrary } from "@/composables/features/scaffold/useLocalUdfLibrary"
import { useUdfBulkImport, type UdfBulkImport } from "@/composables/features/scaffold/useUdfBulkImport"
import type { UdfRequests } from "@/composables/features/scaffold/useUdfRequests"
import type { LocalUdfDefinition, UdfSelection } from "@/features/scaffold/contracts"

type Request = { id: string; method: string; params: Record<string, unknown> }
const cleanup: (() => void)[] = []
afterEach(() => cleanup.splice(0).forEach((close) => close()))

/** 只暂停本地全量复读, 写入仍由既有模拟宿主产生真实协议回执. */
class LocalRefreshTransport extends MockTransport {
    holdList = false
    holdGetId?: string
    held?: Request
    requests: Request[] = []
    receivers = new Set<(event: MessageEvent) => void>()
    override addEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.receivers.add(listener)
        super.addEventListener(type, listener)
    }
    override removeEventListener(type: "message", listener: (event: MessageEvent) => void): void {
        this.receivers.delete(listener)
        super.removeEventListener(type, listener)
    }
    override postMessage(value: unknown): void {
        const request = value as Request
        this.requests.push(structuredClone(request))
        if ((this.holdList && request.method === "udf.localList") || (request.method === "udf.localGet" && request.params.id === this.holdGetId)) {
            this.held = request
            return
        }
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        super.postMessage(value)
    }
    /** 模拟成功写入后的独立复读失败, 不把失败伪装为写入回滚. */
    failRead(code = "LOCAL_READ_FAILED", message = "本地列表读取失败"): void {
        for (const receiver of this.receivers)
            receiver(
                new MessageEvent("message", {
                    data: { protocolVersion: 1, id: this.held!.id, error: { code, message } },
                }),
            )
        this.held = undefined
    }
}

/** 挂载真实请求串行和目录核验器; 外层选择仍由独立 ref 持有. */
function fixture() {
    const transport = new LocalRefreshTransport(),
        bridge = new BridgeClient(transport)
    let state!: UdfState, local!: LocalUdfLibrary, bulk!: UdfBulkImport, requests!: UdfRequests
    const disabled = ref(false)
    const wrapper = mount(
        defineComponent({
            setup() {
                state = useUdfState(bridge, ref<UdfSelection[]>([]) as ModelRef<UdfSelection[]>, () => disabled.value)
                requests = useUdfRequests(state, () => undefined)
                const catalog = useUdfCatalog(state, requests)
                local = useLocalUdfLibrary(state, requests, catalog.commitSelection)
                bulk = useUdfBulkImport(state, requests)
                return () => h("div")
            },
        }),
    )
    cleanup.push(() => {
        wrapper.unmount()
        bridge.dispose()
    })
    return { state, local, bulk, requests, disabled, bridge, transport, wrapper }
}

/** 构造宿主接受的本地定义, 不读取用户资料. */
function definition(name: string, dependencies: LocalUdfDefinition["dependencies"] = []): LocalUdfDefinition {
    return { schemaVersion: 1, name, version: "1.0.0", expression: "() => 1", description: "", dependencies }
}

it("新增及编辑回执立即同步可用函数, 不等全量复读且复读失败保留已保存定义", async () => {
    const { state, local, transport } = fixture()
    Object.assign(state.definition, definition("Local.First"))
    state.editor.value = true
    transport.holdList = true
    const adding = local.save()
    await flushPromises()
    expect(transport.held?.method).toBe("udf.localList")
    expect(state.locals.value.map((item) => item.definition.name)).toEqual(["Local.First"])
    const original = state.locals.value[0]!
    transport.failRead()
    await adding
    expect(state.locals.value[0]).toEqual(original)
    expect(state.error.value).toBe("本地列表读取失败")
    await local.toggleLocal(original, true)
    expect(state.model.value[0]).toMatchObject({ packageId: original.id, baseline: original.baseline })
    state.target.value = original
    state.editor.value = true
    Object.assign(state.definition, definition("Local.Renamed"), { version: "1.1.0" })
    const editing = local.save()
    await flushPromises()
    expect(state.locals.value).toHaveLength(1)
    expect(state.locals.value[0]).toMatchObject({ id: original.id, definition: { name: "Local.Renamed", version: "1.1.0" } })
    expect(state.locals.value[0]!.baseline).not.toBe(original.baseline)
    expect(state.model.value).toEqual([])
    transport.failRead()
    await editing
})

it("删除回执立即移除可用行和选择, 复读失败不能恢复已删除条目", async () => {
    const { state, local, bridge, transport } = fixture()
    const entry = await bridge.request("udf.localSave", { definition: definition("Local.Delete") }).promise
    await local.listLocal()
    await local.toggleLocal(entry, true)
    state.deleteTarget.value = entry
    transport.holdList = true
    const removing = local.remove()
    await flushPromises()
    expect(transport.held?.method).toBe("udf.localList")
    expect(state.locals.value).toEqual([])
    expect(state.model.value).toEqual([])
    transport.failRead()
    await removing
    expect(state.locals.value).toEqual([])
})

it("写入基线冲突保留原条目、已选项和编辑草稿, 不以未提交定义刷新列表", async () => {
    const { state, local, bridge } = fixture()
    const entry = await bridge.request("udf.localSave", { definition: definition("Local.Conflict") }).promise
    await local.listLocal()
    await local.toggleLocal(entry, true)
    const before = JSON.parse(JSON.stringify(state.model.value)) as UdfSelection[]
    await bridge.request("udf.localSave", { id: entry.id, baseline: entry.baseline, definition: { ...entry.definition, version: "2.0.0" } }).promise
    state.target.value = entry
    state.editor.value = true
    Object.assign(state.definition, definition("Local.Uncommitted"))
    await local.save()
    expect(state.locals.value).toEqual([entry])
    expect(state.model.value).toEqual(before)
    expect(state.editor.value).toBe(true)
    expect(state.definition.name).toBe("Local.Uncommitted")
    state.deleteTarget.value = entry
    await local.remove()
    expect(state.locals.value).toEqual([entry])
    expect(state.model.value).toEqual(before)
    expect(state.deleteTarget.value).toEqual(entry)
})

it("本页选择一次合并精确 ID 和基线, 保留隐藏及其他来源选择", async () => {
    const { state, local, bridge, transport } = fixture()
    const a = await bridge.request("udf.localSave", { definition: definition("Local.A") }).promise
    const b = await bridge.request("udf.localSave", { definition: definition("Local.B") }).promise
    const hidden = await bridge.request("udf.localSave", { definition: definition("Local.Hidden") }).promise
    await local.toggleLocal(hidden, true)
    await local.toggleLocal(a, true)
    const updated = await bridge.request("udf.localSave", { id: a.id, baseline: a.baseline, definition: { ...a.definition, version: "1.2.0" } }).promise
    await bridge.request("udf.catalogRefresh", { source: "daxlib" }).promise
    const status = (await bridge.request("udf.catalog", { action: "status", source: "daxlib" }).promise).status!
    const pkg = (await bridge.request("udf.catalog", { action: "package", source: "daxlib", packageId: "mock.sample", version: "1.0.0" }).promise).package!
    state.statuses.value = { daxlib: status }
    const remote = {
        source: "daxlib",
        packageId: pkg.id,
        version: pkg.version,
        baseline: pkg.hash,
        catalogBaseline: status.baseline,
        functions: [pkg.functions[0]!.name],
    }
    state.model.value = [...state.model.value, remote]
    transport.requests = []
    await local.selectPage([updated, b, b])
    expect(state.model.value).toHaveLength(4)
    expect(state.model.value).toContainEqual(remote)
    expect(state.model.value).toContainEqual({
        source: "local",
        packageId: hidden.id,
        version: hidden.definition.version,
        baseline: hidden.baseline,
        functions: [hidden.definition.name],
    })
    expect(state.model.value.find((item) => item.packageId === a.id)).toMatchObject({ version: "1.2.0", baseline: updated.baseline })
    expect(transport.requests.filter((item) => item.method === "udf.localGet")).toHaveLength(3)
})

it("同页本地函数的冲突整批拒绝, 锁定和空页不读取且不修改选择", async () => {
    const { state, local, bridge, transport } = fixture()
    const a = await bridge.request("udf.localSave", { definition: definition("Local.Collision") }).promise
    const b = await bridge.request("udf.localSave", { definition: { ...definition("Local.Collision"), version: "2.0.0" } }).promise
    await local.selectPage([a, b])
    expect(state.model.value).toEqual([])
    expect(state.error.value).toContain("依赖预览缺失、循环、版本冲突")
    transport.requests = []
    state.busy.value = true
    await local.selectPage([a])
    state.busy.value = false
    await local.selectPage([])
    expect(transport.requests).toEqual([])
    expect(state.model.value).toEqual([])
})

it("同逻辑名切换tag原子替换旧GUID, 未选包只记录当前tag且管理器模态仍可切换", async () => {
    const { state, local, bridge, transport, disabled } = fixture()
    const first = await bridge.request("udf.localSave", { definition: definition("Local.Versioned") }).promise
    const second = await bridge.request("udf.localSave", { definition: { ...first.definition, name: "LOCAL.versioned", version: "2.0.0" } }).promise
    await local.listLocal()
    transport.requests = []
    await local.changeVersion(first, second)
    expect(state.localPackageTags.value["local.versioned"]).toBe(first.id)
    expect(state.model.value).toEqual([])
    expect(transport.requests).toEqual([])
    await local.toggleLocal(first, true)
    state.manager.value = true
    disabled.value = true
    await local.changeVersion(second, first)
    expect(state.model.value).toHaveLength(1)
    expect(state.model.value[0]).toMatchObject({ packageId: second.id, version: "2.0.0", baseline: second.baseline })
    expect(state.localPackageTags.value["local.versioned"]).toBe(second.id)
})

it("同名新tag依赖失败和取消保留旧选择、当前tag及编辑草稿", async () => {
    const { state, local, bridge, transport, requests } = fixture()
    const first = await bridge.request("udf.localSave", { definition: definition("Local.Safe") }).promise
    const broken = await bridge.request("udf.localSave", {
        definition: {
            ...first.definition,
            version: "2.0.0",
            dependencies: [{ source: "local", packageId: "0".repeat(32), version: "1.0.0", function: "Local.Missing", baseline: null }],
        },
    }).promise
    const third = await bridge.request("udf.localSave", { definition: { ...first.definition, version: "3.0.0" } }).promise
    await local.listLocal()
    await local.toggleLocal(first, true)
    const before = JSON.parse(JSON.stringify(state.model.value)) as UdfSelection[]
    Object.assign(state.definition, definition("Draft.Keep"))
    await local.changeVersion(broken, first)
    expect(state.model.value).toEqual(before)
    expect(state.localPackageTags.value["local.safe"]).toBe(first.id)
    expect(state.definition.name).toBe("Draft.Keep")
    transport.holdGetId = third.id
    const changing = local.changeVersion(third, first)
    await flushPromises()
    await requests.cancel()
    transport.failRead("CANCELLED", "取消")
    await changing
    expect(state.model.value).toEqual(before)
    expect(state.localPackageTags.value["local.safe"]).toBe(first.id)
    expect(state.definition.name).toBe("Draft.Keep")
})

it("模拟宿主拒绝同名同tag的新建和更新, 保留原精确GUID和内容", async () => {
    const { bridge } = fixture()
    const first = await bridge.request("udf.localSave", { definition: definition("Local.Unique") }).promise
    const second = await bridge.request("udf.localSave", { definition: { ...first.definition, version: "2.0.0" } }).promise
    await expect(bridge.request("udf.localSave", { definition: { ...first.definition, name: "LOCAL.unique" } }).promise).rejects.toMatchObject({
        code: "UDF_LOCAL_VERSION_EXISTS",
    })
    await expect(bridge.request("udf.localSave", { id: second.id, baseline: second.baseline, definition: first.definition }).promise).rejects.toMatchObject({
        code: "UDF_LOCAL_VERSION_EXISTS",
    })
    expect((await bridge.request("udf.localList", {}).promise).items).toEqual([first, second])
})

it("本地全来源每名只预览一个当前tag, 显式展示tag优先于旧选择并保留精确依赖身份", async () => {
    const { state, local, bulk, bridge } = fixture()
    const first = await bridge.request("udf.localSave", { definition: definition("Local.Bulk") }).promise
    const newest = await bridge.request("udf.localSave", { definition: { ...first.definition, version: "2.0.0" } }).promise
    const beta = await bridge.request("udf.localSave", { definition: { ...definition("Local.Beta"), version: "1.0.0-beta" } }).promise
    await local.listLocal()
    state.source.value = "local"
    state.sources.value = [{ id: "local", name: "Local", baseUrl: null, baseline: null, kind: "local", protocol: "git" }]
    await local.toggleLocal(first, true)
    state.localPackageTags.value["local.bulk"] = newest.id
    await bulk.prepare()
    expect(bulk.ready.value).toBe(true)
    expect(bulk.entries.value.map((entry) => entry.selection.packageId).toSorted()).toEqual([newest.id, beta.id].toSorted())
    await bulk.confirm()
    expect(state.model.value).toHaveLength(2)
    expect(state.model.value.find((entry) => entry.functions[0] === "Local.Bulk")?.packageId).toBe(newest.id)
})

it("未选本地包全量预览优先当前tag, 没有草稿才使用最新稳定版", async () => {
    const { state, local, bulk, bridge } = fixture()
    const first = await bridge.request("udf.localSave", { definition: definition("Local.Current") }).promise
    const newest = await bridge.request("udf.localSave", { definition: { ...first.definition, version: "2.0.0" } }).promise
    await bridge.request("udf.localSave", { definition: { ...first.definition, version: "3.0.0-beta" } }).promise
    await local.listLocal()
    state.source.value = "local"
    state.sources.value = [{ id: "local", name: "Local", baseUrl: null, baseline: null, kind: "local", protocol: "git" }]
    state.localPackageTags.value = { "local.current": first.id }
    await bulk.prepare()
    expect(bulk.entries.value.map((entry) => entry.selection.packageId)).toEqual([first.id])
    state.bulkImport.value = false
    state.localPackageTags.value = {}
    await bulk.prepare()
    expect(bulk.entries.value.map((entry) => entry.selection.packageId)).toEqual([newest.id])
})

it("新增较低tag立即定位权威保存版本, 重复tag失败保留原记录和草稿", async () => {
    const { state, local, bridge } = fixture()
    const newest = await bridge.request("udf.localSave", { definition: { ...definition("Local.SaveTag"), version: "2.0.0" } }).promise
    await local.listLocal()
    Object.assign(state.definition, definition("Local.SaveTag"))
    state.editor.value = true
    await local.save()
    const saved = state.locals.value.find((entry) => entry.id !== newest.id)!
    expect(saved.definition.version).toBe("1.0.0")
    expect(state.localPackageTags.value["local.savetag"]).toBe(saved.id)
    const before = JSON.parse(JSON.stringify(state.locals.value))
    state.editor.value = true
    const errors: string[] = []
    const stop = watch(
        state.error,
        (message) => {
            if (message) errors.push(message)
        },
        { flush: "sync" },
    )
    await local.save()
    stop()
    expect(state.locals.value).toEqual(before)
    expect(state.editor.value).toBe(true)
    expect(state.definition.version).toBe("1.0.0")
    expect(state.lastErrorCode.value).toBe("UDF_LOCAL_VERSION_EXISTS")
    expect(errors).toEqual([state.error.value])
})
