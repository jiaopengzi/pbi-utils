/**
 * FilePath    : pbi-utils\web\tests\app\MeasureHierarchyNode.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 度量值目录递归勾选、半选与独立展开交互的真实组件测试.
 */
import { afterEach, expect, it } from "vitest"
import { mount, type VueWrapper } from "@vue/test-utils"
import { ElCheckbox } from "element-plus"
import MeasureHierarchyNode from "@/app/MeasureHierarchyNode.vue"
import { buildMeasureHierarchy } from "@/features/model/measureHierarchy"

const measures = [
    { table: "销售", name: "收入", expression: "1", displayFolder: "财务\\收入;财务\\其他", description: "", formatString: "", isHidden: false },
    { table: "销售", name: "成本", expression: "2", displayFolder: "财务\\成本", description: "", formatString: "", isHidden: false },
    { table: "其他表", name: "收入", expression: "3", displayFolder: "财务", description: "", formatString: "", isHidden: false },
]
const wrappers: VueWrapper[] = []
afterEach(() => {
    for (const wrapper of wrappers.splice(0)) wrapper.unmount()
})

/** 挂载真实目录组件, 默认使用销售表完整子树; 参数仅覆盖当前展示与受控选择. */
function setup(props: Partial<InstanceType<typeof MeasureHierarchyNode>["$props"]> = {}) {
    const wrapper = mount(MeasureHierarchyNode, {
        props: { node: buildMeasureHierarchy(measures, "")[0]!, selected: [], collapsed: new Set<string>(), disabled: false, ...props },
    })
    wrappers.push(wrapper)
    return wrapper
}

it("折叠目录的复选框仍选择全部唯一后代, 不包含其他表同名对象", async () => {
    const wrapper = setup({ collapsed: new Set(['["group","销售"]']) })
    expect(wrapper.findAll("input[type=checkbox]")).toHaveLength(1)
    await wrapper.get("input[type=checkbox]").setValue(true)
    expect(wrapper.emitted("selectGroup")).toEqual([[['["销售","收入"]', '["销售","成本"]'], true]])
    expect(wrapper.emitted("toggle")).toBeUndefined()
    expect(wrapper.emitted("select")).toBeUndefined()
})

it("目录复选框的可见标签可直接点击, 不触发展开或行选择", async () => {
    const wrapper = setup()
    document.body.appendChild(wrapper.element)
    await wrapper.get(".measure-folder-selection").trigger("click")
    expect(wrapper.emitted("selectGroup")).toEqual([[['["销售","收入"]', '["销售","成本"]'], true]])
    expect(wrapper.emitted("toggle")).toBeUndefined()
    expect(wrapper.emitted("select")).toBeUndefined()
})

it("目录半选和全选按唯一对象判断, 多显示位置不增加所需选择数", async () => {
    const wrapper = setup({ selected: ['["销售","收入"]'] })
    const checkbox = wrapper.getComponent(ElCheckbox)
    expect(checkbox.props("modelValue")).toBe(false)
    expect(checkbox.props("indeterminate")).toBe(true)
    await wrapper.setProps({ selected: ['["销售","收入"]', '["销售","成本"]'] })
    expect(checkbox.props("modelValue")).toBe(true)
    expect(checkbox.props("indeterminate")).toBe(false)
    await wrapper.get("input[type=checkbox]").setValue(false)
    expect(wrapper.emitted("selectGroup")).toEqual([[['["销售","收入"]', '["销售","成本"]'], false]])
})

it("嵌套目录递归回传准确选择, 复选框和展开按钮互不触发", async () => {
    const wrapper = setup()
    const folder = wrapper.findAll(".measure-folder-row").find((row) => row.get("button").text() === "其他")!
    await folder.get("input[type=checkbox]").setValue(true)
    expect(wrapper.emitted("selectGroup")).toEqual([[['["销售","收入"]'], true]])
    expect(wrapper.emitted("toggle")).toBeUndefined()
    await folder.get("button").trigger("click")
    expect(wrapper.emitted("toggle")).toEqual([['["group","销售","财务","其他"]']])
    expect(wrapper.emitted("selectGroup")).toHaveLength(1)
    expect(wrapper.emitted("select")).toBeUndefined()
})

it("叶子勾选保留跨表身份, 同一对象多处展示同步受控状态且行按钮独立选中", async () => {
    const wrapper = setup()
    const revenueRows = wrapper.findAll(".measure-row").filter((row) => row.get("button").text() === "收入")
    expect(revenueRows).toHaveLength(2)
    await revenueRows[0]!.get("input[type=checkbox]").setValue(true)
    expect(wrapper.emitted("selectGroup")).toEqual([[['["销售","收入"]'], true]])
    expect(wrapper.emitted("select")).toBeUndefined()
    await wrapper.setProps({ selected: ['["销售","收入"]'] })
    expect(revenueRows.every((row) => (row.get("input[type=checkbox]").element as HTMLInputElement).checked)).toBe(true)
    await revenueRows[1]!.get("button").trigger("click")
    expect(wrapper.emitted("select")).toEqual([[measures[0]]])
    expect(wrapper.emitted("selectGroup")).toHaveLength(1)
    await wrapper.setProps({ node: buildMeasureHierarchy(measures, "")[1]!, selected: ['["销售","收入"]'] })
    const otherRow = wrapper.get(".measure-row")
    expect((otherRow.get("input[type=checkbox]").element as HTMLInputElement).checked).toBe(false)
    await otherRow.get("input[type=checkbox]").setValue(true)
    expect(wrapper.emitted("selectGroup")?.at(-1)).toEqual([['["其他表","收入"]'], true])
})

it("禁用同时锁住目录与叶子, 合成变化不能绕过组件守卫", async () => {
    const wrapper = setup({ disabled: true })
    const checkboxes = wrapper.findAllComponents(ElCheckbox)
    expect(checkboxes.length).toBeGreaterThan(3)
    for (const checkbox of checkboxes) {
        expect(checkbox.props("disabled")).toBe(true)
        checkbox.vm.$emit("change", true)
    }
    await Promise.all(
        wrapper.findAll("button").map((button) => {
            expect(button.attributes("disabled")).toBeDefined()
            return button.trigger("click")
        }),
    )
    expect(wrapper.emitted("selectGroup")).toBeUndefined()
    expect(wrapper.emitted("toggle")).toBeUndefined()
    expect(wrapper.emitted("select")).toBeUndefined()
})

it("搜索过滤后的目录只勾选当前路径匹配对象", async () => {
    const wrapper = setup({ node: buildMeasureHierarchy(measures, "成本")[0]! })
    await wrapper.get("input[type=checkbox]").setValue(true)
    expect(wrapper.emitted("selectGroup")).toEqual([[['["销售","成本"]'], true]])
})
