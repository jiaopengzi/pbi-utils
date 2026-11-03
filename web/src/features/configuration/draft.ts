/**
 * FilePath    : pbi-utils\web\src\features\configuration\draft.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 两个配置入口共用草稿, 可选段请求与中性目录默认绑定, 不保存路径授权.
 */
import { reactive } from "vue"
import type { AppLocale } from "@/i18n"
import type { ModelConfigurationRequest } from "@/bridge/client"
import { defaultOptions } from "@/features/sample/presentation"
import { effectiveFields, recommendedFields } from "../scaffold/calendarFields"
import { calendarPreferenceDraftState } from "../scaffold/calendarPreferences"
import type {
    ScaffoldCalendarOptions,
    ScaffoldPeriodOptions,
    TemplateMeasureDefinition,
    TemplateMeasureMode,
    UdfSelection,
    ModelConfigurationCatalog,
} from "../scaffold/contracts"
export type ConfigurationSection = "sample" | "measures" | "calendar" | "udfs"
export type ConfigurationOperation = ConfigurationSection | "periods"
export interface ConfigurationDraft {
    measuresEnabled: boolean
    measuresMode: TemplateMeasureMode
    /** 模型配置草稿绑定的文件修订; 新建草稿不持有宿主文件事实. */
    measureConfigurationRevision?: string | null
    sampleEnabled: boolean
    calendarEnabled: boolean
    periodsEnabled: boolean
    udfsEnabled: boolean
    measures: TemplateMeasureDefinition[]
    sample: ReturnType<typeof defaultOptions>
    calendar: ScaffoldCalendarOptions
    periods: ScaffoldPeriodOptions
    udfs: UdfSelection[]
}
const initializedPeriods = new WeakSet<ConfigurationDraft>()
/** 创建独立配置草稿, 同目标刷新由调用方保留此对象, 目标切换不得复用其身份. */
export function createConfigurationDraft(locale: AppLocale): ConfigurationDraft {
    const draft = reactive({
        measuresEnabled: false,
        measuresMode: "tmdl" as TemplateMeasureMode,
        sampleEnabled: false,
        calendarEnabled: false,
        periodsEnabled: false,
        udfsEnabled: false,
        measures: [],
        sample: defaultOptions(),
        calendar: {
            kind: "simple",
            startYear: new Date().getFullYear() - 4,
            endYear: new Date().getFullYear(),
            tableName: "Calendar",
            overrides: [],
            fields: [...recommendedFields],
            numberedColumns: true,
            workdays: [0, 1, 2, 3, 4],
        },
        periods: {
            dateTable: "",
            dateColumn: "",
            measureTable: "",
            baseMeasures: [],
            templates: [],
            folder: locale === "en-us" ? "Time intelligence" : "时间智能",
            numberDigits: 2,
            percentDigits: 2,
        },
        udfs: [],
    })
    calendarPreferenceDraftState(draft.calendar)
    return draft
}
/** 关闭段返回 null; 模型指定动作仅提交日期或时间智能其中一段, 不传动作的新建请求保留组合生成, 原草稿不变. */
export function configurationRequest(draft: ConfigurationDraft, locale: AppLocale, section?: ConfigurationOperation): ModelConfigurationRequest {
    return { locale, ...configurationSections(draft, locale, section) }
}
/** 新建冻结组合开启段, 现有模型按 section 动作隔离提交; 只读返回请求段数据, 不修改其他段和路径授权. */
export function configurationSections(draft: ConfigurationDraft, locale: AppLocale, section?: ConfigurationOperation) {
    const includeMeasures = (!section || section === "measures") && draft.measuresEnabled
    return {
        measures: includeMeasures ? draft.measures : null,
        ...(includeMeasures
            ? {
                  measuresMode: draft.measuresMode,
                  ...(draft.measureConfigurationRevision !== undefined ? { measureConfigurationRevision: draft.measureConfigurationRevision } : {}),
              }
            : {}),
        sample: (!section || section === "sample") && draft.sampleEnabled ? { ...draft.sample, locale } : null,
        calendar: (!section || section === "calendar") && draft.calendarEnabled ? draft.calendar : null,
        periods: (!section || section === "periods") && draft.periodsEnabled ? draft.periods : null,
        udfs: (!section || section === "udfs") && draft.udfsEnabled ? draft.udfs : null,
    }
}
/** 仅初始日期草稿绑定既有目标字段, 新建模板持续合并必需锁并保留主动选择; 默认角色来自当前目录, 不放宽字段校验. */
export function initializeConfigurationCatalog(draft: ConfigurationDraft, catalog: ModelConfigurationCatalog): void {
    const state = calendarPreferenceDraftState(draft.calendar)
    const pristine = !state.initialized && state.baseline === JSON.stringify(draft.calendar)
    if (!initializedPeriods.has(draft)) {
        if (catalog.periods) {
            draft.periods = { ...catalog.periods, baseMeasures: [...catalog.periods.baseMeasures], templates: [...catalog.periods.templates] }
            draft.periodsEnabled = true
        } else {
            draft.periods.dateTable = catalog.defaultDateTable ?? ""
            draft.periods.dateColumn = catalog.defaultDateColumn ?? ""
            draft.periods.measureTable = catalog.defaultMeasureTable ?? ""
        }
        // 只有当前模型目录带有已配置事实; 新建模板仍按每次模板目录初始化绑定.
        if ("periods" in catalog) initializedPeriods.add(draft)
    }
    const capability = catalog.calendarCapabilities?.find((item) => item.tableName === draft.calendar.tableName)
    if (capability && (pristine || capability.mode !== "existing"))
        draft.calendar.fields = effectiveFields(
            capability.mode === "existing" ? capability.fields : (draft.calendar.fields ?? recommendedFields),
            capability.lockedFields,
        )
    if (pristine) state.baseline = JSON.stringify(draft.calendar)
}
/** 当前功能存在开启段才显示可执行动作; 关闭仅表示本次不提交. */
export function sectionEnabled(draft: ConfigurationDraft, section: ConfigurationOperation): boolean {
    return section === "sample"
        ? draft.sampleEnabled
        : section === "measures"
          ? draft.measuresEnabled
          : section === "calendar"
            ? draft.calendarEnabled
            : section === "periods"
              ? draft.periodsEnabled
              : section === "udfs" && draft.udfsEnabled
}
