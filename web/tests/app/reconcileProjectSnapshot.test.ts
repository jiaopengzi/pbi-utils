/**
 * FilePath    : pbi-utils\web\tests\app\reconcileProjectSnapshot.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型刷新结构共享、完整权威值和宿主身份隔离.
 */

import { expect, it } from "vitest"
import type { ProjectSnapshot } from "@/bridge/client"
import { previewProject } from "@/bridge/preview"
import { reconcileProjectSnapshot } from "@/app/reconcileProjectSnapshot"

/** 每次建立独立的 JSON 快照, 通过第二个度量值验证数组中的局部变化. */
function snapshot(): ProjectSnapshot {
    const value = structuredClone(previewProject)
    value.connection = { kind: "desktop", label: "本地模型", instanceId: "host-instance" }
    value.model.measures.push({ ...value.model.measures[0]!, name: "订单量", expression: "COUNTROWS('销售')" })
    return value
}

/** 无变化刷新复用根对象, 首次加载直接采用权威结果. */
it("相同快照复用根引用, 首次加载直接返回新快照", () => {
    const current = snapshot()
    const incoming = structuredClone(current)
    expect(reconcileProjectSnapshot(current, incoming)).toBe(current)
    expect(reconcileProjectSnapshot(undefined, incoming)).toBe(incoming)
})

/** 单度量变化只替换其上级分支, 其余表、页及条目保持引用. */
it("单度量变化局部更新并完整保留新表达式", () => {
    const current = snapshot()
    const incoming = structuredClone(current)
    incoming.model.measures[1]!.expression = "COUNTROWS('销售') + 1"
    const result = reconcileProjectSnapshot(current, incoming)
    expect(result).toEqual(incoming)
    expect(result).not.toBe(current)
    expect(result.model).not.toBe(current.model)
    expect(result.model.measures).not.toBe(current.model.measures)
    expect(result.model.measures[0]).toBe(current.model.measures[0])
    expect(result.model.measures[1]).not.toBe(current.model.measures[1])
    expect(result.model.tables).toBe(current.model.tables)
    expect(result.pages).toBe(current.pages)
    expect(result.connection).toBe(current.connection)
    expect(current.model.measures[1]!.expression).toBe("COUNTROWS('销售')")
})

/** 数组新增、删除及换序严格采用新结果, 不从名称猜测复用条目. */
it.each(["append", "remove", "reorder"] as const)("数组 %s 保留新长度和顺序", (change) => {
    const current = snapshot()
    const incoming = structuredClone(current)
    if (change === "append") incoming.model.measures.push({ ...incoming.model.measures[0]!, name: "新增" })
    if (change === "remove") incoming.model.measures.shift()
    if (change === "reorder") incoming.model.measures.reverse()
    const result = reconcileProjectSnapshot(current, incoming)
    expect(result).toEqual(incoming)
    expect(result.model.measures).not.toBe(current.model.measures)
    expect(result.model.tables).toBe(current.model.tables)
    if (change === "append") expect(result.model.measures[0]).toBe(current.model.measures[0])
    else expect(result.model.measures[0]).not.toBe(current.model.measures[1])
})

/** 空值、缺失可选字段及空数组不会回退为旧快照中的值. */
it("null、字段删除和空数组采用权威新值", () => {
    const current = snapshot()
    current.hasPendingChanges = true
    current.saveResult = { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "待保存", errorCode: "FILE_PENDING" }
    const incoming = structuredClone(current)
    incoming.model.measures[0]!.description = null
    incoming.saveResult = null
    delete incoming.hasPendingChanges
    incoming.pages = []
    const result = reconcileProjectSnapshot(current, incoming)
    expect(result).toEqual(incoming)
    expect(result.model.measures[0]!.description).toBeNull()
    expect(result.saveResult).toBeNull()
    expect(Object.hasOwn(result, "hasPendingChanges")).toBe(false)
    expect(result.pages).toEqual([])
})

/** 仅元数据变化也必须发出新根, 不吞掉基线、保存或待核验事实. */
it("基线和保存状态变化生效, 模型内容引用仍可复用", () => {
    const current = snapshot()
    current.hasPendingChanges = true
    current.saveResult = { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "待保存", errorCode: "FILE_PENDING" }
    const incoming = structuredClone(current)
    incoming.baselineId = "new-authoritative-baseline"
    incoming.hasPendingChanges = false
    incoming.saveResult = { modelSaved: true, fileSaved: true, desktopSynchronized: true, message: "已保存", errorCode: null }
    const result = reconcileProjectSnapshot(current, incoming)
    expect(result).toEqual(incoming)
    expect(result).not.toBe(current)
    expect(result.model).toBe(current.model)
    expect(result.pages).toBe(current.pages)
    expect(result.baselineId).toBe("new-authoritative-baseline")
    expect(result.saveResult).not.toBe(current.saveResult)
})

/** 宿主身份的任一字段变化都隔离旧模型, 不复用看起来相同的内容. */
it.each(["projectId", "projectPath", "kind", "instanceId", "modelId"] as const)("身份字段 %s 变化直接采用新快照", (field) => {
    const current = snapshot()
    const incoming = structuredClone(current)
    if (field === "projectId") incoming.projectId = "another-project"
    if (field === "projectPath") incoming.projectPath = "another-path"
    if (field === "kind") incoming.connection!.kind = "powerbi-service"
    if (field === "instanceId") incoming.connection!.instanceId = "another-instance"
    if (field === "modelId") incoming.connection!.modelId = "another-model"
    const result = reconcileProjectSnapshot(current, incoming)
    expect(result).toBe(incoming)
    expect(result.model).not.toBe(current.model)
})
