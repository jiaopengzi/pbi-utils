/**
 * FilePath    : pbi-utils\web\tests\app\ModelTablePresentation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证表元信息的名称和顺序, 保留当前账号可见与不适用的统计说明.
 */
import { expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import ModelTableDetails from "@/app/ModelTableDetails.vue"
import { setLocale } from "@/i18n"
import { setupTables, tableItem, tablesProject } from "./ModelTablesPanel.support"

it.each([
    { locale: "zh-cn", labels: ["列数", "行数", "大小", "分区类型"], removed: "模型存储统计" },
    { locale: "en-us", labels: ["Columns", "Rows", "Size", "Partition type"], removed: "Model storage statistics" },
] as const)("$locale 元信息按列/行/大小/分区顺序显示, 不再重复存储统计说明", ({ locale, labels, removed }) => {
    setLocale(locale)
    const table = tablesProject().model.tables[0]!
    const wrapper = mount(ModelTableDetails, { props: { table, limited: false, rowCount: "25", storageSize: "2 KB", countKind: "storage" } })
    try {
        expect(wrapper.findAll("dt").map((label) => label.text())).toEqual(labels)
        expect(wrapper.findAll("dd").map((value) => value.text())).toEqual([
            String(table.columns.filter((column) => column.kind !== "RowNumber").length),
            "25",
            "2 KB",
            table.partitionKinds.join(", "),
        ])
        expect(wrapper.text()).not.toContain(removed)
    } finally {
        wrapper.unmount()
    }
})

it("保留实际查询的行数口径", () => {
    const wrapper = mount(ModelTableDetails, {
        props: { table: tablesProject().model.tables[0]!, limited: false, rowCount: "25", storageSize: "-", countKind: "query" },
    })
    try {
        expect(wrapper.text()).toContain("当前账号可见")
        expect(wrapper.findAll("dt").map((cell) => cell.text())).toEqual(["列数", "行数", "大小", "分区类型"])
    } finally {
        wrapper.unmount()
    }
})

it("表面板保留刷新统计, 移除单独的精确统计入口", async () => {
    const { wrapper, cleanup } = await setupTables()
    try {
        expect(wrapper.text()).not.toContain("行数默认来自模型统计")
        expect(wrapper.text()).toContain("刷新统计")
        expect(wrapper.text()).not.toContain("精确统计所选表")
    } finally {
        cleanup()
    }
})

it.each([
    { locale: "zh-cn", label: "不适用", reason: "此表没有可查询的数据列" },
    { locale: "en-us", label: "Not applicable", reason: "This table has no queryable data columns" },
] as const)("$locale 纯度量表显示明确不适用原因, 不伪造零行", async ({ locale, label, reason }) => {
    setLocale(locale)
    const noCount = {
        table: "销售",
        rowCount: null,
        sizeBytes: 272,
        isQueryCount: false,
        rowCountUnavailableReason: "no_queryable_columns" as const,
    }
    const { wrapper, cleanup } = await setupTables({ statistics: [noCount] })
    try {
        const item = tableItem(wrapper, "销售")
        expect(item.get(".table-title").text()).toContain(label)
        await item.get(".el-collapse-item__header").trigger("click")
        expect(item.text()).toContain(reason)
        expect(item.get(".table-title").text()).not.toMatch(/\b0\b/)
    } finally {
        cleanup()
    }
})
