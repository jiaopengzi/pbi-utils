/**
 * FilePath    : pbi-utils\web\tests\app\ModelTablesPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证表格批量选择、元信息、统一浮动消息及导出取消/迟到边界, 不创建真实文件.
 */
import { expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { ElSelect, ElSwitch } from "element-plus"
import ModelTablesPanel from "@/app/ModelTablesPanel.vue"
import ModelPanel from "@/app/ModelPanel.vue"
import ModelEditPanel from "@/app/ModelEditPanel.vue"
import UiTabs from "@/ui/UiTabs.vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale } from "@/i18n"
import { controlledBridge, exportResult, selectTable, setupTables, tableItem, tablesProject } from "./ModelTablesPanel.support"

it.each([
    { format: "xlsx", missingSchema: false },
    { format: "csv", missingSchema: false },
    { format: "xlsx", missingSchema: true },
    { format: "csv", missingSchema: true },
] as const)("受限Service导出$format, 缺少字段架构=$missingSchema 仍允许真实列读取", async ({ format, missingSchema }) => {
    const project = tablesProject()
    project.connection = { kind: "powerbi-service", label: "Power BI Service", isLimited: true, modelId: "service-model" }
    if (missingSchema) project.model.tables[0]!.columns = []
    const { wrapper, sent, send, cleanup } = await setupTables({ project })
    try {
        await selectTable(wrapper, "销售")
        wrapper.findComponent(ElSelect).vm.$emit("update:modelValue", format)
        await flushPromises()
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeUndefined()
        expect(wrapper.text()).toContain("未提供关系与角色信息")
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent).toHaveLength(1)
        expect(sent[0]).toMatchObject({
            method: "model.exportTables",
            params: {
                projectId: "preview",
                baselineId: "preview-only",
                tables: ["销售"],
                format,
                includeLog: format === "xlsx",
                locale: "zh-cn",
                rowLimit: 100,
            },
        })
        expect(Object.keys(sent[0]!.params).toSorted()).toEqual(["baselineId", "format", "includeLog", "locale", "projectId", "rowLimit", "tables"])
        send({ id: sent[0]!.id, result: exportResult(`销售.${format}`, ["销售"], 2, format) })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(document.body.textContent).toContain(`销售.${format}`)
    } finally {
        cleanup()
    }
})

it("受限Service可选择导出时仍只读, 关系/隐藏/UDF未知事实保持", async () => {
    const project = tablesProject()
    project.connection = { kind: "powerbi-service", label: "Power BI Service", isLimited: true }
    const transport = controlledBridge(project)
    const wrapper = mount(ModelPanel, { props: { project, bridge: transport.bridge, initialTab: "tables" } })
    try {
        await flushPromises()
        await tableItem(wrapper, "销售").get(".el-collapse-item__header").trigger("click")
        await selectTable(wrapper, "销售")
        expect(wrapper.findComponent(ModelEditPanel).exists()).toBe(false)
        expect(wrapper.findAll('[role="tab"]').map((tab) => tab.text())).toEqual(["度量值", "表格模型", "UDF"])
        expect(wrapper.text()).toContain("未提供关系与角色信息")
        expect(wrapper.text()).not.toContain("0 个角色")
        expect(tableItem(wrapper, "销售").find("tbody tr td:last-child").text()).toBe("未提供")
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeUndefined()
        await wrapper
            .findAll('[role="tab"]')
            .find((tab) => tab.text() === "UDF")!
            .trigger("click")
        expect(wrapper.text()).toContain("不提供兼容级别或 UDF 定义")
        expect(wrapper.text()).not.toContain("0 个模型函数")
    } finally {
        wrapper.unmount()
        transport.bridge.dispose()
    }
})

it("批量入口初始不选表, 全选后只发一轮包含全部所选表的导出", async () => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        const all = wrapper.get<HTMLInputElement>('[aria-label="选择所有可导出的表"] input')
        expect(all.element.checked).toBe(false)
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent).toHaveLength(0)
        await all.setValue(true)
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeUndefined()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent).toHaveLength(1)
        expect(sent[0]).toMatchObject({ method: "model.exportTables", params: { tables: ["销售", "另一张表"], format: "xlsx", includeLog: true } })
        send({ id: sent[0]!.id, result: exportResult("tables.xlsx", ["销售", "另一张表"]) })
        await flushPromises()
        expect(document.body.textContent).toContain("tables.xlsx")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})

