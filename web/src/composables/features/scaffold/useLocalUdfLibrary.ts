/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useLocalUdfLibrary.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 宿主本地函数库读取、选择和基线保护的新增编辑删除.
 */
import { nextTick } from "vue"
import type { LocalUdfDefinition, LocalUdfEntry, UdfSelection } from "@/features/scaffold/contracts"
import type { UdfRequests } from "@/composables/features/scaffold/useUdfRequests"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
import { localUdfPackageKey } from "@/features/scaffold/udfLocalPackages"
/** 名称只用于同一逻辑包的草稿归组; 宿主读写与依赖身份仍使用精确 GUID 和基线. */
function samePackage(selection: UdfSelection, item: LocalUdfEntry): boolean {
    return (
        selection.source === "local" &&
        (selection.packageId === item.id || selection.functions.some((name) => localUdfPackageKey(name) === localUdfPackageKey(item.definition.name)))
    )
}
/** 从权威条目建立精确选择, 不以名称派生文件地址、稳定 ID 或写授权. */
function selectionFor(item: LocalUdfEntry): UdfSelection {
    return {
        source: "local",
        packageId: item.id,
        version: item.definition.version,
        functions: [item.definition.name],
        baseline: item.baseline,
    }
}
/** 所有条目由宿主读取; 管理动作保留精确 ID、基线和显式删除确认.
 * @param state 当前本地条目、编辑草稿和显式选择.
 * @param requests 当前挂载的串行请求入口.
 * @returns 本地读取、选择和CRUD动作; 失败保留原条目与选择.
 */
export function useLocalUdfLibrary(state: UdfState, requests: UdfRequests, commitSelection: (next: UdfSelection[]) => Promise<boolean>) {
    const { model, locals, target, deleteTarget, definition, editor, manager, error } = state
    const { t } = useI18n()
    const { call } = requests
    /** 读取本地库, 不从网页文件系统构造条目. */
    async function listLocal(): Promise<void> {
        const result = await call("udf.localList", {}, true)
        if (result) locals.value = result.items
    }
    /** 同名包仅保留一个明确 tag; 整轮依赖验证成功才替换原精确 ID/基线和当前 tag. */
    async function toggleLocal(item: LocalUdfEntry, checked: unknown): Promise<void> {
        if (state.locked.value || state.uncertain.value) return
        const remaining = model.value.filter((selection) => !samePackage(selection, item))
        if (!checked) model.value = remaining
        else if (await commitSelection([...remaining, selectionFor(item)]))
            state.localPackageTags.value = { ...state.localPackageTags.value, [localUdfPackageKey(item.definition.name)]: item.id }
    }
    /** 当前 tag 切换在已选组先核验依赖, 失败/取消保留旧选择和 tag; 未选组仅更新展示草稿, 不访问宿主. */
    async function changeVersion(next: LocalUdfEntry, previous: LocalUdfEntry): Promise<void> {
        if (state.busy.value || state.uncertain.value || (state.disabled() && !manager.value) || state.lifecycle.disposed) return
        const key = localUdfPackageKey(next.definition.name)
        if (key !== localUdfPackageKey(previous.definition.name) || !locals.value.some((item) => item.id === next.id && item.baseline === next.baseline)) return
        const selected = model.value.find((item) => samePackage(item, previous))
        if (selected) {
            const remaining = model.value.filter((item) => !samePackage(item, previous))
            if (!(await commitSelection([...remaining, selectionFor(next)]))) return
        }
        state.localPackageTags.value = { ...state.localPackageTags.value, [key]: next.id }
    }
    /** 将本显示页按精确 ID/版本/基线一次合并, 保留隐藏和其他来源选择; 依赖冲突或取消不产生部分选择.
     * @param items 本地列表实际显示页每个包当前 tag 的完整条目, 同名多 tag 的歧义候选整批拒绝.
     * @returns 完整依赖核验完成后更新草稿, 失败由共享请求入口反馈并保留旧选择.
     */
    async function selectPage(items: LocalUdfEntry[]): Promise<void> {
        if (state.locked.value || state.uncertain.value || !items.length) return
        const entries = new Map(items.map((item) => [item.id, item]))
        const keys = new Set<string>()
        for (const item of entries.values()) {
            const key = localUdfPackageKey(item.definition.name)
            if (keys.has(key)) {
                error.value = t("scaffold.dependencyPreviewInvalid")
                return
            }
            keys.add(key)
        }
        const next = model.value.filter((item) => ![...entries.values()].some((entry) => samePackage(item, entry)))
        for (const item of entries.values()) next.push(selectionFor(item))
        if (await commitSelection(next))
            state.localPackageTags.value = {
                ...state.localPackageTags.value,
                ...Object.fromEntries([...entries.values()].map((item) => [localUdfPackageKey(item.definition.name), item.id])),
            }
    }
    /** 获取当前条目再编辑, 目标携带当前宿主基线. */
    async function edit(item?: LocalUdfEntry): Promise<void> {
        const current = item ? await call("udf.localGet", { id: item.id }) : undefined
        if (item && !current) return
        target.value = current
        Object.assign(
            definition,
            structuredClone(current?.definition ?? { schemaVersion: 1, name: "", version: "1.0.0", expression: "", description: "", dependencies: [] }),
        )
        editor.value = true
    }
    /** 保存前检查 UTF-8 长度, 宿主独立执行严格 schema 和基线验证. */
    async function save(): Promise<void> {
        if (!definition.name.trim() || !definition.version.trim() || !definition.expression.trim()) {
            error.value = t("scaffold.invalid")
            return
        }
        if (new TextEncoder().encode(definition.expression).length > 32768) {
            error.value = t("scaffold.functionSize")
            return
        }
        const value = await call(
            "udf.localSave",
            {
                ...(target.value ? { id: target.value.id, baseline: target.value.baseline } : {}),
                definition: JSON.parse(JSON.stringify(definition)) as LocalUdfDefinition,
            },
            false,
            ["UDF_LOCAL_VERSION_EXISTS"],
        )
        if (value) {
            // 权威写入回执立即替换可用条目, 后续全量复读失败或延迟不能让旧版本继续可选.
            locals.value = [...locals.value.filter((item) => item.id !== value.id), value]
            state.localPackageTags.value = { ...state.localPackageTags.value, [localUdfPackageKey(value.definition.name)]: value.id }
            editor.value = false
            model.value = model.value.filter((item) => item.source !== "local" || item.packageId !== value.id)
            await nextTick()
            await listLocal()
        } else if (state.lastErrorCode.value === "UDF_LOCAL_VERSION_EXISTS") error.value = t("scaffold.localVersionExists")
    }
    /** 删除仅使用确认时冻结的 ID/基线, 成功后取消相关选择. */
    async function remove(): Promise<void> {
        const current = deleteTarget.value
        if (!current) return
        const result = await call("udf.localDelete", { id: current.id, baseline: current.baseline, confirm: true })
        if (result?.deleted) {
            locals.value = locals.value.filter((item) => item.id !== current.id)
            state.localPackageTags.value = Object.fromEntries(Object.entries(state.localPackageTags.value).filter(([, id]) => id !== current.id))
            deleteTarget.value = undefined
            model.value = model.value.filter((item) => item.source !== "local" || item.packageId !== current.id)
            await nextTick()
            await listLocal()
        }
    }
    /** 打开管理页同时读取宿主库. */
    async function openManager(): Promise<void> {
        manager.value = true
        await listLocal()
    }
    return { listLocal, toggleLocal, changeVersion, selectPage, edit, save, remove, openManager }
}
export type LocalUdfLibrary = ReturnType<typeof useLocalUdfLibrary>
