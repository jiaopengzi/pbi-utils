/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfLocalPageSelection.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证本地列表当前页批量选择的搜索、分页和禁用边界.
 */
import { afterEach, expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { ElPagination, ElSelect } from "element-plus"
import { markRaw } from "vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import UdfLocalBrowser from "@/features/scaffold/UdfLocalBrowser.vue"
import type { LocalUdfEntry, UdfSelection } from "@/features/scaffold/contracts"

const cleanup: (() => void)[] = []
afterEach(() => cleanup.splice(0).forEach((close) => close()))

/** 直接使用列表的宿主条目契约, 不通过显示名称生成选择基线. */
function entry(index: number): LocalUdfEntry {
    return {
        id: `local-${index}`,
        baseline: "c".repeat(64),
        definition: {
            schemaVersion: 1,
            name: `Local.Fn${String(index).padStart(3, "0")}`,
            version: "1.0.0",
            expression: "() => 1",
            description: "",
            dependencies: [],
        },
    }
}

/** 挂载真实列表和分页器, 记录对外发出的完整当前页候选. */
function fixture(embedded = false) {
    const items = Array.from({ length: 55 }, (_, index) => entry(index))
    const wrapper = mount(UdfLocalBrowser, { props: { items, selections: [], query: "", disabled: false, embedded } })
    cleanup.push(() => wrapper.unmount())
    return { wrapper, items, selectPage: () => wrapper.get(".udf-page-actions button") }
}

it.each([false, true])("本地嵌入模式%s复用页操作栏, 仅选择当前50行并可选择最后短页", async (embedded) => {
    const { wrapper, items, selectPage } = fixture(embedded)
    expect(selectPage().text()).toBe("选择本页函数")
    expect(wrapper.get(".udf-page-actions").findComponent(ElPagination).exists()).toBe(true)
    await selectPage().trigger("click")
    expect(wrapper.emitted("selectPage")?.[0]).toEqual([items.slice(0, 50)])
    wrapper.getComponent(ElPagination).vm.$emit("update:current-page", 2)
    await flushPromises()
    await selectPage().trigger("click")
    expect(wrapper.emitted("selectPage")?.[1]).toEqual([items.slice(50)])
})

it("搜索重置页码, 本页候选只包含名称匹配条目, 空页和禁用不发选择事件", async () => {
    const { wrapper, items, selectPage } = fixture()
    wrapper.getComponent(ElPagination).vm.$emit("update:current-page", 2)
    await wrapper.setProps({ query: "Fn001" })
    expect(wrapper.getComponent(ElPagination).props("currentPage")).toBe(1)
    await selectPage().trigger("click")
    expect(wrapper.emitted("selectPage")?.[0]).toEqual([[items[1]]])
    await wrapper.setProps({ disabled: true })
    await selectPage().trigger("click")
    expect(wrapper.emitted("selectPage")).toHaveLength(1)
    await wrapper.setProps({ disabled: false, query: "Missing" })
    expect(selectPage().attributes("disabled")).toBeDefined()
    await selectPage().trigger("click")
    expect(wrapper.emitted("selectPage")).toHaveLength(1)
})

it("来源全量动作位于对应列表标题, 远端嵌入本地页和独立本地页均可原子选择", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    cleanup.push(() => bridge.dispose())
    await bridge.request("udf.catalogRefresh", { source: "daxlib" }).promise
    const local = await bridge.request("udf.localSave", { definition: entry(1).definition }).promise
    const wrapper = mount(UdfStep, {
        props: {
            bridge,
            disabled: false,
            modelValue: [] as UdfSelection[],
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    const heading = () => wrapper.get(".udf-browser > .udf-list-heading")
    expect(heading().get("h4").text()).toBe("DAXLib")
    expect(heading().get("button").text()).toBe("选择当前来源全部函数")
    expect(wrapper.findAll("button").filter((button) => button.text() === "选择当前来源全部函数")).toHaveLength(1)
    expect(wrapper.get(".udf-local-browser").findAll(".udf-page-actions button")[0]?.text()).toBe("选择本页函数")
    await wrapper.get(".udf-local-browser .udf-page-actions button").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toEqual([
        { source: "local", packageId: local.id, version: "1.0.0", functions: [local.definition.name], baseline: local.baseline },
    ])
    const picker = wrapper.getComponent(ElSelect)
    await picker.setValue("local")
    picker.vm.$emit("change", "local")
    await flushPromises()
    expect(heading().get("h4").text()).toBe("本地")
    expect(
        heading()
            .findAll("button")
            .map((button) => button.text()),
    ).toEqual(["刷新", "选择当前来源全部函数"])
    await heading().get("button").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toHaveLength(1)
    await wrapper.get(".udf-page-actions button").trigger("click")
    await flushPromises()
    expect(wrapper.props("modelValue")).toHaveLength(1)
})
