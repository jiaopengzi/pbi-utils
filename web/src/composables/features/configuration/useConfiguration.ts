/**
 * FilePath    : pbi-utils\web\src\composables\features\configuration\useConfiguration.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 当前模型目标隔离的配置草稿、应用和保存恢复.
 */
import { computed, nextTick, onBeforeUnmount, provide, ref, shallowRef, useId, watch, type TemplateRef } from "vue"
import { BridgeError, type BridgeClient, type BridgePhase, type ConfigurationContext, type ProjectSnapshot } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { hasPendingModelUpdate, isConnectedDesktopProject, isModelUpdateComplete, requiresModelVerification } from "@/bridge/modelAccess"
import {
    configurationRequest,
    createConfigurationDraft,
    initializeConfigurationCatalog,
    sectionEnabled,
    type ConfigurationOperation,
    type ConfigurationSection,
} from "@/features/configuration/draft"
import { configurationDrafts } from "@/features/configuration/session"
import { bindTemplateMeasureDraft, isTemplateMeasureDraftStale, selectTemplateMeasureMode } from "@/features/configuration/templateMeasureDraft"
import { configurationRecoveryKey } from "@/features/configuration/saveStatus"
import { scaffoldValidationKey, scaffoldValidationPrefixKey } from "@/features/scaffold/validationContext"
import { validateConfiguration, type ScaffoldValidationIssue } from "@/features/scaffold/validation"
import type { CalendarPreviewContext, ScaffoldPeriodTemplate, TemplateMeasureDefinition, TemplateMeasureMode } from "@/features/scaffold/contracts"
import { sameModelTarget } from "@/features/model/operationFreshness"
import { reconcileProjectSnapshot } from "@/app/reconcileProjectSnapshot"
import { modelReadCache } from "@/features/model/modelReadCache"

export type ConfigurationProps = { bridge: BridgeClient; project: ProjectSnapshot; section: ConfigurationSection; active: boolean; externalLocked?: boolean }
export type ConfigurationEmit = {
    (event: "busy", ...args: [value: boolean]): void
    (event: "updated", ...args: [value: ProjectSnapshot]): void
    (event: "capability", ...args: [value: boolean]): void
}

/** 返回有界保存事实的结构键, 供监听与加载复用判断; 等值 JSON 回填不因对象引用不同而重复读取引擎. */
function saveStateKey(project: ProjectSnapshot): string {
    const result = project.saveResult
    return JSON.stringify([
        project.hasPendingChanges === true,
        result?.errorCode ?? null,
        result?.modelSaved ?? null,
        result?.fileSaved ?? null,
        result?.desktopSynchronized ?? null,
        result?.message ?? null,
    ])
}

