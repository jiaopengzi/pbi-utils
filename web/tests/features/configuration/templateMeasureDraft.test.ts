/**
 * FilePath    : pbi-utils\web\tests\features\configuration\templateMeasureDraft.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模板度量来源失效恢复时的分模式草稿隔离及 TMDL 目标修订.
 */
import { expect, it } from "vitest"
import type { ConfigurationContext } from "@/bridge/client"
import { previewProject } from "@/bridge/preview"
import { configurationRequest, createConfigurationDraft } from "@/features/configuration/draft"
import { bindTemplateMeasureDraft, isTemplateMeasureDraftStale, selectTemplateMeasureMode } from "@/features/configuration/templateMeasureDraft"

/** 创建 Config 可用或失效的完整宿主来源, 参数记录与文件修订均明确且独立. */
function context(available: boolean): ConfigurationContext {
    const measures = [{ name: "Item", value: "来源值", enabled: false, dataCategory: "Uncategorized", description: "", displayFolder: "" }]
    return {
        targetId: "fallback-target",
        project: structuredClone(previewProject),
        details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
        canWrite: true,
        canGenerateSample: false,
        canUseMeasureConfig: available,
        measureConfiguration: { mode: available ? "config" : "tmdl", measures },
        measureConfigurationRevision: available ? "a".repeat(64) : null,
        configMeasures: available ? measures : null,
        measureConfigUnavailableReason: available ? null : "layout",
        isCreatedTarget: false,
    }
}

it("宿主允许缺失配置的目标切换两种模式时保留独立草稿与 missing 修订", () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.measuresEnabled = true
    const available = context(true)
    available.measureConfiguration!.mode = "tmdl"
    available.measureConfigurationRevision = "missing"
    available.configMeasures = null
    bindTemplateMeasureDraft(draft, available, [])
    draft.measures[0]!.value = "TMDL草稿"
    selectTemplateMeasureMode(draft, "config", available)
    expect(draft.measures[0]!.value).toBe("来源值")
    draft.measures[0]!.value = "Config草稿"
    expect(configurationRequest(draft, "zh-cn", "measures")).toMatchObject({
        measuresMode: "config",
        measureConfigurationRevision: "missing",
        measures: [{ value: "Config草稿" }],
    })
    selectTemplateMeasureMode(draft, "tmdl", available)
    expect(configurationRequest(draft, "zh-cn", "measures")).toMatchObject({
        measuresMode: "tmdl",
        measureConfigurationRevision: "missing",
        measures: [{ value: "TMDL草稿" }],
    })
    selectTemplateMeasureMode(draft, "config", available)
    expect(draft.measures[0]!.value).toBe("Config草稿")
    expect(available.measureConfiguration!.measures[0]!.value).toBe("来源值")
    expect(isTemplateMeasureDraftStale(draft, available)).toBe(false)
})

it("Config 失效降级后的 TMDL 编辑不污染恢复时的 Config 编辑", () => {
    const draft = createConfigurationDraft("zh-cn")
    const available = context(true)
    bindTemplateMeasureDraft(draft, available, [])
    draft.measures[0]!.value = "Config未保存编辑"
    bindTemplateMeasureDraft(draft, context(false), [])
    expect(draft.measuresMode).toBe("tmdl")
    draft.measures[0]!.value = "降级后TMDL编辑"
    bindTemplateMeasureDraft(draft, available, [])
    selectTemplateMeasureMode(draft, "config", available)
    expect(draft.measures[0]!.value).toBe("Config未保存编辑")
    selectTemplateMeasureMode(draft, "tmdl", available)
    expect(draft.measures[0]!.value).toBe("降级后TMDL编辑")
})

it.each(["layout", "invalid"] as const)("Config 因 %s 失效时保留两种模式已有的独立编辑", (reason) => {
    const draft = createConfigurationDraft("zh-cn")
    const available = context(true)
    available.project.model.templateMeasureConfiguration = { mode: "tmdl", measures: available.measureConfiguration!.measures }
    bindTemplateMeasureDraft(draft, available, [])
    selectTemplateMeasureMode(draft, "tmdl", available)
    draft.measures[0]!.value = "独立TMDL编辑"
    selectTemplateMeasureMode(draft, "config", available)
    draft.measures[0]!.value = "独立Config编辑"

    bindTemplateMeasureDraft(draft, { ...context(false), measureConfigUnavailableReason: reason }, [])
    expect(draft).toMatchObject({ measuresMode: "tmdl", measureConfigurationRevision: null, measures: [{ value: "独立TMDL编辑" }] })

    bindTemplateMeasureDraft(draft, available, [])
    selectTemplateMeasureMode(draft, "config", available)
    expect(draft.measures[0]!.value).toBe("独立Config编辑")
    selectTemplateMeasureMode(draft, "tmdl", available)
    expect(draft.measures[0]!.value).toBe("独立TMDL编辑")
})

it("Config 失效后显式 TMDL 意图绑定无配置目标的修订, 保留编辑且不借旧文件修订写回", () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.measuresEnabled = true
    bindTemplateMeasureDraft(draft, context(true), [])
    draft.measures[0]!.value = "保留编辑"
    bindTemplateMeasureDraft(draft, context(false), [])
    expect(configurationRequest(draft, "zh-cn", "measures")).toMatchObject({
        measuresMode: "tmdl",
        measureConfigurationRevision: null,
        measures: [{ value: "保留编辑" }],
    })
})

