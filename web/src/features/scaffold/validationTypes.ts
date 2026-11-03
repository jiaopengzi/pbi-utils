/**
 * FilePath    : pbi-utils\web\src\features\scaffold\validationTypes.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享配置校验的字段定位与输入契约, 不携带宿主授权.
 */
import type { SampleDataOptions } from "@/bridge/client"
import type {
    PbipTemplateInfo,
    ScaffoldCalendarOptions,
    ScaffoldPeriodOptions,
    ScaffoldPeriodTemplate,
    TemplateDetails,
    TemplateMeasureDefinition,
    TemplateQueryDefinition,
    UdfSelection,
} from "./contracts"

export interface ScaffoldValidationFields {
    template: "templateId" | "projectName" | "output" | "templateQueryGroups"
    sample: "productCount" | "storeCount" | "inventoryCycle" | "startDate" | "endDate" | "seed"
    measures: "measures" | "name" | "value" | "dataCategory" | "description" | "displayFolder"
    calendar:
        | "kind"
        | "fields"
        | "workdays"
        | "tableName"
        | "startYear"
        | "endYear"
        | "overrides"
        | "overrideDate"
        | "overrideName"
        | "overrideIsWorkday"
        | "periodDateTable"
        | "periodDateColumn"
        | "periodMeasureTable"
        | "periodBaseMeasures"
        | "periodTemplates"
        | "periodFolder"
        | "periodNumberDigits"
        | "periodPercentDigits"
    udfs: "udfs" | "source" | "sourceBaseline" | "packageId" | "version" | "functions" | "baseline"
}
export type ScaffoldValidationStep = keyof ScaffoldValidationFields
export type ScaffoldValidationIssue = {
    [Step in ScaffoldValidationStep]: { step: Step; field: ScaffoldValidationFields[Step]; row?: number; key: string; params?: Record<string, string | number> }
}[ScaffoldValidationStep]
export interface ScaffoldValidationInput {
    template?: PbipTemplateInfo
    projectName: string
    output: string
    sample: SampleDataOptions | null
    measures: TemplateMeasureDefinition[] | null
    calendar: ScaffoldCalendarOptions | null
    periods: ScaffoldPeriodOptions | null
    udfs: UdfSelection[] | null
    details?: TemplateDetails
    periodDefinitions?: ScaffoldPeriodTemplate[]
    templateQueries?: string[] | null
    templateQueryGroups?: Record<string, string> | null
    templateQueryDefinitions?: TemplateQueryDefinition[]
}
