/**
 * FilePath    : pbi-utils\web\src\ui\numberAccessibility.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 同步组合数字输入的动态原生状态与无障碍属性, 保留第三方 DOM 和交互.
 */
import type { ObjectDirective } from "vue"
/** 以实际输入的禁用属性与范围为准, 修正第三方仅挂载时写入的 ARIA 快照. */
function synchronize(element: HTMLElement): void {
    const input = element.querySelector("input")
    if (!input) return
    input.setAttribute("aria-disabled", String(input.disabled))
    for (const bound of ["min", "max"] as const) {
        const value = input.getAttribute(bound)
        if (value !== null && value !== "" && Number.isFinite(Number(value))) input.setAttribute(`aria-value${bound}`, value)
        else input.removeAttribute(`aria-value${bound}`)
    }
}
export const vNumberAccessibility: ObjectDirective<HTMLElement> = { mounted: synchronize, updated: synchronize }