it("已选 TMDL 的脏编辑在配置不可用时解绑文件修订, 隐藏 Config 草稿仍保留原修订", () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.measuresEnabled = true
    const available = context(true)
    available.measureConfiguration!.mode = "tmdl"
    bindTemplateMeasureDraft(draft, available, [])
    draft.measures[0]!.value = "TMDL未保存编辑"
    bindTemplateMeasureDraft(draft, context(false), [])
    expect(configurationRequest(draft, "zh-cn", "measures")).toMatchObject({
        measuresMode: "tmdl",
        measureConfigurationRevision: null,
        measures: [{ value: "TMDL未保存编辑" }],
    })
    const restored = { ...available, measureConfigurationRevision: "b".repeat(64) }
    bindTemplateMeasureDraft(draft, restored, [])
    expect(draft.measureConfigurationRevision).toBeNull()
    selectTemplateMeasureMode(draft, "config", restored)
    expect(draft.measureConfigurationRevision).toBe("a".repeat(64))
    expect(draft.measures[0]!.value).toBe("来源值")
})

it("只读目标未报告目录或文件失效时初次仍显示宿主 Config 来源", () => {
    const draft = createConfigurationDraft("zh-cn")
    const readonly = { ...context(true), canWrite: false, canUseMeasureConfig: false }
    bindTemplateMeasureDraft(draft, readonly, [])
    expect(draft).toMatchObject({ measuresMode: "config", measureConfigurationRevision: "a".repeat(64), measures: [{ value: "来源值" }] })
})

it.each([false, true])("保存待处理导致能力暂时关闭时不回退 Config 或丢失来源修订, dirty=%s", (dirty) => {
    const draft = createConfigurationDraft("zh-cn")
    const available = context(true)
    bindTemplateMeasureDraft(draft, available, [])
    if (dirty) draft.measures[0]!.value = "待处理前编辑"
    const pending = {
        ...available,
        canWrite: false,
        canUseMeasureConfig: false,
        measureConfiguration: null,
        measureConfigurationRevision: null,
        configMeasures: null,
    }
    bindTemplateMeasureDraft(draft, pending, [])
    expect(draft).toMatchObject({
        measuresMode: "config",
        measureConfigurationRevision: "a".repeat(64),
        measures: [{ value: dirty ? "待处理前编辑" : "来源值" }],
    })
    bindTemplateMeasureDraft(draft, available, [])
    expect(draft.measuresMode).toBe("config")
    expect(draft.measureConfigurationRevision).toBe("a".repeat(64))
})

it("保存恢复成功但未读取文件来源时保留原草稿和修订, 后续真实来源漂移仍阻止旧编辑", () => {
    const draft = createConfigurationDraft("zh-cn")
    bindTemplateMeasureDraft(draft, context(true), [])
    draft.measures[0]!.value = "保留文件编辑"
    const recovered = { ...context(true), canUseMeasureConfig: false, measureConfigurationRevision: null, configMeasures: null }
    recovered.measureConfiguration!.measures[0]!.value = "引擎回执值"
    bindTemplateMeasureDraft(draft, recovered, [])
    expect(draft).toMatchObject({ measuresMode: "config", measureConfigurationRevision: "a".repeat(64), measures: [{ value: "保留文件编辑" }] })
    expect(isTemplateMeasureDraftStale(draft, recovered)).toBe(false)
    const fresh = { ...context(true), measureConfigurationRevision: "b".repeat(64) }
    bindTemplateMeasureDraft(draft, fresh, [])
    expect(draft.measureConfigurationRevision).toBe("a".repeat(64))
    expect(isTemplateMeasureDraftStale(draft, fresh)).toBe(true)
})

it("无文件能力的 TMDL 确定提交不提升保留 Config 草稿的旧修订, 来源恢复后仍拒绝漂移文件", () => {
    const draft = createConfigurationDraft("zh-cn")
    const available = context(true)
    available.project.model.templateMeasureConfiguration = { mode: "tmdl", measures: available.measureConfiguration!.measures }
    bindTemplateMeasureDraft(draft, available, [])
    selectTemplateMeasureMode(draft, "tmdl", available)
    draft.measures[0]!.value = "独立TMDL编辑"
    selectTemplateMeasureMode(draft, "config", available)
    draft.measures[0]!.value = "保留Config编辑"
    const unavailable = context(false)
    bindTemplateMeasureDraft(draft, unavailable, [])
    unavailable.measureConfiguration!.measures[0]!.value = "已提交TMDL回读值"
    bindTemplateMeasureDraft(draft, unavailable, [], false, "tmdl")
    expect(draft).toMatchObject({ measuresMode: "tmdl", measureConfigurationRevision: null, measures: [{ value: "已提交TMDL回读值" }] })

    const restored = { ...available, measureConfigurationRevision: "b".repeat(64) }
    bindTemplateMeasureDraft(draft, restored, [])
    selectTemplateMeasureMode(draft, "config", restored)
    expect(draft).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "保留Config编辑" }] })
    expect(isTemplateMeasureDraftStale(draft, restored)).toBe(true)
})
