/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfDateTime.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 将来源绝对时间或缓存获取时间显示为当前机器本地时间, 不更改存储值或附加时区尾缀.
 */
/** 将时间分量补足两位, 不改变其本地时区含义. */
function pad(part: number): string {
    return String(part).padStart(2, "0")
}
/** 显示 YYYY-MM-DD HH:mm:ss; 空/损坏时间返回共享未知值. */
export function formatUdfLocalTime(value: string | null | undefined): string {
    if (!value) return "-"
    const date = new Date(value)
    if (!Number.isFinite(date.getTime())) return "-"
    return `${String(date.getFullYear()).padStart(4, "0")}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}
