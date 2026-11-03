/**
 * FilePath    : pbi-utils\web\tests\support\diagnostics.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为测试记录意外诊断并精确核验局部预期, 保留原输出, 不按日志正则全局放行.
 */
import { expect } from "vitest"
import { isDeepStrictEqual } from "node:util"
import type { Plugin } from "vue"

export type DiagnosticChannel = "warn" | "error" | "vue-warn" | "vue-error" | "window-error" | "window-rejection"
export interface TestDiagnostic {
    channel: DiagnosticChannel
    args: unknown[]
}
export interface DiagnosticConsole {
    warn: (...args: unknown[]) => void
    error: (...args: unknown[]) => void
}

/** 只有完整原参数与当前局部预期匹配时添加单次标记, 保留第一参数的格式占位及其余参数. */
function marked(channel: "warn" | "error", args: unknown[]): unknown[] {
    return typeof args[0] === "string" ? [`[expected console.${channel}] ${args[0]}`, ...args.slice(1)] : [`[expected console.${channel}]`, ...args]
}

/** 一个隔离测试文件的诊断账本; 只消费精确预期成功匹配的片段, 缺失或多余诊断都会失败. */
export class DiagnosticLedger {
    private entries: TestDiagnostic[] = []
    private expected?: { start: number; entries: TestDiagnostic[] }

    /** 记录真实诊断原参数, 不筛选文本或将error转换为成功. */
    record(channel: DiagnosticChannel, args: unknown[]): boolean {
        const entry = { channel, args }
        const matched = this.expected !== undefined && isDeepStrictEqual(entry, this.expected.entries[this.entries.length - this.expected.start])
        this.entries.push(entry)
        return matched
    }

    /** 对局部动作产生的完整诊断序列作精确断言; 不消费动作之前的意外诊断. */
    async expectDuring(expected: TestDiagnostic[], action: () => void | Promise<void>, settle: () => Promise<void> = async () => undefined): Promise<void> {
        if (this.expected) throw new Error("预期诊断动作不得并发或嵌套")
        const start = this.entries.length
        this.expected = { start, entries: expected }
        try {
            await action()
            await settle()
            expect(this.entries.slice(start), "局部动作的诊断参数、顺序和次数必须与明确预期一致").toEqual(expected)
            expect(isDeepStrictEqual(this.entries.slice(start), expected), "预期诊断必须是完整原参数, 不接受模式匹配器").toBe(true)
            this.entries.splice(start)
        } finally {
            this.expected = undefined
        }
    }

    /** 每例及文件结束核验所有未消费诊断; 无论断言是否失败均释放记录, 防止把同一错误串到后续用例. */
    assertEmpty(): void {
        const entries = this.entries.splice(0)
        expect(entries, "测试产生了未明确断言的 console/Vue 诊断").toEqual([])
    }
}

/** 用普通函数包装console而非vi.spyOn, 使restoreAllMocks不会意外卸掉守卫; 输出仍交给原控制台. */
export function guardConsole(target: DiagnosticConsole, ledger: DiagnosticLedger): { assertInstalled: () => void; restore: () => void } {
    const originalWarn = target.warn,
        originalError = target.error
    const warn = (...args: unknown[]) => {
        originalWarn.apply(target, ledger.record("warn", args) ? marked("warn", args) : args)
    }
    const error = (...args: unknown[]) => {
        originalError.apply(target, ledger.record("error", args) ? marked("error", args) : args)
    }
    target.warn = warn
    target.error = error
    return {
        assertInstalled: () => {
            expect(target.warn, "测试不能替换console.warn守卫, 预期诊断应使用局部精确断言").toBe(warn)
            expect(target.error, "测试不能替换console.error守卫, 预期诊断应使用局部精确断言").toBe(error)
        },
        restore: () => {
            target.warn = originalWarn
            target.error = originalError
        },
    }
}

/** 记录浏览器全局错误事件并保留原事件传播和输出; 不 preventDefault, 不把 DOM 环境事件当作已由 Node 异常门禁覆盖. */
export function guardWindow(target: Window, ledger: DiagnosticLedger, output: DiagnosticConsole): () => void {
    const error = (event: ErrorEvent) => {
        ledger.record("window-error", [event])
        output.error(event.error ?? event.message)
    }
    const rejection = (event: PromiseRejectionEvent) => {
        ledger.record("window-rejection", [event])
        output.error(event.reason)
    }
    target.addEventListener("error", error)
    target.addEventListener("unhandledrejection", rejection)
    return () => {
        target.removeEventListener("error", error)
        target.removeEventListener("unhandledrejection", rejection)
    }
}

/** 在VTU合并局部app配置后包裹Vue处理器, 自定义handler仍收到原事件, 同时不能绕过文件诊断门禁. */
export function createVueDiagnostics(ledger: DiagnosticLedger, output: DiagnosticConsole): Plugin {
    return {
        install(app) {
            const warn = app.config.warnHandler,
                error = app.config.errorHandler
            app.config.warnHandler = (message, instance, trace) => {
                ledger.record("vue-warn", [message])
                output.warn(`[Vue warn]: ${message}`, trace)
                warn?.(message, instance, trace)
            }
            app.config.errorHandler = (cause, instance, info) => {
                ledger.record("vue-error", [cause, info])
                output.error(cause, info)
                error?.(cause, instance, info)
            }
        },
    }
}
