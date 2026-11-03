/**
 * FilePath    : pbi-utils\web\src\composables\app\useMeasuresEditor.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 度量值选择草稿、原子批次编辑与本地格式化生命周期.
 */
import { hostText } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import { computed, onUnmounted, ref, shallowRef, watch } from "vue"
import type { BridgeClient, BridgePhase, MeasureChange, ProjectSnapshot } from "@/bridge/client"
import { hasPendingModelUpdate, isConnectedDesktopProject, isModelUpdateComplete, isReadOnlyProject } from "@/bridge/modelAccess"
import { measureBatchAllowed, measureBatchRequest } from "@/bridge/measureBatchBudget"
import { formatDax } from "@/features/code/formatDax"
import { measureExpressionForEditor, measureExpressionForSave } from "@/features/code/measureExpression"
import { useMeasureDraftGuard, type MeasureNavigation } from "./useMeasureDraftGuard"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { readModelForOperation, sameModelTarget } from "@/features/model/operationFreshness"
import { filterHierarchyMeasures, measureKey as keyOf } from "@/features/model/measureHierarchy"

export type MeasuresEditorProps = { project: ProjectSnapshot; bridge: BridgeClient; locked?: boolean }
export type MeasuresEditorEmit = { (event: "busy", ...args: [value: boolean]): void; (event: "updated", ...args: [value: ProjectSnapshot]): void }

type Fields = Required<Omit<MeasureChange, "action" | "table" | "name">>

type Measure = ProjectSnapshot["model"]["measures"][number]

/** 无选择或多选时保持空表单, 隐藏属性只有勾选后才参与批量更新. */
function emptyFields(): Fields {
    return { expression: "", description: "", formatString: "", displayFolder: "", isHidden: false }
}

/** 将权威属性转换为编辑显示, 仅去除度量正文保存包装, 不改动模型字符串内容. */
function measureFields(measure: Measure): Fields {
    return {
        expression: measureExpressionForEditor(measure.expression ?? ""),
        description: measure.description ?? "",
        formatString: measure.formatString ?? "",
        displayFolder: measure.displayFolder ?? "",
        isHidden: measure.isHidden,
    }
}

