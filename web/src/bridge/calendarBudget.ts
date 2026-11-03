/**
 * FilePath    : pbi-utils\web\src\bridge\calendarBudget.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 与宿主一致的日历信封预算, 其他字段维持64KiB.
 */
const encoder = new TextEncoder()
/** 只接受对象分段, 不以方法前缀或网页自报权限放宽消息. */
function object(value: unknown): Record<string, unknown> | undefined {
    return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined
}
/** 计算完整JSON的UTF8字节数, 转义字符同样消耗额度. */
function bytes(value: unknown): number {
    return encoder.encode(JSON.stringify(value)).length
}
/** 绝对4MiB, 仅实际日历分段可超出64KiB; 后端继续验证各领域内容及配额. */
export function calendarEnvelopeAllowed(method: string, params: unknown, envelopeBytes: number): boolean {
    if (envelopeBytes > 4 * 1024 * 1024) return false
    if (envelopeBytes <= 65536) return true
    const input = object(params)
    if (!input) return false
    let calendar: unknown
    if (method === "scaffold.updateDraft" && input.section === "calendar") calendar = input.value
    else if (method === "configuration.apply" && input.section === "calendar") calendar = object(input.request)?.calendar
    else if (method === "calendar.preview") calendar = input.calendar
    else if (method === "calendar.parseWorkdays" && typeof input.text === "string")
        return encoder.encode(input.text).length <= 2 * 1024 * 1024 && envelopeBytes - bytes(input.text) <= 65536
    return !!object(calendar) && envelopeBytes - bytes(calendar) <= 65536
}
