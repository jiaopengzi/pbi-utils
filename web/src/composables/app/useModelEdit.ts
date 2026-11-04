/**
 * FilePath    : pbi-utils\web\src\composables\app\useModelEdit.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 当前模型编辑草稿联动、单次提交与权威保存回读.
 */
import { stringSelection } from "@/ui/forms"
import { hostText } from "@/i18n"
import { useModelEditDraft } from "./useModelEditDraft"
import { modelEditTargetIdentity, type ModelEditSelection, type ModelEditSession } from "@/features/model/modelEditSession"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { computed, nextTick, onBeforeUnmount, onMounted, onUnmounted, ref, shallowRef, watch, type TemplateRef } from "vue"
import type { BridgeClient, BridgePhase, ProjectSnapshot } from "@/bridge/client"
import { validateForm, yearRule } from "@/ui/forms"
import type { FormInstance, InputInstance } from "element-plus"
import { hasPendingModelUpdate, isConnectedDesktopProject, isModelUpdateComplete } from "@/bridge/modelAccess"

export type ModelEditProps = {
    bridge: BridgeClient
    project: ProjectSnapshot
    template?: "calendar" | "period"
    initialOperation?: string
    locked?: boolean
    active?: boolean
    editSession?: ModelEditSession
}
export type ModelEditEmit = {
    (event: "busy", ...args: [value: boolean]): void
    (event: "updated", ...args: [value: ProjectSnapshot]): void
    (event: "draft-change", ...args: [value: ModelEditSession]): void
}