it("全选跳过不可导出的表, 部分选择显示indeterminate且取消全选清空", async () => {
    const project = tablesProject()
    project.model.tables.push({
        name: "无实体列",
        description: null,
        isHidden: false,
        partitionKinds: [],
        columns: [{ name: "RowNumber-仅内部", kind: "RowNumber", dataType: "Int64", isHidden: true, description: null }],
    })
    const { wrapper, sent, cleanup } = await setupTables({ project })
    try {
        const all = wrapper.get<HTMLInputElement>('[aria-label="选择所有可导出的表"] input')
        expect(wrapper.get('[aria-label="选择表 无实体列"] input').attributes("disabled")).toBeDefined()
        await selectTable(wrapper, "销售")
        expect(all.element.indeterminate).toBe(true)
        expect(all.element.checked).toBe(false)
        await all.setValue(true)
        expect(all.element.indeterminate).toBe(false)
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 另一张表"] input').element.checked).toBe(true)
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 无实体列"] input').element.checked).toBe(false)
        await all.setValue(false)
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        expect(sent).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it("格式与选择全局保持, 切语言和刷新统计不重放导出或清空选项", async () => {
    const { wrapper, sent, send, statisticsRequests, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        wrapper.findComponent(ElSelect).vm.$emit("update:modelValue", "csv")
        await flushPromises()
        setLocale("en-us")
        await flushPromises()
        expect(wrapper.findComponent(ElSelect).props("modelValue")).toBe("csv")
        expect(wrapper.findAllComponents(ElSelect)).toHaveLength(1)
        expect(wrapper.findComponent(ElSwitch).exists()).toBe(false)
        expect(sent).toHaveLength(0)
        const previous = statisticsRequests.length
        await wrapper
            .findAll("button")
            .find((item) => /刷新统计|Refresh statistics/.test(item.text()))!
            .trigger("click")
        await flushPromises()
        expect(statisticsRequests).toHaveLength(previous + 1)
        expect(wrapper.findComponent(ElSelect).props("modelValue")).toBe("csv")
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeUndefined()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent[0]).toMatchObject({ method: "model.exportTables", params: { tables: ["销售"], format: "csv", includeLog: false, locale: "en-us" } })
        send({ id: sent[0]!.id, result: exportResult("销售.csv", ["销售"], 2, "csv") })
        await flushPromises()
        expect(document.body.textContent).toContain("销售.csv")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})

it("同项目刷新架构保留有效选择和格式, 切到其它项目清空选择", async () => {
    const { wrapper, project, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "另一张表")
        await selectTable(wrapper, "销售")
        wrapper.findComponent(ElSelect).vm.$emit("update:modelValue", "csv")
        await flushPromises()
        await wrapper.setProps({
            project: { ...project, baselineId: "refreshed-baseline", model: { ...project.model, tables: [project.model.tables[0]!] } },
        })
        await flushPromises()
        expect(wrapper.findComponent(ElSelect).props("modelValue")).toBe("csv")
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 销售"] input').element.checked).toBe(true)
        expect(wrapper.find('[aria-label="选择表 另一张表"]').exists()).toBe(false)
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent).toHaveLength(1)
        expect(sent[0]).toMatchObject({ method: "model.exportTables", params: { tables: ["销售"], baselineId: "refreshed-baseline", format: "csv" } })
        send({ id: sent[0]!.id, result: exportResult("销售.csv", ["销售"], 2, "csv") })
        await flushPromises()
        await wrapper.setProps({ project: { ...project, projectId: "different-project", baselineId: "different-baseline" } })
        await flushPromises()
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择所有可导出的表"] input').element.checked).toBe(false)
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 销售"] input').element.checked).toBe(false)
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        expect(sent).toHaveLength(1)
    } finally {
        cleanup()
    }
})

it.each(["limited", "pending", "no-columns", "full-service-no-columns", "limited-service-pending"])("%s仍不能选中并导出", async (reason) => {
    const project = tablesProject()
    if (reason === "limited") project.connection!.isLimited = true
    if (reason === "pending") project.hasPendingChanges = true
    if (reason === "no-columns") project.model.tables[0]!.columns = []
    if (reason === "full-service-no-columns") {
        project.connection = { kind: "powerbi-service", label: "Power BI Service" }
        project.model.tables[0]!.columns = []
    }
    if (reason === "limited-service-pending") {
        project.connection = { kind: "powerbi-service", label: "Power BI Service", isLimited: true }
        project.hasPendingChanges = true
    }
    const { wrapper, sent, cleanup } = await setupTables({ project })
    try {
        expect(wrapper.get('[aria-label="选择表 销售"] input').attributes("disabled")).toBeDefined()
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it("取消确认不提前解锁, 等原导出终态后排空; 发布阶段禁止取消", async () => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-export-button").trigger("click")
        await flushPromises()
        document.body.querySelector<HTMLButtonElement>(".operation-message button")!.click()
        await flushPromises()
        expect(sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: sent[0]!.id } })
        send({ id: sent[1]!.id, result: { accepted: true } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(wrapper.get('[aria-label="选择表 另一张表"] input').attributes("disabled")).toBeDefined()
        send({ id: sent[0]!.id, error: { code: "CANCELLED", message: "cancelled" } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(wrapper.find(".el-alert").exists()).toBe(false)
        await wrapper.get(".table-export-button").trigger("click")
        send({ requestPhase: { requestId: sent[2]!.id, phase: "committing" } })
        await flushPromises()
        expect(document.body.querySelector<HTMLButtonElement>(".operation-message button")!.disabled).toBe(true)
        send({ id: sent[2]!.id, result: exportResult("销售.xlsx", ["销售"], 0) })
        await flushPromises()
    } finally {
        cleanup()
    }
})

it("失败用浮动消息且保留选择和摘要, 模拟不能宣称创建真实文件", async () => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        const summary = wrapper.findComponent(ElSwitch)
        summary.vm.$emit("update:modelValue", false)
        await flushPromises()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent[0]!.params.includeLog).toBe(false)
        send({ id: sent[0]!.id, error: { code: "EXPORT_FAILED", message: "磁盘写入失败" } })
        await flushPromises()
        expect(wrapper.find(".el-alert").exists()).toBe(false)
        expect(document.querySelector(".el-message--error")?.textContent).toContain("磁盘写入失败")
        expect(summary.props("modelValue")).toBe(false)
        await wrapper.get(".table-export-button").trigger("click")
        send({ id: sent[1]!.id, result: exportResult("fake.xlsx", ["销售"], 55, "xlsx", true) })
        await flushPromises()
        expect(document.body.textContent).toContain("浏览器模拟预览无法读取真实表数据")
        expect(document.body.textContent).not.toContain("已导出 55 行")
    } finally {
        cleanup()
    }
})

it.each(["target", "unmount"])("%s时取消旧请求, 迟到结果不污染新目标或通知完成", async (change) => {
    const { wrapper, project, sent, send, unmount, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-export-button").trigger("click")
        if (change === "target") await wrapper.setProps({ project: { ...project, projectId: "new-target", baselineId: "new-baseline" } })
        else unmount()
        const busyEvents = wrapper.emitted("busy")?.length
        expect(sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: sent[0]!.id } })
        send({ id: sent[1]!.id, result: { accepted: false } })
        send({ id: sent[0]!.id, result: exportResult("late.xlsx", ["销售"], 999) })
        await flushPromises()
        expect(document.body.textContent).not.toContain("late.xlsx")
        if (change === "unmount") expect(wrapper.emitted("busy")?.length).toBe(busyEvents)
    } finally {
        cleanup()
    }
})

