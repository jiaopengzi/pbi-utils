/**
 * FilePath    : pbi-utils\web\src\features\scaffold\validationSections.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 示例、模板度量值与UDF选择的纯字段校验.
 */
import type { SampleDataOptions } from "@/bridge/client"
import type { TemplateMeasureDefinition, UdfSelection } from "./contracts"
import type { ScaffoldValidationIssue } from "./validationTypes"
import { isTemplateMeasureCategory } from "./dataCategories"
import { absent, date, digest, integer, issue, nameKey, objectName, semver, text } from "./validationRules"
/** 复用示例日期解析并逐字段对应共享生成服务的整数、60天窗口与生日偏移边界. */
export function sampleIssue(sample: SampleDataOptions | null): ScaffoldValidationIssue | undefined {
    if (absent(sample)) return undefined
    for (const [field, min, max] of [
        ["productCount", 1, 10000],
        ["storeCount", 1, 10000],
        ["inventoryCycle", 5, 180],
    ] as const)
        if (!integer(sample[field], min, max)) return issue("sample", field, "Range", undefined, { min, max })
    const start = date(sample.startDate),
        end = date(sample.endDate)
    if (!Number.isFinite(start)) return issue("sample", "startDate", "Date")
    if (!Number.isFinite(end)) return issue("sample", "endDate", "Date")
    if (end - start < 60 * 86400000) return issue("sample", "endDate", "DateGap", undefined, { days: 60 })
    if (!integer(Number(sample.endDate.slice(0, 4)), 61, 9985)) return issue("sample", "endDate", "Range", undefined, { min: 61, max: 9985 })
    if (!absent(sample.seed) && !integer(sample.seed, -2147483648, 2147483647)) return issue("sample", "seed", "Seed")
    return undefined
}

/** 六字段配置段关闭时不读取; 行级关闭仍会保存配置并隐藏旧对象, 因此与宿主一样校验全行. */
export function measuresIssue(measures: TemplateMeasureDefinition[] | null): ScaffoldValidationIssue | undefined {
    if (absent(measures)) return undefined
    if (!Array.isArray(measures) || measures.length > 128) return issue("measures", "measures", "Count", undefined, { min: 0, max: 128 })
    const names = new Set<string>()
    for (const [index, item] of measures.entries()) {
        const row = index + 1
        if (!item || !objectName(item.name)) return issue("measures", "name", "Name", row)
        const key = nameKey(item.name)
        if (names.has(key)) return issue("measures", "name", "Duplicate", row)
        names.add(key)
        if (!isTemplateMeasureCategory(item.dataCategory)) return issue("measures", "dataCategory", "Category", row)
        for (const [field, max] of [
            ["value", 32768],
            ["description", 8192],
            ["displayFolder", 200],
        ] as const)
            if (!text(item[field], max)) return issue("measures", field, "Text", row, { max })
    }
    return undefined
}

/** 只检查选择本身的语法与已读基线, 不猜测远端包内容; 完整依赖由宿主权威核验. */
export function udfIssue(selections: UdfSelection[] | null): ScaffoldValidationIssue | undefined {
    if (absent(selections)) return undefined
    if (!Array.isArray(selections) || selections.length < 1 || selections.length > 128) return issue("udfs", "udfs", "Count", undefined, { min: 1, max: 128 })
    const packages = new Set<string>(),
        sources = new Map<string, string | null>()
    for (const [index, selection] of selections.entries()) {
        const row = index + 1
        if (!selection || (!["daxlib", "local"].includes(selection.source) && !/^source:[0-9a-f]{32}$/.test(selection.source)))
            return issue("udfs", "source", "Source", row)
        if (selection.source.startsWith("source:") && !digest(selection.sourceBaseline)) return issue("udfs", "sourceBaseline", "SourceBaseline", row)
        const sourceBaseline = selection.sourceBaseline ?? null
        if (sources.has(selection.source) && sources.get(selection.source) !== sourceBaseline) return issue("udfs", "sourceBaseline", "SourceBaseline", row)
        sources.set(selection.source, sourceBaseline)
        if (
            selection.source === "local"
                ? !/^[0-9a-fA-F]{32}$/.test(selection.packageId)
                : typeof selection.packageId !== "string" || selection.packageId.length > 128 || !/^[A-Za-z_][A-Za-z0-9_.-]*$/.test(selection.packageId)
        )
            return issue("udfs", "packageId", "Package", row)
        const key = nameKey(selection.source + ":" + selection.packageId)
        if (packages.has(key)) return issue("udfs", "packageId", "Duplicate", row)
        packages.add(key)
        if (
            !Array.isArray(selection.functions) ||
            selection.functions.length < 1 ||
            selection.functions.length > (selection.source === "local" ? 1 : 128) ||
            selection.functions.some((name) => !objectName(name))
        )
            return issue("udfs", "functions", "Package", row)
        if (selection.source === "local" && !digest(selection.baseline)) return issue("udfs", "baseline", "Baseline", row)
        if (
            !absent(selection.version) &&
            (!text(selection.version, 100) ||
                !((selection.source !== "local" && (selection.version === "latest" || selection.version === "")) || semver.test(selection.version)))
        )
            return issue("udfs", "version", "Version", row)
        // 远端和本地精确版本均允许预发行; 宿主继续核验完整依赖闭包与内容基线.
    }
    return undefined
}
