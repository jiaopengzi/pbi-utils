/**
 * FilePath    : pbi-utils\web\src\features\scaffold\validation.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 新建与模型配置纯校验入口, 按流程顺序返回首个问题.
 */
import type { ScaffoldValidationInput, ScaffoldValidationIssue } from "./validationTypes"
export type { ScaffoldValidationFields, ScaffoldValidationStep, ScaffoldValidationIssue, ScaffoldValidationInput } from "./validationTypes"
import { blank, boundarySpace, controls, digest, issue, objectName, queryGroupName } from "./validationRules"
import { calendarIssue, periodsIssue } from "./validationCalendar"
import { sampleIssue, measuresIssue, udfIssue } from "./validationSections"
/** 校验基础模板、单目录名称、宿主目录及已选辅助表分组, 不触碰文件系统或未选草稿. */
export function validateScaffoldBase(
    input: Pick<ScaffoldValidationInput, "template" | "projectName" | "output" | "templateQueries" | "templateQueryGroups" | "templateQueryDefinitions">,
): Extract<ScaffoldValidationIssue, { step: "template" }> | undefined {
    const { template, projectName, output } = input
    if (!template || !objectName(template.id) || !digest(template.baseline)) return issue("template", "templateId", "Template")
    if (
        blank(projectName) ||
        projectName.length > 100 ||
        boundarySpace.test(projectName) ||
        projectName.endsWith(".") ||
        controls.test(projectName) ||
        /[<>:"/\\|?*]/.test(projectName) ||
        projectName === "." ||
        projectName === ".." ||
        /^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i.test(projectName.split(".")[0]!)
    )
        return issue("template", "projectName", "ProjectName")
    if (blank(output)) return issue("template", "output", "Output")
    return templateQueryGroupsIssue(input)
}
/** 已选项才校验实际分组, 目录顺序对应真实字段行; 不修改覆盖map或宿主固定目录. */
export function templateQueryGroupsIssue(
    input: Pick<ScaffoldValidationInput, "templateQueries" | "templateQueryGroups" | "templateQueryDefinitions">,
): Extract<ScaffoldValidationIssue, { step: "template" }> | undefined {
    for (const [index, query] of (input.templateQueryDefinitions ?? []).entries()) {
        if (!input.templateQueries?.includes(query.id)) continue
        const value = input.templateQueryGroups && Object.hasOwn(input.templateQueryGroups, query.id) ? input.templateQueryGroups[query.id] : query.queryGroup
        if (!queryGroupName(value)) return issue("template", "templateQueryGroups", "QueryGroup", index + 1)
    }
    return undefined
}
/** 按可见流程顺序返回首个确定输入错误; null 表示关闭配置, 不推断宿主安全或网络错误. */
export function validateScaffold(input: ScaffoldValidationInput): ScaffoldValidationIssue | undefined {
    return validateScaffoldBase(input) ?? validateConfiguration(input)
}

/** 对现有模型与新建共用可选段校验, 目标授权和基线完全由宿主负责. */
export function validateConfiguration(
    input: Pick<ScaffoldValidationInput, "sample" | "measures" | "calendar" | "periods" | "udfs" | "details" | "periodDefinitions">,
): ScaffoldValidationIssue | undefined {
    return sampleIssue(input.sample) ?? measuresIssue(input.measures) ?? calendarIssue(input.calendar) ?? periodsIssue(input) ?? udfIssue(input.udfs)
}
