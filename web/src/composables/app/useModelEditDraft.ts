/**
 * FilePath    : pbi-utils\web\src\composables\app\useModelEditDraft.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 同步模型编辑选中字段, 保留草稿并阻止失效目标及外部冲突覆盖.
 */
import { computed, ref, watch, type Ref } from "vue"
import type { ProjectSnapshot } from "@/bridge/client"
import { modelEditTargetIdentity, type ModelEditSession } from "@/features/model/modelEditSession"

type DraftSelection = {
    operation: Ref<string>
    table: Ref<string>
    pageId: Ref<string>
    objectName: Ref<string>
    property: Ref<string>
    value: Ref<string>
}
type FieldSource = { present: boolean; value: string }

/**
 * 按当前选择读取快照字段; null 显示空文本, 数值0保留. 不查找同名的其他表/页对象.
 * 返回 present 区分目标不存在与目标字段为空, 仅允许当前操作的已声明属性.
 */
function readField(project: ProjectSnapshot, selection: DraftSelection): FieldSource {
    const { operation, table, pageId, objectName, property } = selection
    let present = false
    let value: string | number | null | undefined
    if (operation.value === "table.set" && property.value === "description") {
        const target = project.model.tables.find((item) => item.name === objectName.value)
        present = !!target
        value = target?.description
    } else if (operation.value === "measure.set" && project.model.tables.some((item) => item.name === table.value)) {
        const target = project.model.measures.find((item) => item.table === table.value && item.name === objectName.value)
        present = !!target && ["description", "formatString", "displayFolder", "expression"].includes(property.value)
        value = target?.[property.value as "description" | "formatString" | "displayFolder" | "expression"]
    } else if (operation.value === "visual.set") {
        const target = project.pages.find((item) => item.id === pageId.value)?.visuals.find((item) => item.id === objectName.value)
        present = !!target && ["x", "y", "width", "height"].includes(property.value)
        value = target?.[property.value as "x" | "y" | "width" | "height"]
    } else if (operation.value === "udf.set" && property.value === "expression") {
        const target = project.model.functions.find((item) => item.name === objectName.value)
        present = !!target
        value = target?.expression
    }
    return { present, value: value === null || value === undefined ? "" : String(value) }
}

/**
 * 维护字段草稿与来源基线; props 提供权威快照和模板标记, selection 为表单状态, restored 为同目标工作区会话.
 * 先恢复来源基线和冲突事实再核验当前字段, 不把恢复的 dirty 输入当作首次选择回填.
 * 返回有效目标、冲突、会话快照及恢复操作; 选择失效清空, 请求失败回读保留同目标输入.
 */
export function useModelEditDraft(props: { project: ProjectSnapshot; template?: string }, selection: DraftSelection, restored?: ModelEditSession) {
    const { operation, table, pageId, objectName, property, value } = selection
    const conflict = ref(restored?.conflict ?? false)
    const source = computed(() => readField(props.project, selection))
    const selectionKey = computed(() => JSON.stringify([operation.value, table.value, pageId.value, objectName.value, property.value]))
    const targetValid = computed(
        () => !!props.template || source.value.present || (operation.value === "udf.set" && property.value === "expression" && !!objectName.value.trim()),
    )
    const valueLimit = computed(() => (operation.value === "table.set" ? 2000 : 30000))
    const canApply = computed(() => targetValid.value && !conflict.value && (operation.value !== "table.set" || value.value.length <= valueLimit.value))
    const currentModel = ref(modelEditTargetIdentity(props.project))
    let currentSelection = restored ? selectionKey.value : ""
    const baselineValue = ref(restored?.sourceValue ?? "")
    const existingTarget = ref(restored?.existingTarget ?? false)
    const editSession = computed<ModelEditSession>(() => ({
        targetIdentity: currentModel.value,
        operation: operation.value,
        table: table.value,
        pageId: pageId.value,
        objectName: objectName.value,
        property: property.value,
        value: value.value,
        sourceValue: baselineValue.value,
        existingTarget: existingTarget.value,
        conflict: conflict.value,
    }))
    let failureReadback: { model: string; selection: string; baselineId: string; source: string } | undefined

    /** 清除选中目标及其草稿, 不把旧值留给接下来选择的目标. */
    function clearTarget(): void {
        objectName.value = ""
        value.value = ""
        conflict.value = false
        currentSelection = ""
        baselineValue.value = ""
        existingTarget.value = false
        failureReadback = undefined
    }

    /** 在失败回读发出 updated 前登记精确模型/选择/基线, 仅该回读可保留原输入. */
    function preserveFailedReadback(project: ProjectSnapshot): void {
        failureReadback = {
            model: modelEditTargetIdentity(project),
            selection: selectionKey.value,
            baselineId: project.baselineId,
            source: readField(project, selection).value,
        }
    }

    /** 用户明确选择恢复时才以当前快照字段替换草稿并接收新基线; 无效目标或无冲突时不修改, 不请求宿主. */
    function loadCurrentValue(): void {
        if (!targetValid.value || !conflict.value) return
        baselineValue.value = source.value.value
        value.value = source.value.value
        existingTarget.value = source.value.present
        conflict.value = false
        failureReadback = undefined
    }

    watch(
        () => ({ model: modelEditTargetIdentity(props.project), key: selectionKey.value, source: source.value, baselineId: props.project.baselineId }),
        (next) => {
            if (props.template) return
            if (next.model !== currentModel.value) {
                currentModel.value = next.model
                table.value = ""
                pageId.value = ""
                clearTarget()
                return
            }
            if (operation.value === "measure.set" && table.value && !props.project.model.tables.some((item) => item.name === table.value)) {
                table.value = ""
                clearTarget()
                return
            }
            if (operation.value === "visual.set" && pageId.value && !props.project.pages.some((item) => item.id === pageId.value)) {
                pageId.value = ""
                clearTarget()
                return
            }
            // UDF 允许输入新名称, 但已存在函数从同目标快照消失时不能静默变成创建请求.
            const removedFunction = operation.value === "udf.set" && next.key === currentSelection && existingTarget.value && !next.source.present
            if (objectName.value && ((!next.source.present && operation.value !== "udf.set") || removedFunction)) {
                clearTarget()
                return
            }
            if (next.key !== currentSelection) {
                currentSelection = next.key
                baselineValue.value = next.source.value
                existingTarget.value = next.source.present
                value.value = next.source.value
                conflict.value = false
                failureReadback = undefined
                return
            }
            const preserve =
                failureReadback?.model === next.model &&
                failureReadback.selection === next.key &&
                failureReadback.baselineId === next.baselineId &&
                failureReadback.source === next.source.value
            if (preserve) failureReadback = undefined
            if (next.source.value !== baselineValue.value) {
                // 未编辑字段跟随权威变化; 自己提交的相等回显接收, 不把用户草稿静默覆盖.
                if (preserve) conflict.value = value.value !== next.source.value
                else if (value.value === baselineValue.value || value.value === next.source.value) {
                    value.value = next.source.value
                    conflict.value = false
                } else conflict.value = true
                baselineValue.value = next.source.value
            }
            existingTarget.value = next.source.present
        },
        { immediate: true },
    )
    return { targetValid, valueLimit, canApply, conflict, preserveFailedReadback, loadCurrentValue, editSession }
}