it("默认浏览器模拟明确拒绝批量导出, 不返回伪造文件", async () => {
    const bridge = new BridgeClient(new MockTransport())
    try {
        await expect(
            bridge.request("model.exportTables", {
                projectId: "preview",
                baselineId: "preview-only",
                tables: ["销售"],
                format: "xlsx",
                includeLog: true,
                locale: "zh-cn",
                rowLimit: 100,
            }).promise,
        ).rejects.toMatchObject({ code: "MODEL_EXPORT_SIMULATION_UNAVAILABLE" })
    } finally {
        bridge.dispose()
    }
})

it.each(["zh-cn", "en-us"] as const)("Excel超限在%s经message.ts浮动反馈, 没有UiNotice或页内alert", async (locale) => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        setLocale(locale)
        await flushPromises()
        await wrapper.get(".table-export-button").trigger("click")
        send({ id: sent[0]!.id, error: { code: "EXPORT_EXCEL_LIMIT", message: "数据超过 Excel 行数、列数或单元格长度上限, 请使用 CSV" } })
        await flushPromises()
        const message = document.querySelector(".el-message--error")!
        expect(message.textContent).toContain("Excel")
        expect(message.textContent).toContain("CSV")
        if (locale === "en-us") expect(message.textContent).not.toMatch(/\p{Script=Han}/u)
        expect(wrapper.find(".el-alert").exists()).toBe(false)
    } finally {
        cleanup()
    }
})

