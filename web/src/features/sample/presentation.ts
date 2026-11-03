/**
 * FilePath    : pbi-utils\web\src\features\sample\presentation.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 示例数据界面的日期校验和中英文文案, 不生成业务数据.
 */
import type { SampleDataOptions } from "@/bridge/client"

const dayMs = 86400000
/** 格式化本地日历日期, date 为有效日期, 返回 YYYY-MM-DD. */
export function localDate(date: Date): string {
    return [String(date.getFullYear()).padStart(4, "0"), String(date.getMonth() + 1).padStart(2, "0"), String(date.getDate()).padStart(2, "0")].join("-")
}
/** 读取纯日历日期为 UTC 毫秒, value 不合法时返回 NaN, 避免夏令时影响窗口天数. */
export function dateMillis(value: string): number {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value) || value.startsWith("0000-")) return NaN
    const result = Date.parse(`${value}T00:00:00Z`)
    return Number.isFinite(result) && new Date(result).toISOString().slice(0, 10) === value ? result : NaN
}
/** 返回事实截止日的次日, value 不合法或超过四位年份时返回空字符串. */
export function followingDate(value: string): string {
    const result = dateMillis(value) + dayMs
    if (!Number.isFinite(result)) return ""
    const date = new Date(result).toISOString().slice(0, 10)
    return /^\d{4}-\d{2}-\d{2}$/.test(date) ? date : ""
}
/** 从本地结束日推导上游默认参数, now 可注入以便验证; 起点为同一日历日期减 1600 天, 不包含输出路径. */
export function defaultOptions(now = new Date()): SampleDataOptions {
    const endDate = localDate(now)
    const startDate = new Date(dateMillis(endDate) - 1600 * dayMs).toISOString().slice(0, 10)
    return { locale: "zh-cn", productCount: 666, storeCount: 55, inventoryCycle: 22, startDate, endDate }
}
/** 验证业务参数并返回首个文案键, options 为待生成参数; 空字符串表示可提交, 后端再次权威校验. */
export function optionsError(options: SampleDataOptions): "" | "productsError" | "storesError" | "inventoryError" | "dateError" | "dateYearError" {
    if (!Number.isInteger(options.productCount) || options.productCount < 1 || options.productCount > 10000) return "productsError"
    if (!Number.isInteger(options.storeCount) || options.storeCount < 1 || options.storeCount > 10000) return "storesError"
    if (!Number.isInteger(options.inventoryCycle) || options.inventoryCycle < 5 || options.inventoryCycle > 180) return "inventoryError"
    const start = dateMillis(options.startDate),
        end = dateMillis(options.endDate)
    if (!Number.isFinite(start) || !Number.isFinite(end) || end - start < 60 * dayMs) return "dateError"
    const endYear = Number(options.endDate.slice(0, 4))
    if (endYear < 61 || endYear > 9985) return "dateYearError"
    return ""
}
/** 格式化产物大小, size 为字节数, 返回 B/KB/MB/GB 文本. */
export function formatSize(size: number): string {
    if (size < 1024) return `${size} B`
    let value = size,
        index = 0
    while (value >= 1024 && index < 4) {
        value /= 1024
        index++
    }
    return `${value.toFixed(1)} ${["B", "KB", "MB", "GB", "TB"][index]}`
}
