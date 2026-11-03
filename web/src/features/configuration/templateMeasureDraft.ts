/**
 * FilePath    : pbi-utils\web\src\features\configuration\templateMeasureDraft.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 保留当前目标两种模板来源的独立编辑与读取修订, 区分刷新、显式重读与确定成功提交.
 */
import type { ConfigurationContext } from "@/bridge/client"
import type { TemplateMeasureDefinition, TemplateMeasureMode } from "@/features/scaffold/contracts"
import type { ConfigurationDraft } from "./draft"

type ModeDraft = { measures: TemplateMeasureDefinition[]; baseline: string; revision: string | null }
type SourceState = { mode: TemplateMeasureMode; modes: Record<TemplateMeasureMode, ModeDraft> }
const sources = new WeakMap<ConfigurationDraft, SourceState>()

/** 复制六字段记录, 避免草稿编辑污染宿主快照; 空集合保持为空, 不生成默认记录. */
function copy(measures: TemplateMeasureDefinition[]): TemplateMeasureDefinition[] {
    return measures.map((item) => ({ ...item }))
}

/** 保存当前显示数组并判断两个模式是否有未提交编辑; 切换来源本身也是用户意图. */
function isDirty(draft: ConfigurationDraft, state: SourceState): boolean {
    state.modes[draft.measuresMode].measures = draft.measures
    return draft.measuresMode !== state.mode || Object.values(state.modes).some((item) => JSON.stringify(item.measures) !== item.baseline)
}

/** 绑定宿主来源; replace 仅供显式重读, submittedMode 仅由同目标确定成功回执发放, 活动来源回读且另一模式脏输入保留; 自动刷新不借新修订提交旧编辑. */
export function bindTemplateMeasureDraft(
    draft: ConfigurationDraft,
    context: ConfigurationContext,
    defaults: TemplateMeasureDefinition[],
    replace = false,
    submittedMode?: TemplateMeasureMode,
): void {
    const existing = sources.get(draft)
    const sourceUnavailable = context.measureConfigUnavailableReason === "layout" || context.measureConfigUnavailableReason === "invalid"
    // 保存待处理或只读权限只撤销写能力, 未证明文件来源失效时保留已读模式与修订.
    if (existing && !context.canUseMeasureConfig && !sourceUnavailable) return
    if (existing && !replace && !submittedMode && isDirty(draft, existing)) {
        if (sourceUnavailable) {
            // Config 保留旧文件修订和编辑; 仅干净 TMDL 草稿承接当前编辑, 不覆盖另一模式的未提交输入,
            // 后续显式 TMDL 提交仅修改模型, 不借新文件修订覆盖原 Config 或不可信文件.
            const fallback = existing.modes.tmdl
            if (draft.measuresMode === "config" && JSON.stringify(fallback.measures) === fallback.baseline) fallback.measures = copy(draft.measures)
            fallback.revision = null
            draft.measuresMode = "tmdl"
            draft.measures = fallback.measures
            draft.measureConfigurationRevision = fallback.revision
        }
        return
    }
    const effective = context.measureConfiguration ?? { mode: "tmdl", measures: defaults }
    const mode = sourceUnavailable ? "tmdl" : effective.mode
    const tmdl = effective.mode === "tmdl" ? effective.measures : (context.project.model.templateMeasureConfiguration?.measures ?? defaults)
    const revision = context.measureConfigurationRevision ?? null
    const modeDraft = (values: TemplateMeasureDefinition[]): ModeDraft => ({ measures: copy(values), baseline: JSON.stringify(values), revision })
    const state: SourceState = {
        mode,
        modes: { tmdl: modeDraft(tmdl), config: modeDraft(effective.mode === "config" ? effective.measures : (context.configMeasures ?? tmdl)) },
    }
    if (existing && submittedMode) {
        existing.modes[draft.measuresMode].measures = draft.measures
        const otherMode = submittedMode === "tmdl" ? "config" : "tmdl"
        const other = existing.modes[otherMode]
        if (JSON.stringify(other.measures) !== other.baseline) {
            // 仅共享本轮原修订的另一草稿可跟随我们确定成功的本地变更; NoChanges 不证明新修订来自本轮写入.
            state.modes[otherMode] =
                context.canUseMeasureConfig &&
                other.revision === (draft.measureConfigurationRevision ?? null) &&
                (context.noChanges !== true || revision === other.revision)
                    ? { ...state.modes[otherMode], measures: other.measures }
                    : other
        }
    }
    sources.set(draft, state)
    draft.measuresMode = mode
    draft.measures = state.modes[mode].measures
    draft.measureConfigurationRevision = revision
}

/** 切换可用来源并恢复该来源本次编辑; 两种模式均保留各自原修订, 不发起刷新或模型写入. */
export function selectTemplateMeasureMode(draft: ConfigurationDraft, mode: TemplateMeasureMode, context: ConfigurationContext): void {
    if (mode === "config" && !context.canUseMeasureConfig) return
    const state = sources.get(draft)
    if (!state) return
    state.modes[draft.measuresMode].measures = draft.measures
    const selected = state.modes[mode]
    draft.measuresMode = mode
    draft.measures = selected.measures
    draft.measureConfigurationRevision = selected.revision
}

/** 比较模板草稿读取修订和宿主最新修订; 不更新草稿, 防止用新修订提交旧编辑. */
export function isTemplateMeasureDraftStale(draft: ConfigurationDraft, context: ConfigurationContext): boolean {
    if (!context.canUseMeasureConfig && !context.measureConfigUnavailableReason) return false
    return draft.measureConfigurationRevision !== (context.measureConfigurationRevision ?? null)
}