it("英文失败保留文件已经导出但备份清理失败事实, 通过浮动消息显示", async () => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        setLocale("en-us")
        await flushPromises()
        await wrapper.get(".table-export-button").trigger("click")
        send({ id: sent[0]!.id, error: { code: "EXPORT_CLEANUP_FAILED", message: "文件已成功导出, 但原目标备份清理失败; 请检查目标目录" } })
        await flushPromises()
        expect(document.querySelector(".el-message--error")?.textContent).toContain(
            "The file was exported successfully, but cleanup of the original destination backup failed",
        )
        expect(wrapper.find(".el-alert").exists()).toBe(false)
    } finally {
        cleanup()
    }
})

it("父面板合并导出忙状态, 批量选择与表说明编辑双向互锁", async () => {
    const project = tablesProject()
    project.connection = { kind: "desktop", label: "Desktop", instanceId: "isolated" }
    const { bridge, sent, send } = controlledBridge(project)
    const wrapper = mount(ModelPanel, { props: { bridge, project, initialTab: "tables" }, global: { stubs: { ConfigurationPanel: true } } })
    try {
        await flushPromises()
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-export-button").trigger("click")
        expect(wrapper.findComponent(UiTabs).props("disabled")).toBe(true)
        expect(wrapper.findComponent(ModelEditPanel).props("locked")).toBe(true)
        expect(wrapper.get(".copy-change textarea").attributes("disabled")).toBeDefined()
        send({ id: sent.find((request) => request.method === "model.exportTables")!.id, error: { code: "CANCELLED", message: "已取消" } })
        await flushPromises()
        expect(wrapper.findComponent(ModelEditPanel).props("locked")).toBe(false)
        const editor = wrapper.findComponent(ModelEditPanel)
        editor.findAllComponents(ElSelect)[0]!.vm.$emit("update:modelValue", "销售")
        await flushPromises()
        await editor.get("textarea").setValue("编辑草稿")
        await editor.get("form").trigger("submit")
        await flushPromises()
        expect(wrapper.findComponent(ModelTablesPanel).props("locked")).toBe(true)
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        send({ id: sent.find((request) => request.method === "model.edit")!.id, result: project })
        await flushPromises()
        expect(wrapper.findComponent(UiTabs).props("disabled")).toBe(false)
        expect(wrapper.findComponent(ModelTablesPanel).props("locked")).toBe(false)
        expect(editor.get("textarea").element).toHaveProperty("value", "编辑草稿")
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it.each(["zh-cn", "en-us"] as const)("取消附带清理失败在%s使用警告消息保留处理事实", async (locale) => {
    const { wrapper, sent, send, cleanup } = await setupTables()
    try {
        await selectTable(wrapper, "销售")
        setLocale(locale)
        await flushPromises()
        await wrapper.get(".table-export-button").trigger("click")
        send({ id: sent[0]!.id, error: { code: "CANCELLED", message: "操作已取消; 导出临时文件清理失败, 请检查目标目录" } })
        await flushPromises()
        expect(document.querySelector(".el-message--warning")?.textContent).toContain(
            locale === "zh-cn" ? "导出临时文件清理失败, 请检查目标目录" : "Export temporary file cleanup failed. Check the destination folder.",
        )
        expect(wrapper.find(".el-alert").exists()).toBe(false)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})

it("字段界面过滤内部RowNumber, 仍保留同名用户数据列并正确计数", async () => {
    const project = tablesProject()
    project.model.tables[0]!.columns.push(
        { name: "RowNumber-7C11-内部字段", kind: "RowNumber", dataType: "Int64", isHidden: true, description: null },
        { name: "RowNumber", kind: "Data", dataType: "Int64", isHidden: false, description: null },
    )
    const { wrapper, cleanup } = await setupTables({ project })
    try {
        const sales = tableItem(wrapper, "销售")
        await sales.get(".el-collapse-item__header").trigger("click")
        expect(wrapper.text()).not.toContain("RowNumber-7C11-内部字段")
        expect(sales.text()).toContain("RowNumber")
        expect(sales.get(".table-title span:last-child").text()).toBe("2")
        expect(wrapper.findAll("th").map((cell) => cell.text())).toContain("数据类型")
        expect(wrapper.findAll("th").map((cell) => cell.text())).not.toContain("类型")
    } finally {
        cleanup()
    }
})

it("统计请求绑定精确身份, 已读取零值/未知值与估计值分别呈现", async () => {
    const { wrapper, statisticsRequests, setStatistics, cleanup } = await setupTables({
        statistics: [
            { table: "销售", rowCount: 0, sizeBytes: 0, isEstimate: false, isQueryCount: false },
            { table: "另一张表", rowCount: null, sizeBytes: null, isEstimate: false, isQueryCount: false },
        ],
    })
    try {
        expect(statisticsRequests).toHaveLength(1)
        expect(statisticsRequests[0]!.params).toEqual({ projectId: "preview", baselineId: "preview-only" })
        const sales = tableItem(wrapper, "销售"),
            other = tableItem(wrapper, "另一张表")
        await sales.get(".el-collapse-item__header").trigger("click")
        await other.get(".el-collapse-item__header").trigger("click")
        expect(sales.text()).toContain("行数")
        expect(sales.text()).toMatch(/0\s*(B|Bytes)/)
        expect(other.text()).toContain("-")
        expect(other.text()).not.toMatch(/0\s*(B|Bytes)/)
        setStatistics([
            { table: "销售", rowCount: 4096, sizeBytes: 2048, isEstimate: true, isQueryCount: false },
            { table: "另一张表", rowCount: null, sizeBytes: null, isEstimate: false, isQueryCount: false },
        ])
        await wrapper
            .findAll("button")
            .find((item) => /刷新统计|Refresh statistics/.test(item.text()))!
            .trigger("click")
        await flushPromises()
        expect(sales.text()).toMatch(/≈\s*4[,. ]?096/)
        expect(sales.text()).toMatch(/2\s*KB/i)
    } finally {
        cleanup()
    }
})

it("统计刷新失败通过警告反馈, 不丢失已读取的行数与大小", async () => {
    const { wrapper, statisticsRequests, setAutoStatistics, send, cleanup } = await setupTables({
        statistics: [{ table: "销售", rowCount: 1234, sizeBytes: 2048, isEstimate: false, isQueryCount: false }],
    })
    try {
        setAutoStatistics(false)
        const sales = tableItem(wrapper, "销售")
        await sales.get(".el-collapse-item__header").trigger("click")
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "刷新统计")!
            .trigger("click")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        send({ id: statisticsRequests.at(-1)!.id, error: { code: "MODEL_STATS_FAILED", message: "统计读取失败" } })
        await flushPromises()
        expect(document.querySelector(".el-message--warning")?.textContent).toContain("统计读取失败")
        expect(sales.text()).toMatch(/1[,. ]?234/)
        expect(sales.text()).toMatch(/2\s*KB/i)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})

it("统计取消确认等待原统计终态, 期间保持导出禁用并拒绝身份不匹配数据", async () => {
    const { wrapper, statisticsRequests, sent, send, cleanup } = await setupTables({ autoStatistics: false })
    try {
        const first = statisticsRequests[0]!
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        document.body.querySelector<HTMLButtonElement>(".operation-message button")!.click()
        await flushPromises()
        expect(sent[0]).toMatchObject({ method: "request.cancel", params: { requestId: first.id } })
        send({ id: sent[0]!.id, result: { accepted: true } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(wrapper.get('[aria-label="选择表 销售"] input').attributes("disabled")).toBeDefined()
        send({
            id: first.id,
            result: {
                projectId: "wrong-project",
                baselineId: "wrong-baseline",
                tables: [{ table: "销售", rowCount: 777, sizeBytes: 777, isEstimate: false, isQueryCount: false }],
                isSimulation: false,
            },
        })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(tableItem(wrapper, "销售").text()).not.toContain("777")
        expect(wrapper.get('[aria-label="选择表 销售"] input').attributes("disabled")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it("切换模型后迟到统计不覆盖新目标, 新目标统计使用本轮身份", async () => {
    const { wrapper, project, statisticsRequests, replyStatistics, cleanup } = await setupTables({ autoStatistics: false })
    try {
        expect(statisticsRequests).toHaveLength(1)
        const first = statisticsRequests[0]!
        await wrapper.setProps({ project: { ...project, projectId: "new-target", baselineId: "new-baseline" } })
        await flushPromises()
        expect(statisticsRequests).toHaveLength(1)
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        replyStatistics(first, [{ table: "销售", rowCount: 999, sizeBytes: 999, isEstimate: true, isQueryCount: false }])
        await flushPromises()
        expect(tableItem(wrapper, "销售").text()).not.toContain("999")
        expect(statisticsRequests).toHaveLength(2)
        const second = statisticsRequests.at(-1)!
        expect(second.params).toEqual({ projectId: "new-target", baselineId: "new-baseline" })
        replyStatistics(second, [{ table: "销售", rowCount: 22, sizeBytes: 1024, isEstimate: false, isQueryCount: false }])
        await flushPromises()
        const sales = tableItem(wrapper, "销售")
        await sales.get(".el-collapse-item__header").trigger("click")
        expect(sales.text()).toContain("22")
        expect(sales.text()).not.toContain("999")
    } finally {
        cleanup()
    }
})
