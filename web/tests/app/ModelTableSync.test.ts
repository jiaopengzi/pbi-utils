/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableSync.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证表格只读统计自动同步权威模型, 有界恢复过期基线并保护取消和未知提交.
 */
import { expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import type { ProjectSnapshot } from "@/bridge/client"
import ModelPanel from "@/app/ModelPanel.vue"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import { controlledBridge, selectTable, setupTables, tableItem, tablesProject } from "./ModelTablesPanel.support"

/** 使用宿主已绑定的 Desktop 身份, 不连接真实引擎或写入用户模型. */
function desktopProject(): ProjectSnapshot {
    const project = tablesProject()
    project.connection = { kind: "desktop", label: "Desktop", instanceId: "desktop-instance", modelId: "database" }
    return project
}

/** 控制首次统计过期, 将后续权威回读交给每个用例, 不自动响应副作用请求. */
async function staleStatistics() {
    const h = await setupTables({ project: desktopProject(), autoStatistics: false })
    h.send({ id: h.statisticsRequests[0]!.id, error: { code: "STALE_BASELINE", message: "模型已变化, 请刷新后重试" } })
    await flushPromises()
    return h
}

/** 返回与统计共用权威模型的完整配置上下文, 不省略目标或写入能力字段. */
function configurationContext(project: ProjectSnapshot) {
    return {
        targetId: "current-model",
        project,
        details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
        canWrite: true,
        canGenerateSample: false,
        isCreatedTarget: false,
        isSimulation: false,
    }
}

it("首次统计基线过期时自动回读并发布新快照, 父级接纳后填充最新表格", async () => {
    const h = await staleStatistics()
    try {
        expect(h.sent.map((request) => request.method)).toEqual(["project.inspect"])
        expect(document.querySelector(".el-message--warning")).toBeNull()
        const fresh = structuredClone(h.project)
        fresh.baselineId = "fresh-baseline"
        fresh.model.tables.push({ ...structuredClone(fresh.model.tables[0]!), name: "新表" })
        h.send({ id: h.sent[0]!.id, result: fresh })
        await flushPromises()
        expect(h.wrapper.emitted("updated")).toEqual([[fresh]])
        await h.wrapper.setProps({ project: fresh })
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(2)
        expect(h.statisticsRequests[1]!.params.baselineId).toBe("fresh-baseline")
        h.replyStatistics(h.statisticsRequests[1]!, [{ table: "新表", rowCount: 42, sizeBytes: 2048, isEstimate: false, isQueryCount: false }])
        await flushPromises()
        expect(tableItem(h.wrapper, "新表").text()).toContain("42")
        expect(h.wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(h.sent.map((request) => request.method)).toEqual(["project.inspect"])
    } finally {
        h.cleanup()
    }
})

it("回读后统计再次过期只报告一次失败, 不无限核验或重放读写操作", async () => {
    const h = await staleStatistics()
    try {
        expect(h.sent[0]?.method).toBe("project.inspect")
        h.send({ id: h.sent[0]!.id, result: h.project })
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(2)
        h.send({ id: h.statisticsRequests[1]!.id, error: { code: "STALE_BASELINE", message: "模型继续变化" } })
        await flushPromises()
        expect(h.sent).toHaveLength(1)
        expect(h.statisticsRequests).toHaveLength(2)
        expect(document.querySelector(".el-message--warning")?.textContent).toContain("模型继续变化")
        expect(h.wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        h.cleanup()
    }
})

it.each(["target", "instance", "model", "draft", "verification"])("自动回读遇到 %s 不发布快照或继续统计", async (change) => {
    const h = await staleStatistics()
    try {
        expect(h.sent[0]?.method).toBe("project.inspect")
        const fresh = structuredClone(h.project)
        fresh.baselineId = "fresh-baseline"
        if (change === "target") fresh.projectPath = "model://different"
        if (change === "instance") fresh.connection!.instanceId = "different-instance"
        if (change === "model") fresh.connection!.modelId = "different-model"
        if (change === "draft") fresh.hasPendingChanges = true
        if (change === "verification")
            fresh.saveResult = { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "待核验", errorCode: "MODEL_NEEDS_VERIFICATION" }
        h.send({ id: h.sent[0]!.id, result: fresh })
        await flushPromises()
        expect(h.wrapper.emitted("updated")).toBeUndefined()
        expect(h.statisticsRequests).toHaveLength(1)
        expect(h.wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        h.cleanup()
    }
})

it("自动回读取消后即使宿主返回成功也不接纳或重读, 取消受理仍等待终态", async () => {
    const h = await staleStatistics()
    try {
        expect(h.sent[0]?.method).toBe("project.inspect")
        document.querySelector<HTMLButtonElement>(".operation-feedback__cancel")!.click()
        await flushPromises()
        expect(h.sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: h.sent[0]!.id } })
        h.send({ id: h.sent[1]!.id, result: { accepted: true } })
        await flushPromises()
        expect(h.wrapper.emitted("busy")?.at(-1)).toEqual([true])
        h.send({ id: h.sent[0]!.id, result: { ...h.project, baselineId: "fresh" } })
        await flushPromises()
        expect(h.wrapper.emitted("updated")).toBeUndefined()
        expect(h.statisticsRequests).toHaveLength(1)
        expect(h.wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        h.cleanup()
    }
})

it.each(["draft", "verification"])("自动回读期间同基线新增 %s 状态时, 迟到的正常快照不能解除保护", async (state) => {
    const h = await staleStatistics()
    try {
        expect(h.sent[0]?.method).toBe("project.inspect")
        const pending = structuredClone(h.project)
        if (state === "draft") pending.hasPendingChanges = true
        else pending.saveResult = { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "待核验", errorCode: "NEEDS_VERIFICATION" }
        await h.wrapper.setProps({ project: pending })
        h.send({ id: h.sent[0]!.id, result: { ...h.project, baselineId: "fresh" } })
        await flushPromises()
        expect(h.wrapper.emitted("updated")).toBeUndefined()
        expect(h.statisticsRequests).toHaveLength(1)
        await h.wrapper.setProps({ project: h.project })
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(2)
        h.replyStatistics(h.statisticsRequests[1]!)
        await flushPromises()
    } finally {
        h.cleanup()
    }
})

it.each(["target", "unmount"])("自动回读在 %s 后的迟到结果不发布到新页面", async (change) => {
    const h = await staleStatistics()
    try {
        expect(h.sent[0]?.method).toBe("project.inspect")
        if (change === "target") await h.wrapper.setProps({ project: { ...h.project, projectId: "another", baselineId: "another" } })
        else h.unmount()
        h.send({ id: h.sent[0]!.id, result: { ...h.project, baselineId: "fresh" } })
        await flushPromises()
        expect(h.wrapper.emitted("updated")).toBeUndefined()
        expect(document.querySelector(".el-message--warning")).toBeNull()
    } finally {
        h.cleanup()
    }
})

it("从 Desktop 返回表格页会自动重读统计, 在途焦点重复事件合并且卸载移除监听", async () => {
    const h = await setupTables({ project: desktopProject() })
    try {
        h.setAutoStatistics(false)
        window.dispatchEvent(new Event("focus"))
        window.dispatchEvent(new Event("focus"))
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(2)
        h.replyStatistics(h.statisticsRequests[1]!, [{ table: "销售", rowCount: 99, sizeBytes: null, isEstimate: false, isQueryCount: false }])
        await flushPromises()
        expect(tableItem(h.wrapper, "销售").text()).toContain("99")
        h.unmount()
        window.dispatchEvent(new Event("focus"))
        expect(h.statisticsRequests).toHaveLength(2)
    } finally {
        h.cleanup()
    }
})

it("显式精确计数过期不自动重放COUNTROWS或发布新的选择基线", async () => {
    const h = await setupTables({ project: desktopProject() })
    try {
        await selectTable(h.wrapper, "销售")
        await h.wrapper.get(".exact-row-count").trigger("click")
        h.send({ id: h.rowCountRequests[0]!.id, error: { code: "STALE_BASELINE", message: "计数目标已变化" } })
        await flushPromises()
        expect(h.rowCountRequests).toHaveLength(1)
        expect(h.sent).toHaveLength(0)
        expect(h.wrapper.emitted("updated")).toBeUndefined()
        expect(document.querySelector(".el-message--warning")?.textContent).toContain("计数目标已变化")
    } finally {
        h.cleanup()
    }
})

it("表格新快照经模型面板转发到工作区, 不局限在统计子组件内部", async () => {
    const project = tablesProject()
    const transport = controlledBridge(project, { autoStatistics: false })
    const wrapper = mount(ModelPanel, { props: { project, bridge: transport.bridge, initialTab: "tables" } })
    try {
        await flushPromises()
        transport.send({ id: transport.statisticsRequests[0]!.id, error: { code: "STALE_BASELINE", message: "旧基线" } })
        await flushPromises()
        expect(transport.sent[0]?.method).toBe("project.inspect")
        const fresh = { ...project, baselineId: "current" }
        transport.send({ id: transport.sent[0]!.id, result: fresh })
        await flushPromises()
        expect(wrapper.emitted("updated")).toEqual([[fresh]])
    } finally {
        wrapper.unmount()
        transport.bridge.dispose()
    }
})

it("新基线等待隐藏配置读链排空再统计, 第二次过期不因父级更新重置恢复次数", async () => {
    const project = desktopProject()
    const transport = controlledBridge(project, { autoStatistics: false })
    const wrapper = mount(ModelPanel, { props: { project, bridge: transport.bridge, initialTab: "tables" } })
    const catalog = new ScaffoldMock().handle("scaffold.templates", {})
    try {
        await flushPromises()
        expect(transport.statisticsRequests).toHaveLength(0)
        transport.send({ id: transport.sent.find((request) => request.method === "scaffold.templates")!.id, result: catalog })
        await flushPromises()
        transport.send({ id: transport.sent.find((request) => request.method === "configuration.context")!.id, result: configurationContext(project) })
        await flushPromises()
        expect(transport.statisticsRequests).toHaveLength(1)
        transport.send({ id: transport.statisticsRequests[0]!.id, error: { code: "STALE_BASELINE", message: "旧基线" } })
        await flushPromises()
        const inspect = transport.sent.find((request) => request.method === "project.inspect")!
        expect(inspect).toBeDefined()
        const fresh = { ...project, baselineId: "authoritative-new-baseline" }
        transport.send({ id: inspect.id, result: fresh })
        await flushPromises()
        await wrapper.setProps({ project: fresh })
        await flushPromises()
        expect(transport.statisticsRequests).toHaveLength(1)
        transport.send({ id: transport.sent.filter((request) => request.method === "scaffold.templates")[1]!.id, result: catalog })
        await flushPromises()
        expect(transport.statisticsRequests).toHaveLength(1)
        transport.send({ id: transport.sent.filter((request) => request.method === "configuration.context")[1]!.id, result: configurationContext(fresh) })
        await flushPromises()
        expect(transport.statisticsRequests).toHaveLength(2)
        expect(transport.statisticsRequests[1]!.params.baselineId).toBe(fresh.baselineId)
        transport.send({ id: transport.statisticsRequests[1]!.id, error: { code: "STALE_BASELINE", message: "持续变化" } })
        await flushPromises()
        expect(transport.sent.filter((request) => request.method === "project.inspect")).toHaveLength(1)
        expect(transport.statisticsRequests).toHaveLength(2)
        expect(document.querySelector(".el-message--warning")?.textContent).toContain("持续变化")
    } finally {
        wrapper.unmount()
        transport.bridge.dispose()
    }
})

it("返回前台不会插入已锁定或待核验模型的后台读取", async () => {
    const h = await setupTables({ project: desktopProject() })
    try {
        await h.wrapper.setProps({ locked: true })
        window.dispatchEvent(new Event("focus"))
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(1)
        await h.wrapper.setProps({ locked: false, project: { ...h.project, hasPendingChanges: true } })
        window.dispatchEvent(new Event("focus"))
        document.dispatchEvent(new Event("visibilitychange"))
        await flushPromises()
        expect(h.statisticsRequests).toHaveLength(1)
        expect(h.sent).toHaveLength(0)
    } finally {
        h.cleanup()
    }
})