/** 管理 度量值选择草稿、原子批次编辑与本地格式化生命周期; props 为当前响应式输入, emit 转发权威结果, 返回视图绑定与操作; 请求失败保留原错误和输入. */
export function useMeasuresEditor(props: MeasuresEditorProps, emit: MeasuresEditorEmit) {
    const { t, daxFormatStyle: formatStyle } = useI18n()
    const { showFeedback } = useFeedback()
    const readOnly = computed(() => isReadOnlyProject(props.project))
    const query = ref("")
    const selected = ref<string[]>([])
    const busy = ref(false)
    const requestId = ref("")
    const activeOperation = ref<"save" | "format" | "create" | "delete" | "formatted">("save")
    const cancelling = ref(false)
    const committed = ref(false)
    const error = ref("")
    const enabled = ref<string[]>([])
    const fields = ref<Fields>(emptyFields())
    const formatted = ref<MeasureChange[]>([])
    const creating = ref(false)
    const drafts = ref<{ id: number; table: string; name: string; fields: Fields }[]>([])
    const orphaned = ref<Measure[]>([])
    let draftId = 0
    let generation = 0,
        disposed = false,
        stopRequested = false
    let savedProject: ProjectSnapshot | undefined
    // 外部工作区失效不替换草稿, 但撤销旧格式化/保存结果及尚未完成的选择意图.
    watch(
        () => props.locked,
        (locked) => {
            if (locked) generation++
        },
        { flush: "sync" },
    )
    // 草稿与用户选择时的完整权威定义绑定, 自动回读不能把旧输入静默改绑到外部新定义.
    const selectionProject = shallowRef(props.project)
    const blocked = computed(() => busy.value || !!props.locked || hasPendingModelUpdate(props.project) || orphaned.value.length > 0)
    const measures = computed(() => filterHierarchyMeasures(props.project.model.measures, query.value))
    const chosen = computed(() => [...props.project.model.measures, ...orphaned.value].filter((m) => selected.value.includes(keyOf(m))))
    const current = computed(() => (chosen.value.length === 1 ? chosen.value[0] : undefined))
    const editingMeasure = computed(() => current.value && selectionProject.value.model.measures.find((measure) => keyOf(measure) === keyOf(current.value!)))
    const editingFields = computed(() => (editingMeasure.value ? measureFields(editingMeasure.value) : undefined))
    const fieldsChanged = computed(() =>
        editingMeasure.value
            ? Object.keys(fields.value).some((key) => fields.value[key as keyof Fields] !== editingFields.value![key as keyof Fields])
            : enabled.value.length > 0,
    )
    // 保留编辑节点和固定按钮文案, 在正文外更新操作说明与取消状态.
    useOperationMessage(() =>
        busy.value
            ? {
                  message: `${t(activeOperation.value === "format" ? "model.formatLocal" : "model.saveEdits")} · ${t("model.processing")}`,
                  cancel: requestId.value ? cancel : undefined,
                  cancelLabel: t("model.cancel"),
                  cancelDisabled: cancelling.value || committed.value,
              }
            : undefined,
    )

    /** 精确单选时加载实际快照; 多选清空, 避免误把某个对象的值应用到全部对象. */
    function loadSelection(): void {
        selectionProject.value = props.project
        orphaned.value = []
        fields.value = current.value ? measureFields(current.value) : emptyFields()
        enabled.value = []
        formatted.value = []
        error.value = ""
    }
    watch(
        () => props.project,
        (next, previous) => {
            const ownSave = savedProject && sameModelTarget(next, savedProject) && next.baselineId === savedProject.baselineId
            savedProject = undefined
            if (sameModelTarget(next, previous) && next.baselineId === previous.baselineId) return
            generation++
            // 先按旧权威选择判断用户输入, 避免把外部更新当编辑或在对象消失后丢失草稿.
            const before = selectionProject.value.model.measures.filter((measure) => selected.value.includes(keyOf(measure)))
            const beforeFields = before.length === 1 ? measureFields(before[0]!) : undefined
            const dirty =
                before.length === 1
                    ? Object.keys(fields.value).some((key) => fields.value[key as keyof Fields] !== beforeFields![key as keyof Fields])
                    : enabled.value.length > 0
            if (!ownSave && sameModelTarget(next, previous) && (creating.value || dirty || formatted.value.length > 0)) {
                orphaned.value = creating.value ? [] : before.filter((measure) => !next.model.measures.some((candidate) => keyOf(candidate) === keyOf(measure)))
                if (orphaned.value.length) error.value = t("model.externalMeasureMissing")
                return
            }
            orphaned.value = []
            selected.value = selected.value.filter((key) => props.project.model.measures.some((m) => keyOf(m) === key))
            creating.value = false
            drafts.value = []
            loadSelection()
        },
    )
    // 默认展示第一项, 随后选择完全由用户控制, 不随搜索自动改变目标.
    if (props.project.model.measures[0]) selected.value = [keyOf(props.project.model.measures[0])]
    loadSelection()

    const draftGuard = useMeasureDraftGuard({
        project: () => props.project,
        dirty: () => !readOnly.value && (creating.value || fieldsChanged.value || formatted.value.length > 0),
        busy: () => busy.value || !!props.locked,
        invalidated: () => !!props.locked,
        save: () => (creating.value ? saveCreates() : formatted.value.length ? save(formatted.value, "formatted") : saveEdits()),
        apply: applyNavigation,
    })
    const { confirming, saving: savingDraft, resolve: resolveDraft } = draftGuard
    const displayFolders = computed(() => props.project.model.measures.map((measure) => measure.displayFolder ?? ""))

    /** 确认后才改变草稿所属对象; 过滤已经消失的身份, 并重新绑定当前权威快照. */
    function applyNavigation(navigation: MeasureNavigation): void {
        creating.value = false
        drafts.value = []
        if (navigation.kind === "selection") selected.value = navigation.keys.filter((key) => props.project.model.measures.some((m) => keyOf(m) === key))
        loadSelection()
        if (navigation.kind === "create") {
            creating.value = true
            addDraft()
        }
    }

    /** 树/复选框/清空仅传递选择意图, 未保存确认结束前保持原选择和编辑器. */
    function requestSelection(keys: string[]): void {
        if (!creating.value && keys.length === selected.value.length && keys.every((key) => selected.value.includes(key))) return
        draftGuard.request({ kind: "selection", keys: [...keys] })
    }

    /** 行点击切换单选并填充编辑栏, 不触发模型写入. */
    function selectOne(measure: Measure): void {
        draftGuard.request({ kind: "selection", keys: [keyOf(measure)] })
    }
    /** 当前搜索结果整体选择, 不把隐藏的旧选择混入操作. */
    function selectVisible(): void {
        requestSelection(measures.value.map(keyOf))
    }
    /** 新增条目只存前端草稿, 一次保存可提交多个条目. */
    function addDraft(): void {
        drafts.value.push({ id: ++draftId, table: current.value?.table ?? props.project.model.tables[0]?.name ?? "", name: "", fields: emptyFields() })
    }
    /** 进入批量新增, 不隐式提交当前编辑栏. */
    function startCreate(): void {
        if (!blocked.value && !readOnly.value) draftGuard.request({ kind: "create" })
    }
    /** 退出新增也通过同一草稿确认, 不静默丢弃新增条目. */
    function backToEdit(): void {
        draftGuard.request({ kind: "edit" })
    }
    /** 同步请求状态并记录 operation 对应的按钮, 防止等待结果时改变目标或重置正文. */
    function setBusy(value: boolean, operation: typeof activeOperation.value = "save"): void {
        if (value) activeOperation.value = operation
        cancelling.value = false
        committed.value = false
        busy.value = value
        emit("busy", value)
    }
    /** 宿主进入不可取消提交后禁用入口, 不按百分比或客户端计时推断已提交. */
    function onPhase(phase: BridgePhase): void {
        if (phase === "committing") committed.value = true
    }
    /** 只发送一个原子批次, action 选择加载按钮; 对照草稿原始定义拒绝外部冲突, 失败保留表单并回读待保存状态. */
    async function save(items: MeasureChange[], action: typeof activeOperation.value = "save"): Promise<boolean> {
        if (readOnly.value || blocked.value) return false
        const original = props.project,
            epoch = ++generation
        let writing = false
        stopRequested = false
        error.value = ""
        try {
            if (!measureBatchAllowed(original.baselineId, items)) throw new Error(t("model.batchBounds"))
            if (items.some((item) => !item.table.trim() || !item.name.trim() || (item.expression !== undefined && !item.expression.trim())))
                throw new Error(t("model.measureRequired"))
            setBusy(true, action)
            const fresh = await readModelForOperation(props.bridge, original, (id) => (requestId.value = id))
            if (disposed || stopRequested || epoch !== generation || !sameModelTarget(original, props.project)) return false
            if (fresh.baselineId !== selectionProject.value.baselineId) {
                const changed = items.some((item) => {
                    if (item.action === "create") return false
                    const before = selectionProject.value.model.measures.find((measure) => keyOf(measure) === keyOf(item))
                    const after = fresh.model.measures.find((measure) => keyOf(measure) === keyOf(item))
                    return !before || !after || Object.keys(before).some((key) => before[key as keyof Measure] !== after[key as keyof Measure])
                })
                if (changed) {
                    emit("updated", fresh)
                    throw new Error(t("model.externalMeasureChanged"))
                }
            }
            if (!measureBatchAllowed(fresh.baselineId, items)) throw new Error(t("model.batchBounds"))
            writing = true
            const request = props.bridge.request("model.edit", measureBatchRequest(fresh.baselineId, items), undefined, onPhase)
            requestId.value = request.id
            const snapshot = await request.promise
            if (disposed || epoch !== generation || !sameModelTarget(original, props.project)) return false
            // 本次完整成功由结果回填表单, 不把主动删除当成外部丢失对象的冲突.
            const complete = isModelUpdateComplete(snapshot)
            if (complete) savedProject = snapshot
            emit("updated", snapshot)
            if (complete) {
                formatted.value = []
                creating.value = false
            }
            // 部分保存和待核验由权威快照留页展示, 只有完整完成或明确模拟才提供一次性消息.
            const result = snapshot.saveResult
            if (complete)
                showFeedback({
                    message:
                        result && isConnectedDesktopProject(snapshot)
                            ? t("model.desktopUpdated")
                            : result?.message
                              ? hostText(result.message)
                              : t("model.simulationSaved"),
                    type: result ? "success" : "info",
                })
            return complete
        } catch (e) {
            error.value = e instanceof Error ? e.message : t("model.saveFailed")
            if (writing && !disposed && epoch === generation) {
                try {
                    emit("updated", await props.bridge.request("project.inspect", {}).promise)
                } catch {
                    /* 回读失败保留原输入和错误. */
                }
            }
            return false
        } finally {
            requestId.value = ""
            setBusy(false)
        }
    }
    /** 计算实际更新批次; value 为待提交表单, 返回单选主动修改或多选明确勾选的字段, 不修改草稿. */
    function editItems(value: Fields): MeasureChange[] {
        const keys = editingMeasure.value
            ? Object.keys(value).filter((key) => value[key as keyof Fields] !== editingFields.value![key as keyof Fields])
            : enabled.value
        if (!keys.length) return []
        const patch = Object.fromEntries(keys.map((key) => [key, value[key as keyof Fields]]))
        if (typeof patch.expression === "string") patch.expression = measureExpressionForSave(patch.expression)
        return chosen.value.map((m) => ({ action: "update", table: m.table, name: m.name, ...patch }))
    }
    /** 计算实际新增批次; expressions 可提供格式化候选, 返回裁剪名称及完整属性, 不修改新增草稿. */
    function createItems(expressions?: readonly string[]): MeasureChange[] {
        return drafts.value.map((draft, index) => ({
            action: "create",
            table: draft.table,
            name: draft.name.trim(),
            ...draft.fields,
            expression: measureExpressionForSave(expressions?.[index] ?? draft.fields.expression),
        }))
    }
    /** 仅保存实际主动修改的属性; 无字段变化时不发请求. */
    async function saveEdits(): Promise<boolean> {
        const items = editItems(fields.value)
        return items.length ? save(items) : true
    }
    /** 新建批次保留各条不同定义, 返回当前批次的直接提交结果. */
    function saveCreates(): Promise<boolean> {
        return save(createItems(), "create")
    }
    /** 提交选中的准确删除对象, 由宿主核验目标并直接持久化. */
    function deleteSelected(): void {
        void save(
            chosen.value.map((m) => ({ action: "delete", table: m.table, name: m.name })),
            "delete",
        )
    }
    /** 用当前 short/long 选择格式化草稿或预览批次, 不保存其他属性; 失败保留输入, 取消或目标变化后的迟到结果不回填. */
    async function format(): Promise<void> {
        if (blocked.value || readOnly.value) return
        const original = props.project,
            epoch = ++generation
        stopRequested = false
        error.value = ""
        try {
            const expressions = creating.value
                ? drafts.value.map((d) => d.fields.expression)
                : current.value
                  ? [fields.value.expression]
                  : chosen.value.map((m) => m.expression)
            setBusy(true, "format")
            const request = formatDax(props.bridge, expressions, formatStyle.value)
            requestId.value = request.id
            const result = await request.promise
            if (disposed || stopRequested || epoch !== generation || !sameModelTarget(original, props.project)) return
            if (result.isSimulation) {
                showFeedback({ message: t("model.simulationFormat"), type: "info" })
                return
            }
            if (result.expressions.length !== expressions.length) throw new Error(t("model.formatCountMismatch"))
            const displayExpressions = result.expressions.map(measureExpressionForEditor)
            const batchEdits = !creating.value && !current.value ? editItems(fields.value) : []
            const items = creating.value
                ? createItems(displayExpressions)
                : current.value
                  ? editItems({ ...fields.value, expression: displayExpressions[0]! })
                  : chosen.value.map((m, index) => ({
                        ...batchEdits[index],
                        action: "update" as const,
                        table: m.table,
                        name: m.name,
                        expression: measureExpressionForSave(displayExpressions[index]!),
                    }))
            // 两种布局新增的空白、JSON 转义、完整属性和基线开销都须能进入实际保存批次, 校验成功前不回填任何字段或预览.
            if (JSON.stringify(result.expressions).length > 30000 || (items.length > 0 && !measureBatchAllowed(props.project.baselineId, items)))
                throw new Error(t("model.formatBounds"))
            if (creating.value)
                drafts.value.forEach((d, index) => {
                    d.fields.expression = displayExpressions[index]!
                })
            else if (current.value) fields.value.expression = displayExpressions[0]!
            else formatted.value = items
        } catch (e) {
            if (!disposed && epoch === generation && sameModelTarget(original, props.project))
                error.value = e instanceof Error ? e.message : t("model.formatFailed")
        } finally {
            requestId.value = ""
            setBusy(false)
        }
    }
    /** 离开时取消未提交请求, 已提交操作由宿主完成并保留最终状态. */
    async function cancel(): Promise<void> {
        if (!requestId.value || cancelling.value || committed.value) return
        stopRequested = true
        cancelling.value = true
        try {
            const result = await props.bridge.request("request.cancel", { requestId: requestId.value }).promise
            if (!result.accepted) cancelling.value = false
        } catch {
            cancelling.value = false
        }
    }
    onUnmounted(() => {
        disposed = true
        generation++
        void cancel()
    })
    return {
        readOnly,
        query,
        selected,
        busy,
        activeOperation,
        error,
        enabled,
        fields,
        formatted,
        confirming,
        savingDraft,
        resolveDraft,
        displayFolders,
        creating,
        drafts,
        blocked,
        measures,
        chosen,
        current,
        fieldsChanged,
        keyOf,
        selectOne,
        requestSelection,
        selectVisible,
        addDraft,
        startCreate,
        backToEdit,
        save,
        saveEdits,
        saveCreates,
        deleteSelected,
        format,
        t,
        hostText,
    }
}
