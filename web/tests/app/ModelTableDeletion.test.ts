/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableDeletion.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证所选表删除走单批次编辑, 防止旧基线重绑、远端写入、取消提前解锁与迟到污染.
 */
import { expect, it } from "vitest"
import { flushPromises } from "@vue/test-utils"
import { BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import { selectTable, setupTables, tablesProject } from "./ModelTablesPanel.support"

/** 创建本测试可编辑的独立本机目标, 不代表发现真实 Desktop 实例. */
function desktop(): ProjectSnapshot {
    const project = tablesProject()
    project.connection = { kind: "desktop", label: "isolated", instanceId: "isolated-desktop", modelId: "model" }
    return project
}

it("只删除所选表, 先读权威同基线再发一个完整批次并保持共享锁", async () => {
    const test = await setupTables({ project: desktop() })
    const { wrapper, project, sent, send, cleanup } = test
    try {
        expect(wrapper.get(".table-delete-button").attributes("disabled")).toBeDefined()
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-delete-button").trigger("click")
        expect(sent).toHaveLength(1)
        expect(sent[0]!.method).toBe("project.inspect")
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        send({ id: sent[0]!.id, result: project })
        await flushPromises()
        expect(sent).toHaveLength(2)
        expect(sent[1]).toMatchObject({
            method: "model.edit",
            params: { operation: "table.deleteBatch", objectName: "tables", property: "items", table: "", pageId: "" },
        })
        expect(JSON.parse(sent[1]!.params.value as string)).toEqual({ projectId: project.projectId, baselineId: project.baselineId, tables: ["销售"] })
        const saved = { ...project, baselineId: "deleted", model: { ...project.model, tables: [project.model.tables[1]!] } }
        send({ id: sent[1]!.id, result: saved })
        await flushPromises()
        expect(wrapper.emitted("updated")?.at(-1)).toEqual([saved])
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        await wrapper.setProps({ project: saved })
        await flushPromises()
        expect(wrapper.find('[aria-label="选择表 销售"]').exists()).toBe(false)
        expect(wrapper.get(".table-delete-button").attributes("disabled")).toBeDefined()
    } finally {
        cleanup()
    }
})

it.each(["ssas-xmla", "powerbi-service"] as const)("%s 只读时隐藏删除, 保留可用导出", async (kind) => {
    const project = tablesProject()
    project.connection = { kind, label: kind }
    const { wrapper, sent, cleanup } = await setupTables({ project })
    try {
        await selectTable(wrapper, "销售")
        expect(wrapper.find(".table-delete-button").exists()).toBe(false)
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeUndefined()
        expect(sent).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it("本地离线和无数据列的表可选择删除, 导出仍保持真实不可用", async () => {
    const project = tablesProject()
    project.connection = { kind: "pbip", label: "PBIP" }
    project.model.tables[0]!.columns = []
    const { wrapper, sent, send, cleanup } = await setupTables({ project })
    try {
        await selectTable(wrapper, "销售")
        expect(wrapper.get(".table-delete-button").attributes("disabled")).toBeUndefined()
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        await wrapper.get(".table-export-button").trigger("click")
        expect(sent).toHaveLength(0)
        await wrapper.get(".table-delete-button").trigger("click")
        await flushPromises()
        expect(sent[0]!.method).toBe("model.edit")
        expect(document.body.querySelector<HTMLButtonElement>(".operation-message button")).not.toBeNull()
        send({ id: sent[0]!.id, result: project })
        await flushPromises()
    } finally {
        cleanup()
    }
})

it.each(["selected-baseline", "authoritative-baseline"])("%s 变化拒绝破坏性选择自动重绑", async (change) => {
    const { wrapper, project, sent, send, cleanup } = await setupTables({ project: desktop() })
    try {
        await selectTable(wrapper, "销售")
        const fresh = { ...project, baselineId: "external" }
        if (change === "selected-baseline") {
            await wrapper.setProps({ project: fresh })
            await flushPromises()
        }
        await wrapper.get(".table-delete-button").trigger("click")
        await flushPromises()
        if (change === "authoritative-baseline") {
            expect(sent[0]!.method).toBe("project.inspect")
            send({ id: sent[0]!.id, result: fresh })
            await flushPromises()
            expect(wrapper.emitted("updated")?.at(-1)).toEqual([fresh])
        }
        expect(sent.filter((request) => request.method === "model.edit")).toHaveLength(0)
        expect(document.body.textContent).toContain("模型已变化, 请刷新后重新选择要删除的表")
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 销售"] input').element.checked).toBe(true)
    } finally {
        cleanup()
    }
})

it("取消确认后仍等待原请求终态, 不发删除批次; 提交阶段不再提供可用取消", async () => {
    const { wrapper, project, sent, send, cleanup } = await setupTables({ project: desktop() })
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-delete-button").trigger("click")
        await flushPromises()
        document.body.querySelector<HTMLButtonElement>(".operation-message button")!.click()
        await flushPromises()
        expect(sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: sent[0]!.id } })
        send({ id: sent[1]!.id, result: { accepted: true } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        send({ id: sent[0]!.id, result: project })
        await flushPromises()
        expect(sent.filter((request) => request.method === "model.edit")).toHaveLength(0)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        await wrapper.get(".table-delete-button").trigger("click")
        send({ id: sent[2]!.id, result: project })
        await flushPromises()
        send({ requestPhase: { requestId: sent[3]!.id, phase: "committing" } })
        await flushPromises()
        expect(document.body.querySelector<HTMLButtonElement>(".operation-message button")!.disabled).toBe(true)
        send({ id: sent[3]!.id, result: project })
        await flushPromises()
    } finally {
        cleanup()
    }
})

it("基线变化后重新选择单张表不会静默继承旧选择", async () => {
    const { wrapper, project, sent, send, cleanup } = await setupTables({ project: desktop() })
    try {
        await selectTable(wrapper, "销售")
        const fresh = { ...project, baselineId: "external" }
        await wrapper.setProps({ project: fresh })
        await flushPromises()
        await selectTable(wrapper, "另一张表")
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 销售"] input').element.checked).toBe(false)
        await wrapper.get(".table-delete-button").trigger("click")
        send({ id: sent[0]!.id, result: fresh })
        await flushPromises()
        expect(JSON.parse(sent[1]!.params.value as string)).toEqual({ projectId: fresh.projectId, baselineId: "external", tables: ["另一张表"] })
        send({ id: sent[1]!.id, result: fresh })
        await flushPromises()
    } finally {
        cleanup()
    }
})

it("写失败保留选择, 回读未知提交状态, 不重放删除", async () => {
    const { wrapper, project, sent, send, cleanup } = await setupTables({ project: desktop() })
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-delete-button").trigger("click")
        send({ id: sent[0]!.id, result: project })
        await flushPromises()
        send({ id: sent[1]!.id, error: { code: "EXECUTION_NEEDS_VERIFICATION", message: "需要核验" } })
        await flushPromises()
        expect(sent[2]!.method).toBe("project.inspect")
        const pending = { ...project, hasPendingChanges: true }
        send({ id: sent[2]!.id, result: pending })
        await flushPromises()
        expect(wrapper.emitted("updated")?.at(-1)).toEqual([pending])
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 销售"] input').element.checked).toBe(true)
        expect(sent.filter((request) => request.method === "model.edit")).toHaveLength(1)
    } finally {
        cleanup()
    }
})

it.each(["target", "unmount"])("%s 后旧请求不更新新目标或报告完成", async (change) => {
    const { wrapper, project, sent, send, unmount, cleanup } = await setupTables({ project: desktop() })
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-delete-button").trigger("click")
        send({ id: sent[0]!.id, result: project })
        await flushPromises()
        if (change === "target") await wrapper.setProps({ project: { ...project, projectId: "different" } })
        else unmount()
        expect(sent[2]).toMatchObject({ method: "request.cancel", params: { requestId: sent[1]!.id } })
        send({ id: sent[2]!.id, result: { accepted: true } })
        send({ id: sent[1]!.id, result: { ...project, model: { ...project.model, tables: [] } } })
        await flushPromises()
        expect(wrapper.emitted("updated")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it("删除请求收到取消确认仍锁定至原终态与权威回读结束", async () => {
    const { wrapper, project, sent, send, cleanup } = await setupTables({ project: desktop() })
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-delete-button").trigger("click")
        send({ id: sent[0]!.id, result: project })
        await flushPromises()
        document.body.querySelector<HTMLButtonElement>(".operation-message button")!.click()
        await flushPromises()
        expect(sent[2]).toMatchObject({ method: "request.cancel", params: { requestId: sent[1]!.id } })
        send({ id: sent[2]!.id, result: { accepted: true } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        send({ id: sent[1]!.id, error: { code: "CANCELLED", message: "已取消保存, 未写入模型" } })
        await flushPromises()
        expect(sent[3]!.method).toBe("project.inspect")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        send({ id: sent[3]!.id, result: project })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(wrapper.get<HTMLInputElement>('[aria-label="选择表 销售"] input').element.checked).toBe(true)
    } finally {
        cleanup()
    }
})

it("显式浏览器模拟删除所选表及其度量值, 明确返回未提交真实模型的模拟回执", async () => {
    const bridge = new BridgeClient(new MockTransport())
    try {
        const project = await bridge.request("project.inspect", {}).promise
        const result = await bridge.request("model.edit", {
            operation: "table.deleteBatch",
            objectName: "tables",
            property: "items",
            value: JSON.stringify({ projectId: project.projectId, baselineId: project.baselineId, tables: ["销售"] }),
            table: "",
            pageId: "",
        }).promise
        expect(result.model.tables.some((table) => table.name === "销售")).toBe(false)
        expect(result.model.measures.some((measure) => measure.table === "销售")).toBe(false)
        expect(result.saveResult?.message).toContain("浏览器模拟保存结果")
        expect(previewProject.model.tables.some((table) => table.name === "销售")).toBe(true)
    } finally {
        bridge.dispose()
    }
})
