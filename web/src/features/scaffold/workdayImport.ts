/**
 * FilePath    : pbi-utils\web\src\features\scaffold\workdayImport.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 工作日导入的纯草稿预览与原子合并, 日期解析始终由宿主权威处理.
 */
import type { WorkdayOverride } from "./contracts"
const importErrors: Record<string, string> = {
    WORKDAY_COUNT: "importCountBudget",
    WORKDAY_BYTES: "importFinalBudget",
    WORKDAY_DUPLICATE: "importDuplicateDraft",
    "原文必须为有效 UTF-8 且不超过 2 MiB": "importErrorText",
    "请选择 CSV 或 TSV 分隔符": "importErrorDelimiter",
    "表头必须为 date,isWorkday,name 或声明的中英文别名": "importErrorHeader",
    必须恰好包含三列: "importErrorColumns",
    字段内不允许控制字符或换行: "importErrorControls",
    "工作标志只能为 true/false/1/0/是/否/工作日/休息日": "importErrorFlag",
    "日期无效、重复、超出范围或名称无效": "importErrorDate",
    "工作日覆盖日期无效、重复、超出范围或名称无效": "importErrorDate",
    "工作日覆盖合并后最多 10000 条": "importCountBudget",
    "工作日覆盖最多 10000 条": "importCountBudget",
    "工作日覆盖合并后的规范化 UTF-8 JSON 不得超过 512 KiB": "importFinalBudget",
    "不允许控制字符或非开头 BOM": "importErrorBom",
    引号只能位于字段开头并成对闭合: "importErrorQuote",
    闭合引号后只能为分隔符或换行: "importErrorQuoteSuffix",
    引用字段缺少闭合引号: "importErrorQuoteClose",
    "工作日覆盖年份需为 1900–2100 且起始不晚于结束": "importErrorRange",
}
/** 仅翻译宿主明确声明的固定错误, 保留原行号; 未知诊断全文保持原样. */
export function workdayImportMessage(message: string, translate: (key: string, params?: Record<string, string | number>) => string): string {
    const row = message.match(/^第 ([0-9]+) 行: (.*)$/u)
    const key = importErrors[row ? row[2]! : message]
    if (!key) return message
    const translated = translate(`scaffold.${key}`)
    return row ? translate("scaffold.importRowError", { row: row[1]!, message: translated }) : translated
}
export interface WorkdayPreview {
    item: WorkdayOverride
    existing?: WorkdayOverride
    state: "added" | "same" | "conflict"
}
/** 宿主已成功验证三列内容后, 只提取日期所在原始物理行号供预览, 不据此授权或应用记录. */
export function workdaySourceLines(text: string, delimiter: "csv" | "tsv"): Map<string, number> {
    const separator = delimiter === "tsv" ? "\t" : ","
    const result = new Map<string, number>()
    for (const [index, line] of text
        .replace(/^\uFEFF/, "")
        .split(/\r\n|\r|\n/)
        .entries()) {
        const first = (line.startsWith('"') ? line.match(/^"([^"]*)"/)?.[1] : line.split(separator)[0])?.trim()
        if (first && /^\d{4}-\d{2}-\d{2}$/.test(first)) result.set(first, index + 1)
    }
    return result
}
/** 将宿主整批解析结果与当前草稿比较, 不修改任何输入. */
export function previewWorkdays(existing: WorkdayOverride[], incoming: WorkdayOverride[]): WorkdayPreview[] {
    const byDate = new Map(existing.map((item) => [item.date, item]))
    return incoming.map((item) => {
        const old = byDate.get(item.date)
        return { item, existing: old, state: !old ? "added" : old.isWorkday === item.isWorkday && old.name === item.name ? "same" : "conflict" }
    })
}
/** 与共享规范化序列化相同的固定属性顺序, 使用 UTF-8 字节预算而不是字符数. */
export function workdayBudget(items: WorkdayOverride[]): "count" | "bytes" | undefined {
    if (items.length > 10000) return "count"
    const normalized = items
        .toSorted((a, b) => a.date.localeCompare(b.date))
        .map(({ date, isWorkday, name }) => ({ date, isWorkday, name: name.replace(/^\p{White_Space}+|\p{White_Space}+$/gu, "") }))
    return new TextEncoder().encode(JSON.stringify(normalized)).length > 512 * 1024 ? "bytes" : undefined
}
/** 整批合并并验证最终全部覆盖预算, 默认保留冲突, 超限抛错且不部分返回. */
export function mergeWorkdays(existing: WorkdayOverride[], incoming: WorkdayOverride[], replace: boolean): WorkdayOverride[] {
    if (new Set(existing.map((item) => item.date)).size !== existing.length) throw new Error("WORKDAY_DUPLICATE")
    const merged = new Map(existing.map((item) => [item.date, { ...item }]))
    for (const item of incoming) if (replace || !merged.has(item.date)) merged.set(item.date, { ...item })
    const result = [...merged.values()].toSorted((a, b) => a.date.localeCompare(b.date))
    const budget = workdayBudget(result)
    if (budget) throw new Error(budget === "count" ? "WORKDAY_COUNT" : "WORKDAY_BYTES")
    return result
}