/** 管理当前模型草稿、文本选区和单次提交; props 为响应式输入, emit 转发权威结果, form 用于校验, valueInput 提供真实 textarea; 返回视图绑定与操作, 请求失败保留原错误和输入. */
export function useModelEdit(props: ModelEditProps, emit: ModelEditEmit, form: TemplateRef<FormInstance>, valueInput: TemplateRef<InputInstance>) {
    const { t } = useI18n()
    const { showFeedback } = useFeedback()
    const requestId = ref("")
    const active = computed(() => props.active !== false)
    // 只在同一精确目标和操作下恢复工作区会话; 请求、busy和保存回执继续由本次挂载拥有.
    const restored =
        !props.template &&
        props.editSession?.targetIdentity === modelEditTargetIdentity(props.project) &&
        props.editSession.operation === (props.initialOperation || "measure.set")
            ? props.editSession
            : undefined
    const operation = ref(
        props.template === "calendar" ? "calendar.create" : props.template === "period" ? "period.generate" : props.initialOperation || "measure.set",
    )
    const table = ref(restored?.table ?? "")
    const objectName = ref(restored?.objectName ?? (props.template === "calendar" ? "Calendar" : ""))
    const property = ref(restored?.property ?? (props.initialOperation === "udf.set" ? "expression" : "description"))
    const value = ref(restored?.value ?? "")
    const startYear = ref(2024)
    const endYear = ref(2027)
    const dateTable = ref("")
    const dateColumn = ref("")
    const measureTable = ref("")
    const templates = ref(["YTD", "PY", "YOY"])
    const digitsN = ref(2)
    const digitsP = ref(2)
    const folder = ref("时间智能")
    const config = computed(() =>
        props.template === "calendar"
            ? JSON.stringify({ startYear: startYear.value, endYear: endYear.value })
            : JSON.stringify({
                  dateTable: dateTable.value,
                  dateColumn: dateColumn.value,
                  measureTable: measureTable.value,
                  templates: templates.value,
                  digitsN: digitsN.value,
                  digitsP: digitsP.value,
                  folder: folder.value,
              }),
    )
    const formModel = computed(() => ({
        objectName: objectName.value,
        table: table.value,
        dateTable: dateTable.value,
        dateColumn: dateColumn.value,
        measureTable: measureTable.value,
        startYear: startYear.value,
        endYear: endYear.value,
        templates: templates.value,
    }))
    const years = yearRule(() => [startYear.value, endYear.value])
    const busy = ref(false)
    const cancelling = ref(false)
    const committed = ref(false)
    const error = ref("")
    // 进度与取消入口固定在全局浮层, 保存期间不向原表单追加节点.
    useOperationMessage(() =>
        active.value && busy.value
            ? {
                  message: `${t("model.applyEdits")} · ${t("model.processingWait")}`,
                  cancel: requestId.value ? cancel : undefined,
                  cancelLabel: t("model.cancel"),
                  cancelDisabled: cancelling.value || committed.value,
              }
            : undefined,
    )
    const properties = computed(() =>
        operation.value === "measure.set"
            ? ["description", "formatString", "displayFolder", "expression"]
            : operation.value === "udf.set"
              ? ["expression"]
              : ["description"],
    )
    const names = computed(() =>
        operation.value === "measure.set"
            ? props.project.model.measures.filter((m) => m.table === table.value).map((m) => m.name)
            : operation.value === "table.set"
              ? props.project.model.tables.map((modelTable) => modelTable.name)
              : props.project.model.functions.map((f) => f.name),
    )
    /** 属性选项在语言切换后重新计算, 不改变属性键或当前编辑值. */
    const labels = computed<Record<string, string>>(() => ({
        description: t("model.description"),
        formatString: t("model.formatString"),
        displayFolder: t("model.displayFolder"),
        expression: t("model.expression"),
    }))
    watch(operation, () => {
        objectName.value = ""
        property.value = properties.value[0]!
    })
    const { targetValid, valueLimit, canApply, conflict, preserveFailedReadback, loadCurrentValue, editSession } = useModelEditDraft(
        props,
        {
            operation,
            table,
            objectName,
            property,
            value,
        },
        restored,
    )
    const selectionIdentity = computed(() =>
        JSON.stringify([modelEditTargetIdentity(props.project), operation.value, table.value, objectName.value, property.value]),
    )
    const selection = shallowRef<ModelEditSelection | undefined>(restored?.selection)
    let disposed = false
    let renderedField = selectionIdentity.value
    let renderedValue = value.value
    /** 只关联仍属于当前字段和原文的选区; 目标、属性或原文改变时不复用旧位置. */
    function currentSelection(): ModelEditSelection | undefined {
        const saved = selection.value
        return saved?.fieldIdentity === selectionIdentity.value && saved.value === value.value ? saved : undefined
    }
    const sessionWithSelection = computed<ModelEditSession>(() => ({ ...editSession.value, selection: currentSelection() }))
    // 同步转交输入、来源和选区事实, 父工作区仅接收当前表说明会话; 不持有隐藏模型页或请求.
    watch(sessionWithSelection, (session) => emit("draft-change", session), { immediate: true, flush: "sync" })
    watch([selectionIdentity, value], () => (selection.value = undefined), { flush: "sync" })
    watch(
        [selectionIdentity, value],
        () => {
            renderedField = selectionIdentity.value
            renderedValue = value.value
        },
        { flush: "post" },
    )
    /** 从实际 textarea 捕获 select/blur/卸载前的选区; DOM 尚属旧字段或文本时忽略, 不请求宿主. */
    function captureSelection(): void {
        const input = valueInput.value?.textarea
        if (
            disposed ||
            !input ||
            !targetValid.value ||
            renderedField !== selectionIdentity.value ||
            renderedValue !== value.value ||
            input.value !== value.value
        )
            return
        selection.value = {
            fieldIdentity: selectionIdentity.value,
            value: value.value,
            start: input.selectionStart,
            end: input.selectionEnd,
            direction: input.selectionDirection,
        }
    }
    // Element Plus 不声明 select 事件, 只对其公开的真实 textarea 绑定原生监听, 控件替换时释放旧节点.
    watch(
        () => valueInput.value?.textarea,
        (input, _previous, onCleanup) => {
            input?.addEventListener("select", captureSelection)
            input?.addEventListener("blur", captureSelection)
            onCleanup(() => {
                input?.removeEventListener("select", captureSelection)
                input?.removeEventListener("blur", captureSelection)
            })
        },
        { flush: "post" },
    )
    /** 首次挂载的文本完成回填后恢复原生选区; 期间用户选择、字段/文本变化及卸载均撤销本次恢复, 不抢焦点. */
    async function restoreSelection(): Promise<void> {
        const saved = currentSelection()
        const input = valueInput.value?.textarea
        if (!saved || !input || !targetValid.value) return
        const initialRange = [input.selectionStart, input.selectionEnd, input.selectionDirection]
        await nextTick()
        if (
            disposed ||
            selection.value !== saved ||
            currentSelection() !== saved ||
            valueInput.value?.textarea !== input ||
            input.value !== saved.value ||
            initialRange.some((part, index) => part !== [input.selectionStart, input.selectionEnd, input.selectionDirection][index]) ||
            !Number.isInteger(saved.start) ||
            !Number.isInteger(saved.end) ||
            saved.start < 0 ||
            saved.end < saved.start ||
            saved.end > input.value.length
        )
            return
        input.setSelectionRange(saved.start, saved.end, saved.direction)
    }
    onMounted(() => void restoreSelection())
    onBeforeUnmount(() => {
        captureSelection()
        disposed = true
    })
    /** 明确放弃冲突草稿并载入当前字段; 临时操作锁或待处理保存期间保持原输入, 不调用桥接. */
    function reloadValue(): void {
        if (disposed || !active.value || props.locked || busy.value || hasPendingModelUpdate(props.project)) return
        loadCurrentValue()
    }
    /** 仅宿主提交阶段关闭取消能力, 原请求仍等待权威结果以保留部分保存事实. */
    function onPhase(phase: BridgePhase): void {
        if (phase === "committing") committed.value = true
    }
    /** 提交一次修改, 宿主按偏好确认并直接保存; 失败回读待处理状态以便重试. */
    async function save() {
        if (
            disposed ||
            !active.value ||
            props.locked ||
            busy.value ||
            !canApply.value ||
            hasPendingModelUpdate(props.project) ||
            !(await validateForm(form.value ?? undefined))
        )
            return
        // 校验期间可能隐藏、卸载或开始旁观导出, 再次核对本组件生命周期和操作锁, 不让迟到校验提交旧参数.
        if (disposed || !active.value || props.locked || busy.value || !canApply.value || hasPendingModelUpdate(props.project)) return
        busy.value = true
        cancelling.value = false
        committed.value = false
        emit("busy", true)
        error.value = ""
        try {
            const request = props.bridge.request(
                "model.edit",
                {
                    operation: operation.value,
                    objectName: objectName.value,
                    property: props.template ? "config" : property.value,
                    value: props.template ? config.value : value.value,
                    table: table.value,
                },
                undefined,
                onPhase,
            )
            requestId.value = request.id
            const snapshot = await request.promise
            emit("updated", snapshot)
            // 部分保存和待核验由权威快照留页展示, 只有完整完成或明确模拟才提供一次性消息.
            const result = snapshot.saveResult
            if (active.value && isModelUpdateComplete(snapshot))
                showFeedback({
                    message:
                        result && isConnectedDesktopProject(snapshot)
                            ? t("model.desktopUpdated")
                            : result?.message
                              ? hostText(result.message)
                              : t("model.simulationSaved"),
                    type: result ? "success" : "info",
                })
        } catch (e) {
            error.value = e instanceof Error ? e.message : t("model.editFailed")
            if (!active.value) return
            try {
                const snapshot = await props.bridge.request("project.inspect", {}).promise
                preserveFailedReadback(snapshot)
                emit("updated", snapshot)
            } catch {
                /* 回读失败时保留原错误与输入. */
            }
        } finally {
            requestId.value = ""
            cancelling.value = false
            committed.value = false
            busy.value = false
            emit("busy", false)
        }
    }
    /** 请求取消由宿主判定, 不将取消确认误报为生成终态. */
    async function cancel() {
        if (!requestId.value || committed.value || cancelling.value) return
        cancelling.value = true
        try {
            const result = await props.bridge.request("request.cancel", { requestId: requestId.value }).promise
            if (!result.accepted) cancelling.value = false
        } catch {
            cancelling.value = false
        }
    }
    watch(dateTable, () => {
        dateColumn.value = ""
    })
    onUnmounted(() => {
        void cancel()
    })
    return {
        operation,
        table,
        objectName,
        property,
        value,
        startYear,
        endYear,
        dateTable,
        dateColumn,
        measureTable,
        templates,
        digitsN,
        digitsP,
        folder,
        formModel,
        years,
        busy,
        error,
        properties,
        names,
        labels,
        targetValid,
        valueLimit,
        fieldIdentity: selectionIdentity,
        canApply,
        conflict,
        reloadValue,
        save,
        t,
        stringSelection,
        hostText,
    }
}