/** 管理当前模型目标隔离的配置草稿、应用和保存恢复; props 为当前响应式输入, emit 转发权威结果, root 为视图提供的只读根节点引用, 用于定位并聚焦校验失败控件, 返回视图绑定与操作; 请求失败保留原错误和输入. */
export function useConfiguration(props: ConfigurationProps, emit: ConfigurationEmit, root: TemplateRef<HTMLElement>) {
    const { t, locale, hostText } = useI18n()
    const { showFeedback } = useFeedback()
    const context = shallowRef<ConfigurationContext>()
    const cache = modelReadCache(props.bridge)
    let contextRevision = cache.revision.value
    const draft = shallowRef(createConfigurationDraft(locale.value))
    const drafts = configurationDrafts(props.bridge)
    const definitions = ref<ScaffoldPeriodTemplate[]>([])
    const defaults = ref<TemplateMeasureDefinition[]>([])
    const visited = shallowRef(new Set<string>(props.active ? [props.section] : []))
    const prefix = `configuration-${useId()}`
    const issue = shallowRef<ScaffoldValidationIssue>()
    provide(scaffoldValidationKey, issue)
    provide(scaffoldValidationPrefixKey, prefix)
    const busy = ref(false),
        childBusy = ref(false),
        dialog = ref(false),
        error = ref(""),
        requestId = ref(""),
        committed = ref(false),
        cancelling = ref(false)
    const formLocked = computed(() => busy.value || childBusy.value || props.externalLocked === true)
    // 目录刷新后的失效选择仅能重新核验, 不能借模型最终动作静默解析新稳定版本.
    const udfValid = ref(false)
    const locked = computed(() => formLocked.value || dialog.value)
    /** 只隐藏恢复入口正在呈现的同一格式化消息, 独立异常保留并随语言重读. */
    const visibleError = computed(() => {
        const message = hostText(error.value)
        const project = context.value?.project
        const recovery = hasPendingModelUpdate(project) ? hostText(project?.saveResult?.message) || t(configurationRecoveryKey(project)) : ""
        return message === recovery ? "" : message
    })
    const canWrite = computed(() => context.value?.canWrite === true && !hasPendingModelUpdate(context.value.project) && !hasPendingModelUpdate(props.project))
    /** 判定宿主上下文是否仍对应当前模型和保存事实, 不把模型基线相同推定为配置文件未变化. */
    function hasCurrentContext(): boolean {
        return (
            !!context.value &&
            contextRevision === cache.revision.value &&
            sameModelTarget(context.value.project, props.project) &&
            context.value.project.connection?.modelId === props.project.connection?.modelId &&
            context.value.project.baselineId === props.project.baselineId &&
            saveStateKey(context.value.project) === saveStateKey(props.project)
        )
    }
    /** 仅当前宿主上下文发放预览目标, 未核验或待保存时撤销旧基线. */
    const previewContext = computed<CalendarPreviewContext | undefined>(() => {
        const target = context.value
        return target && !hasPendingModelUpdate(target.project) && !hasPendingModelUpdate(props.project)
            ? { kind: "model", targetId: target.targetId, baselineId: target.project.baselineId }
            : undefined
    })
    /** value 为宿主最新上下文, 仅 Config 需要文件能力; TMDL 回退仍由后端核验完整模型归属. */
    function canApplyMeasureMode(value: ConfigurationContext | undefined): boolean {
        return draft.value.measuresMode === "tmdl" || value?.canUseMeasureConfig === true
    }
    /** 每个内部动作单独核验开启状态, 时间智能不借用日期表开关或其他段草稿. */
    function canApply(operation: ConfigurationOperation): boolean {
        return (
            (operation === props.section || (props.section === "calendar" && operation === "periods")) &&
            !locked.value &&
            canWrite.value &&
            sectionEnabled(draft.value, operation) &&
            (operation !== "measures" || canApplyMeasureMode(context.value)) &&
            (operation !== "udfs" || !draft.value.udfs.some((item) => item.source !== "local") || udfValid.value) &&
            (operation !== "sample" || context.value?.canGenerateSample === true)
        )
    }
    useMessageNotices(
        "configuration-errors",
        () => [
            !!visibleError.value && { key: `${context.value?.targetId ?? props.project.projectId}:error`, message: visibleError.value, type: "error" },
            props.section === "measures" &&
                !!context.value &&
                isTemplateMeasureDraftStale(draft.value, context.value) && {
                    key: `${context.value.targetId}:measure-source-changed`,
                    message: t("configuration.measureConfigurationChanged"),
                    type: "warning",
                },
        ],
        { ready: () => props.active },
    )
    let disposed = false,
        generation = 0
    watch(
        () => busy.value || childBusy.value || dialog.value,
        (value) => emit("busy", value),
        { flush: "sync" },
    )
    watch(
        () => [props.section, props.active] as const,
        ([value, active]) => {
            if (active) visited.value = new Set([...visited.value, value])
            issue.value = undefined
            // 无变化切换复用已读取上下文; 失败保留显式重读入口, 外部文件由显式重读和应用前核验发现.
        },
    )
    watch(
        draft,
        () => {
            issue.value = undefined
        },
        { deep: true, flush: "sync" },
    )
    useOperationMessage(() =>
        busy.value
            ? {
                  message: t(committed.value ? "configuration.committing" : "configuration.working"),
                  cancel: requestId.value ? cancel : undefined,
                  cancelDisabled: committed.value || cancelling.value,
                  cancelLabel: t("scaffold.cancel"),
              }
            : undefined,
    )
    /** 提交边界来自宿主事件, 不从提示文本推断. */
    function phase(value: BridgePhase): void {
        if (value === "committing") committed.value = true
    }
    /** 按宿主目标隔离输入; replaceMeasures 仅供显式重读, submittedMode 仅供已核同目标的成功回执, 其余段与行级开关保持. */
    function bind(value: ConfigurationContext, replaceMeasures = false, submittedMode?: TemplateMeasureMode): void {
        if (context.value?.targetId !== value.targetId) udfValid.value = false
        context.value = value
        contextRevision = cache.revision.value
        let saved = drafts.get(value.targetId)
        if (!saved) {
            saved = createConfigurationDraft(locale.value)
            initializeConfigurationCatalog(saved, value.details)
            drafts.set(value.targetId, saved)
        }
        bindTemplateMeasureDraft(saved, value, defaults.value, replaceMeasures, submittedMode)
        saved.measuresEnabled = true
        saved.calendarEnabled = true
        saved.periodsEnabled = true
        saved.udfsEnabled = true
        draft.value = saved
        emit("capability", value.canGenerateSample)
    }
    /** 仅恢复当前模式的本次编辑, 锁定期间拒绝切换, 不读取或写入宿主目标. */
    function selectMeasureMode(mode: TemplateMeasureMode): void {
        if (locked.value || !context.value || !canWrite.value) return
        selectTemplateMeasureMode(draft.value, mode, context.value)
    }
    /** 读取宿主当前目标与共享目录, 同目标新快照同步工作区; 自动读取保留既有待处理事实, 取消、迟到和换目标不授予写能力. */
    async function load(): Promise<void> {
        if (hasCurrentContext()) return
        const own = ++generation
        const original = props.project
        context.value = undefined
        emit("capability", false)
        busy.value = true
        error.value = ""
        committed.value = false
        cancelling.value = false
        try {
            const catalog = props.bridge.request("scaffold.templates", {})
            requestId.value = catalog.id
            const values = await catalog.promise
            if (disposed || own !== generation || cancelling.value) return
            definitions.value = values.periods
            defaults.value = values.defaultMeasures
            const pending = props.bridge.request("configuration.context", { target: "current" })
            requestId.value = pending.id
            const value = await pending.promise
            if (
                disposed ||
                own !== generation ||
                cancelling.value ||
                !sameModelTarget(original, props.project) ||
                original.connection?.modelId !== props.project.connection?.modelId ||
                !sameModelTarget(original, value.project) ||
                original.connection?.modelId !== value.project.connection?.modelId
            )
                return
            // 自动回读不能解除或降级用户已知草稿、保存失败或未知提交, 仅显式恢复动作可更新这些事实.
            // 使用终态时的 props 核对同基线迟到状态, 保留恢复入口并撤销新写能力.
            const current = hasPendingModelUpdate(props.project) ? { ...value, project: props.project, canWrite: false } : value
            bind(current)
            // 先绑定本地上下文再同步父级, 同基线回填由 load 的早返条件吸收, 避免再次读取或自取消.
            const snapshot = reconcileProjectSnapshot(props.project, current.project)
            if (snapshot !== props.project) emit("updated", snapshot)
        } catch (cause) {
            if (!disposed && own === generation && !(cause instanceof BridgeError && cause.code === "CANCELLED"))
                error.value = hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
        } finally {
            if (own === generation) {
                requestId.value = ""
                busy.value = false
            }
        }
    }
    /** 只读当前模板来源; replace 仅供显式重读替换编辑, 失败/取消/换目标/迟到均不覆盖输入. */
    async function readMeasures(replace: boolean): Promise<void> {
        const previous = context.value
        if (!previous || locked.value) return
        const own = ++generation
        busy.value = true
        committed.value = false
        cancelling.value = false
        error.value = ""
        try {
            const pending = props.bridge.request("configuration.context", { target: "current" })
            requestId.value = pending.id
            const value = await pending.promise
            if (disposed || own !== generation || cancelling.value) return
            if (
                value.targetId !== previous.targetId ||
                !sameModelTarget(previous.project, value.project) ||
                value.project.connection?.modelId !== previous.project.connection?.modelId
            )
                throw new BridgeError("STALE_CONFIGURATION_TARGET", t("configuration.measureTargetChanged"))
            const current = hasPendingModelUpdate(props.project) ? { ...value, project: props.project, canWrite: false } : value
            bind(current, replace)
            const snapshot = reconcileProjectSnapshot(props.project, current.project)
            if (snapshot !== props.project) emit("updated", snapshot)
        } catch (cause) {
            if (!disposed && own === generation && !(cause instanceof BridgeError && cause.code === "CANCELLED"))
                error.value = hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
        } finally {
            if (own === generation) {
                requestId.value = ""
                busy.value = false
            }
        }
    }
    /** 显式替换当前模板草稿, 只读宿主配置, 不发起模型写入或重放其他动作. */
    function reloadMeasures(): Promise<void> {
        return readMeasures(true)
    }
    /** 发起取消仍等待同一请求终态, 迟到取消异常不得覆盖后续操作. */
    async function cancel(): Promise<void> {
        if (!requestId.value || committed.value || cancelling.value) return
        const own = generation,
            id = requestId.value
        cancelling.value = true
        try {
            await props.bridge.request("request.cancel", { requestId: id }).promise
        } catch (cause) {
            if (!disposed && own === generation && requestId.value === id) error.value = hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
        }
    }
    /** 仅展示同目标确定完成回执相对 original 新增的模板告警; 重复值合并, 历史快照不重放, 不改变成功或恢复状态. */
    function showMeasureWarnings(original: ConfigurationContext, value: ConfigurationContext): void {
        if (
            !isModelUpdateComplete(value.project) ||
            original.targetId !== value.targetId ||
            !sameModelTarget(original.project, value.project) ||
            original.project.connection?.modelId !== value.project.connection?.modelId
        )
            return
        const previous = new Map<string, number>()
        for (const warning of original.project.warnings) previous.set(warning, (previous.get(warning) ?? 0) + 1)
        const added = new Set<string>()
        for (const warning of value.project.warnings) {
            const count = previous.get(warning) ?? 0
            if (count) previous.set(warning, count - 1)
            else added.add(warning)
        }
        for (const warning of added) showFeedback({ message: hostText(warning), type: "warning" })
    }
    /** 操作失败后按 original 的完整目标核验保存状态; measureWarnings 仅供本次模板动作的确定终态, own 过期不发布, 换目标或回读失败撤销旧写能力且保留原错. */
    async function readback(own: number, original: ConfigurationContext, measureWarnings = false): Promise<void> {
        if (disposed || own !== generation) return
        try {
            const pending = props.bridge.request("configuration.context", { target: "current" })
            requestId.value = pending.id
            const value = await pending.promise
            if (!disposed && own === generation) {
                if (
                    value.targetId !== original.targetId ||
                    !sameModelTarget(original.project, value.project) ||
                    original.project.connection?.modelId !== value.project.connection?.modelId
                )
                    throw new BridgeError("STALE_CONFIGURATION_TARGET", t("configuration.measureTargetChanged"))
                bind(value)
                emit("updated", value.project)
                if (measureWarnings) showMeasureWarnings(original, value)
            }
        } catch {
            // 在途 busy 持续阻止写入; 仅回读失败才撤销旧上下文, 不让同一恢复提示因临时空值再次弹出.
            if (!disposed && own === generation) context.value = undefined
        }
    }
    /** 应用 operation 对应的独立配置段, 先读权威上下文并核同目标和新目录; 其他草稿不参与校验, 刷新失败或取消不提交, 目标核验和提交后回读仍由宿主编排. */
    async function apply(operation: ConfigurationOperation): Promise<void> {
        const previous = context.value
        if (!previous || !canApply(operation)) return
        const requestedMeasureMode = draft.value.measuresMode
        const measureRevision = draft.value.measureConfigurationRevision
        const own = ++generation
        busy.value = true
        committed.value = false
        cancelling.value = false
        error.value = ""
        try {
            const checking = props.bridge.request("configuration.context", { target: "current" })
            requestId.value = checking.id
            const current = await checking.promise
            if (disposed || own !== generation || cancelling.value) return
            if (
                current.targetId !== previous.targetId ||
                current.project.projectId !== previous.project.projectId ||
                current.project.projectPath !== previous.project.projectPath ||
                current.project.connection?.kind !== previous.project.connection?.kind ||
                current.project.connection?.instanceId !== previous.project.connection?.instanceId ||
                current.project.connection?.modelId !== previous.project.connection?.modelId
            )
                throw new BridgeError("STALE_BASELINE", "模型已变化, 请重新读取配置目标")
            // 只在本地上下文更新目录与基线, 不先发布父级 props 触发另一次 load 打断本轮操作.
            bind(current)
            if (
                operation === "measures" &&
                ((requestedMeasureMode === "config" && !current.canUseMeasureConfig) || measureRevision !== (current.measureConfigurationRevision ?? null))
            ) {
                error.value = t("configuration.measureConfigurationChanged")
                emit("updated", current.project)
                return
            }
            if (!canWrite.value || (operation === "sample" && !current.canGenerateSample) || (operation === "measures" && !canApplyMeasureMode(current))) {
                emit("updated", current.project)
                return
            }
            const request = configurationRequest(draft.value, locale.value, operation)
            const problem = validateConfiguration({
                ...request,
                sample: request.sample ?? null,
                measures: request.measures ?? null,
                calendar: request.calendar ?? null,
                periods: request.periods ?? null,
                udfs: request.udfs ?? null,
                details: current.details,
                periodDefinitions: definitions.value,
            })
            if (problem) {
                issue.value = problem
                emit("updated", current.project)
                busy.value = false
                await nextTick()
                if (disposed || own !== generation) return
                const field = root.value?.querySelector<HTMLElement>('[data-scaffold-invalid="true"]')
                const control = field?.querySelector<HTMLElement>(
                    'input:not([type="hidden"]):not(:disabled),textarea:not(:disabled),button:not(:disabled),[tabindex="0"]',
                )
                control?.scrollIntoView?.({ block: "center", inline: "nearest" })
                control?.focus({ preventScroll: true })
                return
            }
            const pending = props.bridge.request(
                "configuration.apply",
                { targetId: current.targetId, baselineId: current.project.baselineId, section: props.section, request },
                undefined,
                phase,
            )
            requestId.value = pending.id
            const value = await pending.promise
            if (disposed || own !== generation) return
            if (
                value.targetId !== current.targetId ||
                !sameModelTarget(current.project, value.project) ||
                value.project.connection?.modelId !== current.project.connection?.modelId
            )
                throw new BridgeError("STALE_CONFIGURATION_TARGET", t("configuration.measureTargetChanged"))
            bind(
                value,
                false,
                operation === "measures" && isModelUpdateComplete(value.project) && value.measureConfiguration?.mode === requestedMeasureMode
                    ? requestedMeasureMode
                    : undefined,
            )
            emit("updated", value.project)
            await nextTick()
            const partial = hasPendingModelUpdate(value.project)
            if (partial) error.value = hostText(value.project.saveResult?.message) || t(configurationRecoveryKey(value.project))
            else if (isModelUpdateComplete(value.project)) {
                showFeedback({
                    message: t(
                        value.noChanges
                            ? "configuration.noChanges"
                            : isConnectedDesktopProject(value.project)
                              ? "model.desktopUpdated"
                              : "configuration.applied",
                    ),
                    type: "success",
                })
                // 只展示本次日期应用返回的警告; 无变更、快照刷新或语言切换不重放旧结果.
                if (operation === "calendar" && !value.noChanges)
                    for (const warning of new Set(value.project.warnings)) showFeedback({ message: hostText(warning), type: "warning" })
                if (operation === "measures") showMeasureWarnings(current, value)
            }
        } catch (cause) {
            if (!disposed && own === generation) {
                await readback(own, previous, operation === "measures")
                if (!disposed && own === generation && !(cause instanceof BridgeError && cause.code === "CANCELLED"))
                    error.value = hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
            }
        } finally {
            if (own === generation) {
                requestId.value = ""
                busy.value = false
            }
        }
    }
    /** 后续保存恢复不重放配置, 终态仍以宿主上下文回读为准. */
    async function finish(discard = false): Promise<void> {
        const current = context.value
        const measureRecovery = props.section === "measures"
        if (
            locked.value ||
            !current ||
            !hasPendingModelUpdate(current.project) ||
            (requiresModelVerification(current.project) && !(discard && isConnectedDesktopProject(current.project)))
        )
            return
        const own = ++generation
        busy.value = true
        committed.value = false
        cancelling.value = false
        error.value = ""
        try {
            const pending = props.bridge.request(
                discard ? "configuration.discard" : "configuration.complete",
                { targetId: current.targetId, baselineId: current.project.baselineId },
                undefined,
                phase,
            )
            requestId.value = pending.id
            const value = await pending.promise
            if (disposed || own !== generation) return
            bind(value)
            emit("updated", value.project)
            if (hasPendingModelUpdate(value.project)) error.value = hostText(value.project.saveResult?.message) || t(configurationRecoveryKey(value.project))
            else {
                showFeedback({
                    message: t(
                        discard && isConnectedDesktopProject(value.project)
                            ? "app.modelReloaded"
                            : discard
                              ? "configuration.discarded"
                              : "configuration.completed",
                    ),
                    type: "success",
                })
                if (measureRecovery) showMeasureWarnings(current, value)
            }
        } catch (cause) {
            if (!disposed && own === generation) {
                await readback(own, current, measureRecovery)
                if (!(cause instanceof BridgeError && cause.code === "CANCELLED"))
                    error.value = hostText(cause instanceof Error ? cause.message : t("scaffold.failed"))
            }
        } finally {
            if (own === generation) {
                requestId.value = ""
                busy.value = false
            }
        }
    }
    watch(
        [
            () => props.project.projectId,
            () => props.project.projectPath,
            () => props.project.baselineId,
            () => props.project.connection?.kind,
            () => props.project.connection?.instanceId,
            () => props.project.connection?.modelId,
            () => saveStateKey(props.project),
            () => cache.revision.value,
        ],
        () => {
            void load()
        },
        { immediate: true },
    )
    onBeforeUnmount(() => {
        disposed = true
        generation++
        if (requestId.value && !committed.value) void props.bridge.request("request.cancel", { requestId: requestId.value }).promise.catch(() => undefined)
    })
    return {
        context,
        draft,
        definitions,
        visited,
        prefix,
        formLocked,
        locked,
        visibleError,
        canWrite,
        previewContext,
        canApply,
        load,
        apply,
        reloadMeasures,
        selectMeasureMode,
        finish,
        t,
        childBusy,
        udfValid,
        dialog,
    }
}
