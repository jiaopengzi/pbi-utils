/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfDetailScroll.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 包详情和本地详情按精确身份重新从起点阅读, 同包语言与授权更新保留位置.
 */
import { afterEach, expect, it } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import UdfPackageDetailDialog from "@/features/scaffold/UdfPackageDetailDialog.vue"
import UdfLocalBrowser from "@/features/scaffold/UdfLocalBrowser.vue"
import { setUiSettings } from "@/i18n"
import type { LocalUdfEntry, UdfPackage } from "@/features/scaffold/contracts"
const cleanup: (() => void)[] = []
afterEach(() => cleanup.splice(0).forEach((close) => close()))
/** 构造完整包事实, 复用真实安全Markdown和只读代码组件. */
function pkg(id = "Package.A"): UdfPackage {
    return {
        id,
        committedAt: null,
        version: "1.0.0",
        hash: "a".repeat(64),
        description: "Description",
        readme: "# Documentation\n\n" + "Long document\n\n".repeat(30),
        functions: [{ name: `${id}.Function`, expression: "() => 1", description: "", annotations: {} }],
        dependencies: [],
        licenseFiles: {},
        retrievedAt: "2026-10-24T00:00:00Z",
        fromCache: true,
    }
}
function content(): HTMLElement {
    return document.querySelector<HTMLElement>(".udf-detail-dialog .el-dialog__body")!
}
it.each(["id", "version", "hash"] as const)("包%s变化重置真实正文, 已开窗的异步详情也从起点阅读", async (field) => {
    const value = pkg(),
        wrapper = mount(UdfPackageDetailDialog, { attachTo: document.body, props: { modelValue: true, details: value } })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    const original = content()
    original.scrollTop = 900
    original.scrollLeft = 42
    const next = { ...value, [field]: field === "hash" ? "b".repeat(64) : field === "version" ? "2.0.0" : "Package.B" }
    await wrapper.setProps({ details: next })
    await flushPromises()
    expect(content()).toBe(original)
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
})
it("关闭A后打开B以及同包重新打开均重置, 同包语言切换和对象重建保留阅读位置", async () => {
    const value = pkg(),
        wrapper = mount(UdfPackageDetailDialog, { attachTo: document.body, props: { modelValue: true, details: value } })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    const original = content()
    original.scrollTop = 500
    original.scrollLeft = 15
    await wrapper.setProps({ details: { ...value } })
    setUiSettings({ locale: "en-us", showRecentFiles: true, daxFormatStyle: "long" })
    await flushPromises()
    expect([original.scrollTop, original.scrollLeft]).toEqual([500, 15])
    await wrapper.setProps({ modelValue: false })
    await wrapper.setProps({ modelValue: true, details: pkg("Package.B") })
    await flushPromises()
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
    original.scrollTop = 300
    original.scrollLeft = 9
    await wrapper.setProps({ modelValue: false })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
})
it("尚无详情时打开, 正文迟到和快速A到B切换只重置当前包", async () => {
    const wrapper = mount(UdfPackageDetailDialog, { attachTo: document.body, props: { modelValue: true } })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    const original = content()
    original.scrollTop = 100
    original.scrollLeft = 7
    await wrapper.setProps({ details: pkg() })
    await flushPromises()
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
    original.scrollTop = 200
    original.scrollLeft = 8
    const a = wrapper.setProps({ details: pkg("Package.A2") }),
        b = wrapper.setProps({ details: pkg("Package.B") })
    await Promise.all([a, b])
    await flushPromises()
    expect(original.textContent).toContain("Package.B.Function")
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
})
it("本地详情复用正文时切函数和重新打开当前tag均归零", async () => {
    const items: LocalUdfEntry[] = ["A", "B"].map((name) => ({
        id: name,
        baseline: name.repeat(64),
        definition: { schemaVersion: 1, name: `Local.${name}`, version: "1.0.0", expression: "() => 1", description: "Documentation", dependencies: [] },
    }))
    const wrapper = mount(UdfLocalBrowser, { attachTo: document.body, props: { items, selections: [], query: "", disabled: false } })
    cleanup.push(() => wrapper.unmount())
    await wrapper.get('[data-function="Local.A"] button').trigger("click")
    await flushPromises()
    const original = content()
    original.scrollTop = 400
    original.scrollLeft = 21
    await new DOMWrapper(document.querySelector<HTMLButtonElement>(".udf-detail-dialog .el-dialog__footer button")!).trigger("click")
    await wrapper.get('[data-function="Local.B"] button').trigger("click")
    await flushPromises()
    expect(content()).toBe(original)
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
    original.scrollTop = 250
    original.scrollLeft = 10
    await new DOMWrapper(document.querySelector<HTMLButtonElement>(".udf-detail-dialog .el-dialog__footer button")!).trigger("click")
    await wrapper.get('[data-function="Local.B"] button').trigger("click")
    await flushPromises()
    expect([original.scrollTop, original.scrollLeft]).toEqual([0, 0])
})
