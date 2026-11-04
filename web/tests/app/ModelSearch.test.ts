/**
 * FilePath    : pbi-utils\web\tests\app\ModelSearch.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型搜索的纯文本高亮、可见范围选择与草稿无关的筛选状态.
 */
import { expect, it } from "vitest"
import { mount, flushPromises } from "@vue/test-utils"
import MeasureHierarchy from "@/app/MeasureHierarchy.vue"
import { setLocale } from "@/i18n"
import { selectTable, setupTables } from "./ModelTablesPanel.support"

it("度量值多词搜索高亮递归目录与名称, 清空后不保留标记且不执行HTML", async () => {
    const measure = {
        table: "Sales",
        name: "<b>收入[月]</b>",
        displayFolder: "财务\\月收入",
        expression: "1",
        description: "",
        formatString: "",
        isHidden: false,
    }
    const wrapper = mount(MeasureHierarchy, {
        props: { measures: [measure], query: "  财务 收入 [月]  ", selected: [], selectedCount: 0, disabled: false },
    })
    try {
        expect(wrapper.findAll("mark").map((item) => item.text())).toEqual(["财务", "收入", "收入", "[月]"])
        expect(wrapper.find(".measure-row b").exists()).toBe(false)
        expect(wrapper.get(".measure-row strong").text()).toBe(measure.name)
        await wrapper.setProps({ query: "sales" })
        expect(wrapper.findAll("mark").map((item) => item.text())).toEqual(["Sales"])
        await wrapper.setProps({ query: "  " })
        expect(wrapper.findAll("mark")).toHaveLength(0)
        expect(wrapper.emitted("select")).toBeUndefined()
        expect(wrapper.emitted("update:selected")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it("表格搜索只筛选名称, 清空恢复列表且不重复查询模型", async () => {
    const { wrapper, statisticsRequests, sent, cleanup } = await setupTables()
    try {
        const count = statisticsRequests.length
        const search = wrapper.get('input[aria-label="搜索表格"]')
        await search.setValue("  另一 张表 ")
        expect(wrapper.findAll(".table-name").map((item) => item.text())).toEqual(["另一张表"])
        expect(wrapper.findAll(".table-name mark").map((item) => item.text())).toEqual(["另一", "张表"])
        await search.setValue("no match")
        expect(wrapper.findAll(".model-table-entry")).toHaveLength(0)
        expect(wrapper.text()).toContain("没有匹配的表格")
        expect(wrapper.get('.table-list-head input[type="checkbox"]').attributes("disabled")).toBeDefined()
        await search.setValue("")
        expect(wrapper.findAll(".model-table-entry")).toHaveLength(2)
        expect(statisticsRequests).toHaveLength(count)
        expect(sent).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it("筛选全选只影响可见表, 输入框清除查询保留隐藏选择且没有重复清空按钮", async () => {
    const { wrapper, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get('input[aria-label="搜索表格"]').setValue("另一")
        expect(wrapper.get(".table-search-summary").text()).toContain("已选 1 张, 其中 1 张未显示")
        await wrapper.get('.table-list-head input[type="checkbox"]').setValue(true)
        expect(wrapper.get(".table-search-summary").text()).toContain("已选 2 张, 其中 1 张未显示")
        await wrapper.get('.table-list-head input[type="checkbox"]').setValue(false)
        expect(wrapper.get(".table-search-summary").text()).toContain("已选 1 张, 其中 1 张未显示")
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.get<HTMLInputElement>('input[aria-label="Search tables"]').element.value).toBe("另一")
        expect(wrapper.get(".table-search-summary").text()).toContain("1 selected, 1 hidden")
        expect(wrapper.find('[data-test="clear-table-selection"]').exists()).toBe(false)
        await wrapper.get(".table-search-toolbar .el-input").trigger("mouseenter")
        await wrapper.get(".table-search-toolbar .el-input__clear").trigger("click")
        expect(wrapper.get<HTMLInputElement>('input[aria-label="Search tables"]').element.value).toBe("")
        expect(wrapper.findAll(".model-table-entry")).toHaveLength(2)
        expect(wrapper.get(".table-search-summary").text()).toContain("1 selected, 0 hidden")
    } finally {
        cleanup()
        setLocale("zh-cn")
    }
})

it("表格搜索保留同模型刷新状态, 新项目清空且操作锁限制编辑", async () => {
    const { wrapper, project, cleanup } = await setupTables()
    try {
        const search = wrapper.get('input[aria-label="搜索表格"]')
        await search.setValue("销售")
        await wrapper.setProps({ project: { ...project, baselineId: "new-baseline" } })
        expect(wrapper.findAll(".model-table-entry")).toHaveLength(1)
        await wrapper.setProps({ locked: true })
        expect(search.attributes("disabled")).toBeDefined()
        await wrapper.setProps({ locked: false, project: { ...project, projectId: "other-project" } })
        expect((search.element as HTMLInputElement).value).toBe("")
        expect(wrapper.findAll(".model-table-entry")).toHaveLength(2)
    } finally {
        cleanup()
    }
})
