/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableCache.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证表格页重挂复用内存统计, 失败重试与模型身份失效边界.
 */
import { expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import ModelTablesPanel from "@/app/ModelTablesPanel.vue"
import { invalidateModelReads } from "@/features/model/modelReadCache"
import { controlledBridge, exportResult, selectTable, tableItem, tablesProject } from "./ModelTablesPanel.support"

it("同模型表格页重挂复用已读取统计和精确行数, 不再次进入读取 busy", async () => {
    const project = tablesProject()
    const h = controlledBridge(project, {
        statistics: [{ table: "销售", rowCount: 42, sizeBytes: 1024, isEstimate: false, isQueryCount: true }],
    })
    let wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
    try {
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(1)
        wrapper.unmount()
        wrapper = mount(ModelTablesPanel, { props: { project: structuredClone(project), bridge: h.bridge } })
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(1)
        expect(h.locationRequests).toHaveLength(1)
        expect(tableItem(wrapper, "销售").text()).toContain("42")
        expect(wrapper.emitted("busy")).toEqual([[false]])
    } finally {
        wrapper.unmount()
        h.bridge.dispose()
    }
})

it("显式刷新相同基线也重新读取, 刷新失败后重挂可重试", async () => {
    const project = tablesProject()
    const h = controlledBridge(project)
    let wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
    try {
        await flushPromises()
        h.setAutoStatistics(false)
        invalidateModelReads(h.bridge)
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(2)
        h.send({ id: h.statisticsRequests[1]!.id, error: { code: "READ_FAILED", message: "统计读取失败" } })
        await flushPromises()
        wrapper.unmount()
        wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(3)
        h.replyStatistics(h.statisticsRequests[2]!)
        await flushPromises()
    } finally {
        wrapper.unmount()
        h.bridge.dispose()
    }
})

it.each(["project", "path", "instance", "model", "baseline", "pending"])("%s 变化不复用先前统计", async (change) => {
    const project = tablesProject()
    const h = controlledBridge(project)
    let wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
    try {
        await flushPromises()
        wrapper.unmount()
        const next = structuredClone(project)
        if (change === "project") next.projectId = "different"
        if (change === "path") next.projectPath = "model://another"
        if (change === "instance") next.connection!.instanceId = "different"
        if (change === "model") next.connection!.modelId = "different"
        if (change === "baseline") next.baselineId = "changed"
        if (change === "pending") next.hasPendingChanges = true
        wrapper = mount(ModelTablesPanel, { props: { project: next, bridge: h.bridge } })
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(change === "pending" ? 1 : 2)
        if (change === "pending") {
            await wrapper.setProps({ project })
            await flushPromises()
            expect(h.statisticsRequests).toHaveLength(2)
        }
    } finally {
        wrapper.unmount()
        h.bridge.dispose()
    }
})

it("失效前迟到的统计不进入缓存, 新一代成功结果可在重挂后复用", async () => {
    const project = tablesProject()
    const h = controlledBridge(project, { autoStatistics: false })
    let wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
    try {
        await flushPromises()
        invalidateModelReads(h.bridge)
        await flushPromises()
        h.replyStatistics(h.statisticsRequests[0]!, [{ table: "销售", rowCount: 999, sizeBytes: null, isEstimate: false, isQueryCount: false }])
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(2)
        expect(tableItem(wrapper, "销售").text()).not.toContain("999")
        h.replyStatistics(h.statisticsRequests[1]!, [{ table: "销售", rowCount: 42, sizeBytes: null, isEstimate: false, isQueryCount: false }])
        await flushPromises()
        wrapper.unmount()
        wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(2)
        expect(tableItem(wrapper, "销售").text()).toContain("42")
    } finally {
        wrapper.unmount()
        h.bridge.dispose()
    }
})

it("刷新期间旧导出位置排空后不回填显示或缓存, 新位置读取完成后重挂复用", async () => {
    const project = tablesProject()
    const h = controlledBridge(project, { autoLocation: false })
    let wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
    try {
        await flushPromises()
        invalidateModelReads(h.bridge)
        await flushPromises()
        expect(h.locationRequests).toHaveLength(1)
        h.replyLocation(h.locationRequests[0]!, { id: "old", displayPath: "C:\\old-export", available: true, warning: null, isSimulation: false })
        await flushPromises()
        expect(h.locationRequests).toHaveLength(2)
        expect(wrapper.text()).not.toContain("old-export")
        h.replyLocation(h.locationRequests[1]!, { id: "new", displayPath: "C:\\new-export", available: true, warning: null, isSimulation: false })
        await flushPromises()
        expect(wrapper.text()).toContain("new-export")
        wrapper.unmount()
        wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
        await flushPromises()
        expect(h.locationRequests).toHaveLength(2)
        expect(wrapper.text()).toContain("new-export")
    } finally {
        wrapper.unmount()
        h.bridge.dispose()
    }
})

it("成功导出后位置刷新失败不让旧缓存阻止重挂重试, 当前页保留旧位置展示", async () => {
    const project = tablesProject()
    const h = controlledBridge(project, {
        location: { id: "old", displayPath: "C:\\old-export", available: true, warning: null, isSimulation: false },
    })
    let wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
    try {
        await flushPromises()
        expect(wrapper.text()).toContain("old-export")
        h.setAutoLocation(false)
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-export-button").trigger("click")
        h.send({ id: h.sent.find((request) => request.method === "model.exportTables")!.id, result: exportResult("new-export.xlsx", ["销售"]) })
        await flushPromises()
        expect(h.locationRequests).toHaveLength(2)
        h.send({ id: h.locationRequests[1]!.id, error: { code: "READ_FAILED", message: "位置读取失败" } })
        await flushPromises()
        expect(wrapper.text()).toContain("old-export")
        wrapper.unmount()
        wrapper = mount(ModelTablesPanel, { props: { project, bridge: h.bridge } })
        await flushPromises()
        expect(h.locationRequests).toHaveLength(3)
        h.replyLocation(h.locationRequests[2]!, { id: "new", displayPath: "C:\\new-export", available: true, warning: null, isSimulation: false })
        await flushPromises()
        expect(wrapper.text()).toContain("new-export")
        expect(wrapper.text()).not.toContain("old-export")
    } finally {
        wrapper.unmount()
        h.bridge.dispose()
    }
})
