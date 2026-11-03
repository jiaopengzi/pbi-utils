/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableExportGuard.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 直接验证导出意图边界, 即使数字控件尚未归一也不能提交无效样本量.
 */
import { expect, it } from "vitest"
import { defineComponent } from "vue"
import { flushPromises, mount } from "@vue/test-utils"
import { useModelTableExport } from "@/composables/app/useModelTableExport"
import { controlledBridge, exportResult, tablesProject } from "./ModelTablesPanel.support"

it.each([0, -1, 1.5, Number.NaN, Number.POSITIVE_INFINITY, undefined, 2147483648])("原始行数%s在请求边界拒绝, 不依赖数字控件的视觉禁用", async (value) => {
    const project = tablesProject()
    const { bridge, sent } = controlledBridge(project)
    let exporting!: ReturnType<typeof useModelTableExport>
    const wrapper = mount(
        defineComponent({
            setup() {
                exporting = useModelTableExport({ project, bridge })
                return () => null
            },
        }),
    )
    try {
        exporting.selectTable(project.model.tables[0]!, true)
        exporting.rowLimit.value = value
        expect(exporting.validLimit.value).toBe(false)
        await exporting.exportTables()
        await flushPromises()
        expect(sent).toHaveLength(0)
        expect(document.querySelector(".el-message--warning")).not.toBeNull()
        expect(exporting.busy.value).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("明确全部行时忽略未使用的样本草稿, 仅传null而不传NaN", async () => {
    const project = tablesProject()
    const { bridge, sent, send } = controlledBridge(project)
    let exporting!: ReturnType<typeof useModelTableExport>
    const wrapper = mount(
        defineComponent({
            setup() {
                exporting = useModelTableExport({ project, bridge })
                return () => null
            },
        }),
    )
    try {
        exporting.selectTable(project.model.tables[0]!, true)
        exporting.rowLimit.value = Number.NaN
        exporting.scope.value = "all"
        expect(exporting.validLimit.value).toBe(true)
        const completion = exporting.exportTables()
        expect(sent[0]!.params.rowLimit).toBeNull()
        send({ id: sent[0]!.id, result: exportResult("all.xlsx", ["销售"], 0, "xlsx", false, null) })
        await completion
        expect(exporting.busy.value).toBe(false)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})
