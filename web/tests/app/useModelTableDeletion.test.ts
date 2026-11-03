/**
 * FilePath    : pbi-utils\web\tests\app\useModelTableDeletion.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 直接挂载表删除组合函数, 验证大型选择仍经一次编辑提交并保持既有输入预算.
 */
import { defineComponent, h } from "vue"
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { useModelTableDeletion } from "@/composables/app/useModelTableDeletion"
import { MockWorkspace } from "@/bridge/mockWorkspace"
import { controlledBridge, tablesProject } from "./ModelTablesPanel.support"

/** 挂载真实组合函数与类型化桥接, 用最小按钮承接意图, 不渲染数千条 Element Plus 表行. */
function setupDeletion(tables: string[]) {
    const project = tablesProject()
    project.connection = { kind: "desktop", label: "isolated", instanceId: "large-batch" }
    const table = project.model.tables[0]!
    project.model.tables = tables.map((name) => ({ ...structuredClone(table), name }))
    const transport = controlledBridge(project)
    const updated = vi.fn()
    const wrapper = mount(
        defineComponent({
            setup() {
                const deletion = useModelTableDeletion(
                    { project, bridge: transport.bridge },
                    () => tables,
                    () => project.baselineId,
                    () => false,
                    updated,
                )
                return () => h("button", { disabled: deletion.busy.value, onClick: () => void deletion.deleteSelected() }, "delete")
            },
        }),
    )
    return {
        project,
        updated,
        wrapper,
        ...transport,
        cleanup: () => {
            wrapper.unmount()
            transport.bridge.dispose()
        },
    }
}

it.each([129, 2000])("%s 张所选表只发送一个完整删除批次, 不按旧度量值上限拆分", async (count) => {
    const tables = Array.from({ length: count }, (_, index) => `T${index}`)
    const { project, updated, wrapper, sent, send, cleanup } = setupDeletion(tables)
    try {
        await wrapper.get("button").trigger("click")
        expect(sent).toHaveLength(1)
        expect(sent[0]!.method).toBe("project.inspect")
        send({ id: sent[0]!.id, result: project })
        await flushPromises()
        expect(sent).toHaveLength(2)
        expect(sent[1]!.method).toBe("model.edit")
        expect(sent[1]!.params.operation).toBe("table.deleteBatch")
        expect(JSON.parse(sent[1]!.params.value as string)).toEqual({ projectId: project.projectId, baselineId: project.baselineId, tables })
        send({ id: sent[1]!.id, result: { ...project, baselineId: "saved", model: { ...project.model, tables: [] } } })
        await flushPromises()
        expect(updated).toHaveBeenCalledTimes(1)
        expect(sent.filter((request) => request.method === "model.edit")).toHaveLength(1)
    } finally {
        cleanup()
    }
})

it.each(["count", "characters"])("超过 %s 预算时整批拒绝, 不发送截断或分片请求", async (limit) => {
    const tables =
        limit === "count" ? Array.from({ length: 2001 }, (_, index) => `T${index}`) : Array.from({ length: 200 }, (_, index) => `${"T".repeat(195)}${index}`)
    const { wrapper, sent, cleanup } = setupDeletion(tables)
    try {
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(sent).toHaveLength(0)
        expect(wrapper.get("button").attributes("disabled")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it.each(["accepted", "count", "characters"])("显式模拟共享 %s 表数和 JSON 预算", (limit) => {
    const tables =
        limit === "characters"
            ? Array.from({ length: 200 }, (_, index) => `${"T".repeat(195)}${index}`)
            : Array.from({ length: limit === "accepted" ? 2000 : 2001 }, (_, index) => `T${index}`)
    const project = tablesProject()
    project.connection = { kind: "desktop", label: "isolated" }
    const table = project.model.tables[0]!
    project.model.tables = tables.map((name) => ({ ...structuredClone(table), name }))
    const responses: unknown[] = []
    const workspace = new MockWorkspace((message) => responses.push(message))
    workspace.currentProject = project
    expect(
        workspace.handle({
            id: "large-batch",
            method: "model.edit",
            params: { operation: "table.deleteBatch", value: JSON.stringify({ projectId: project.projectId, baselineId: project.baselineId, tables }) },
        }),
    ).toBe(true)
    if (limit === "accepted") {
        expect(workspace.currentProject.model.tables).toHaveLength(0)
        expect(responses).toEqual([
            expect.objectContaining({
                result: expect.objectContaining({ saveResult: expect.objectContaining({ message: expect.stringContaining("浏览器模拟") }) }),
            }),
        ])
    } else {
        expect(workspace.currentProject.model.tables).toHaveLength(tables.length)
        expect(responses).toEqual([expect.objectContaining({ error: { code: "VALIDATION_ERROR", message: "VALIDATION_ERROR" } })])
    }
})
