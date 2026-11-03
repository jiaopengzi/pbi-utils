/**
 * FilePath    : pbi-utils\web\tests\support\testDiagnostics.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 注册每例诊断门禁与精确预期入口, 在业务清理后检查异步诊断且保留原始输出.
 */
import { afterAll, afterEach, beforeEach } from "vitest"
import { setImmediate } from "node:timers"
import { createVueDiagnostics, DiagnosticLedger, guardConsole, guardWindow } from "./diagnostics"

const ledger = new DiagnosticLedger()

/** 排空当前事件轮及已排程微任务, 使用真实Node回调避免测试fake timers令门禁悬挂; 不等待任意未来定时任务. */
async function settleDiagnostics(): Promise<void> {
    await new Promise<void>((resolve) => setImmediate(resolve))
}

/** 安装一次文件级守卫; 先于setup清理hook注册, 保证Vitest默认stack顺序下最后核验清理期间的诊断. */
export function installTestDiagnostics() {
    const output = { warn: console.warn.bind(console), error: console.error.bind(console) }
    const consoleGuard = guardConsole(console, ledger)
    const restoreWindow = typeof window === "undefined" ? undefined : guardWindow(window, ledger, output)
    beforeEach(() => ledger.assertEmpty())
    afterEach(async () => {
        await settleDiagnostics()
        consoleGuard.assertInstalled()
        ledger.assertEmpty()
    })
    afterAll(async () => {
        try {
            await settleDiagnostics()
            consoleGuard.assertInstalled()
            ledger.assertEmpty()
        } finally {
            consoleGuard.restore()
            restoreWindow?.()
        }
    })
    return createVueDiagnostics(ledger, output)
}

/** 仅放行此动作完整匹配的warn参数及次数; 不匹配、缺失、多余或动作外的诊断仍使测试失败. */
export async function expectConsoleWarnings(args: unknown[][], action: () => void | Promise<void>): Promise<void> {
    await ledger.expectDuring(
        args.map((value) => ({ channel: "warn", args: value })),
        action,
        settleDiagnostics,
    )
}
