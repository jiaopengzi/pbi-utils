/**
 * FilePath    : pbi-utils\web\src\bridge\modelExportProgress.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 严格解析导出的有界单调进度, 拒绝异源、未知字段、倒退及不可能计数.
 */
import type { ModelTableExportProgress } from "./protocol"

const stages = ["reading", "verifying", "finalizing", "publishing"] as const
const fields = ["requestId", "kind", "stage", "completedTables", "totalTables", "tableIndex", "rowsRead"]

/**
 * 仅接受当前请求同一表集合的真实计数; 重复、倒退和畸形通知返回undefined, 不改变原请求预算或终态.
 * @param value 未信任的宿主事件负载.
 * @param totalTables 请求发出时冻结的所选表数.
 * @param previous 当前请求已经接受的最后进度.
 */
export function parseModelExportProgress(value: unknown, totalTables: number, previous?: ModelTableExportProgress): ModelTableExportProgress | undefined {
    if (!value || typeof value !== "object" || Array.isArray(value)) return
    const item = value as Record<string, unknown>
    if (Object.keys(item).length !== fields.length || Object.keys(item).some((key) => !fields.includes(key))) return
    if (typeof item.requestId !== "string" || !item.requestId || item.requestId.length > 100 || item.kind !== "tableExport") return
    if (!stages.includes(item.stage as ModelTableExportProgress["stage"])) return
    const { completedTables, tableIndex, rowsRead } = item
    if (!Number.isInteger(totalTables) || totalTables < 1 || totalTables > 2000 || item.totalTables !== totalTables) return
    if (typeof completedTables !== "number" || !Number.isInteger(completedTables) || completedTables < 0 || completedTables > totalTables) return
    if (typeof tableIndex !== "number" || !Number.isInteger(tableIndex) || tableIndex < 1 || tableIndex > totalTables || completedTables > tableIndex) return
    if (typeof rowsRead !== "number" || !Number.isSafeInteger(rowsRead) || rowsRead < 0) return
    if (item.stage === "reading" && completedTables !== tableIndex - 1) return
    if (item.stage === "verifying" && completedTables < tableIndex - 1) return
    if (["finalizing", "publishing"].includes(String(item.stage)) && (completedTables !== totalTables || tableIndex !== totalTables)) return
    const current = item as unknown as ModelTableExportProgress
    if (previous) {
        if (current.requestId !== previous.requestId || tableIndex < previous.tableIndex || completedTables < previous.completedTables) return
        if (tableIndex === previous.tableIndex) {
            if (rowsRead < previous.rowsRead || stages.indexOf(current.stage) < stages.indexOf(previous.stage)) return
            if (rowsRead === previous.rowsRead && completedTables === previous.completedTables && current.stage === previous.stage) return
        } else if (previous.stage === "finalizing" || previous.stage === "publishing") return
    }
    return current
}
