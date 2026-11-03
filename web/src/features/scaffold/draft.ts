/**
 * FilePath    : pbi-utils\web\src\features\scaffold\draft.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 串行保存宿主草稿与有界分项更新, 提供轻量表单校验.
 */
import type { BridgeClient } from "@/bridge/client"
import type { DraftIdentity, TemplateMeasureDefinition, UdfSelection } from "./contracts"
import { isTemplateMeasureCategory } from "./dataCategories"
/** 顺序消费宿主返回版本; 大集合按条更新, 任一失败立即停止, 不继续预览. */
export async function saveDraft(
    bridge: BridgeClient,
    identity: DraftIdentity | undefined,
    base: unknown,
    sections: Record<string, unknown>,
    onSaved?: (identity: DraftIdentity) => void,
): Promise<DraftIdentity> {
    let current = identity
    /** 每次 mutation 都使用上一响应身份, 不并发覆盖草稿. */
    async function update(section: string, value: unknown): Promise<void> {
        current = await bridge.request("scaffold.updateDraft", { ...current, section, value }).promise
        onSaved?.(current)
    }
    await update("base", base)
    const updates: { section: string; value: unknown }[] = []
    for (const [section, value] of Object.entries(sections)) {
        const size = new TextEncoder().encode(JSON.stringify(value)).length
        if (size < 48000 || (section === "calendar" && size < 3 * 1024 * 1024)) updates.push({ section, value })
        else if (section === "measures") {
            updates.push({ section: "measures", value: [] })
            for (const [index, item] of (value as TemplateMeasureDefinition[]).entries()) updates.push({ section: "measure", value: { index, item } })
        } else if (section === "udfs") {
            updates.push({ section: "udfs", value: [] })
            for (const selection of value as UdfSelection[])
                for (const functionName of selection.functions)
                    updates.push({ section: "udfFunction", value: { ...selection, functions: undefined, functionName, selected: true } })
        } else throw new Error("SCAFFOLD_MESSAGE_TOO_LARGE")
    }
    // 每项依赖上一项返回的 revision, Promise 链表达此顺序且失败立即停止.
    await updates.reduce((queue, item) => queue.then(() => update(item.section, item.value)), Promise.resolve())
    return current!
}
/** 校验可编辑六字段, 返回首个有问题的行和可翻译键. */
export function measureError(items: TemplateMeasureDefinition[]): { key: string; row: number } | undefined {
    const names = new Set<string>()
    for (const [index, item] of items.entries()) {
        const key = !item.name.trim()
            ? "nameRequired"
            : names.has(item.name.toLocaleLowerCase())
              ? "duplicate"
              : !isTemplateMeasureCategory(item.dataCategory)
                ? "category"
                : new TextEncoder().encode(item.value).length > 32768
                  ? "valueSize"
                  : ""
        if (key) return { key, row: index + 1 }
        names.add(item.name.toLocaleLowerCase())
    }
}
