/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfLocalPackageBrowsing.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地同名函数包的版本切换、当前页选择和受控回滚边界.
 */
import { afterEach, expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { ElSelect } from "element-plus"
import UdfLocalBrowser from "@/features/scaffold/UdfLocalBrowser.vue"
import type { LocalUdfEntry, UdfSelection } from "@/features/scaffold/contracts"
const cleanup: (() => void)[] = []
afterEach(() => cleanup.splice(0).forEach((close) => close()))
/** 生成同名版本, GUID和基线始终分别保留. */
function entry(id: string, version: string, name = "Local.Calc"): LocalUdfEntry {
    return {
        id,
        baseline: id.repeat(64).slice(0, 64),
        definition: { schemaVersion: 1, name, version, expression: `() => ${id.length}`, description: "# Local documentation", dependencies: [] },
    }
}
const old = entry("a", "1.9.0"),
    latest = entry("b", "1.10.0"),
    beta = entry("c", "2.0.0-beta")
/** 精确版本选择仍使用宿主GUID, 不把函数名发到模型契约的packageId中. */
function selection(item: LocalUdfEntry): UdfSelection {
    return {
        source: "local",
        packageId: item.id,
        version: item.definition.version,
        functions: [item.definition.name],
        baseline: item.baseline,
    }
}
function fixture(selections: UdfSelection[] = []) {
    const wrapper = mount(UdfLocalBrowser, { props: { items: [old, latest, beta], selections, query: "", disabled: false } })
    cleanup.push(() => wrapper.unmount())
    return wrapper
}
it.each([false, true])("同名不同tag在embedded=%s只显示一个包, 默认最新稳定且本页只发当前tag", async (embedded) => {
    const wrapper = fixture()
    await wrapper.setProps({ embedded })
    expect(wrapper.findAll(".udf-function-row")).toHaveLength(1)
    const picker = wrapper.findComponent(ElSelect)
    expect(picker.props("modelValue")).toBe(latest.id)
    expect(picker.findAllComponents({ name: "ElOption" }).map((option) => option.props("label"))).toEqual(["2.0.0-beta", "1.10.0", "1.9.0"])
    await wrapper.get(".udf-page-actions button").trigger("click")
    expect(wrapper.emitted("selectPage")?.at(-1)).toEqual([[latest]])
})
it("已选旧tag优先; 失败无新选择时tag保持旧值, 成功受控更新后才切换", async () => {
    const wrapper = fixture([selection(old)])
    const picker = wrapper.findComponent(ElSelect)
    expect(picker.props("modelValue")).toBe(old.id)
    await picker.setValue(latest.id)
    expect(wrapper.emitted("changeVersion")?.at(-1)).toEqual([latest, old])
    expect(picker.props("modelValue")).toBe(old.id)
    await wrapper.setProps({ selections: [selection(latest)] })
    expect(picker.props("modelValue")).toBe(latest.id)
})
it("未选包允许独立tag草稿, 删除当前tag保留同名包并回退存活版本", async () => {
    const wrapper = fixture()
    await wrapper.findComponent(ElSelect).setValue(old.id)
    expect(wrapper.findComponent(ElSelect).props("modelValue")).toBe(old.id)
    await wrapper.get(".udf-page-actions button").trigger("click")
    expect(wrapper.emitted("selectPage")?.at(-1)).toEqual([[old]])
    await wrapper.setProps({ items: [latest, beta] })
    expect(wrapper.findAll(".udf-function-row")).toHaveLength(1)
    expect(wrapper.findComponent(ElSelect).props("modelValue")).toBe(latest.id)
})
it("本地详情全宽1200且不重复显示依赖元数据, 禁用拒绝迟到tag事件", async () => {
    const wrapper = fixture()
    await wrapper.get('[data-function="Local.Calc"] button').trigger("click")
    expect(wrapper.findComponent({ name: "UiDialog" }).props("width")).toBe("min(1200px, calc(100vw - 32px))")
    expect(wrapper.text()).not.toContain("声明依赖")
    await wrapper.setProps({ disabled: true })
    wrapper.findComponent(ElSelect).vm.$emit("update:modelValue", old.id)
    wrapper.findComponent(ElSelect).vm.$emit("change", old.id)
    expect(wrapper.emitted("changeVersion")).toBeUndefined()
})
