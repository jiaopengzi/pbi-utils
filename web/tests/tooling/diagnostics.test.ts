/**
 * FilePath    : pbi-utils\web\tests\tooling\diagnostics.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证诊断门禁对意外、缺失、多余、恢复mock及Vue局部处理器的真实行为, 不修改产品代码.
 */
import { expect, it, vi } from "vitest"
import { createRenderer, defineComponent, h } from "vue"
import { createVueDiagnostics, DiagnosticLedger, guardConsole, guardWindow } from "../support/diagnostics"

it.each(["warn", "error"] as const)("%s原输出保留, restoreAllMocks后意外诊断仍失败", (channel) => {
    const ledger = new DiagnosticLedger(),
        output = { warn: vi.fn(), error: vi.fn() }
    const original = output[channel]
    const guard = guardConsole(output, ledger)
    vi.restoreAllMocks()
    output[channel]("意外诊断", { code: "fixture" })
    expect(original).toHaveBeenCalledExactlyOnceWith("意外诊断", { code: "fixture" })
    expect(() => ledger.assertEmpty()).toThrow("未明确断言")
    guard.assertInstalled()
    guard.restore()
    expect(output[channel]).toBe(original)
})

it("精确预期只消费局部动作的完整序列, 动作外意外告警仍失败", async () => {
    const ledger = new DiagnosticLedger()
    ledger.record("warn", ["动作外"])
    await ledger.expectDuring([{ channel: "warn", args: ["async-validator:", ["field is required"]] }], async () => {
        await Promise.resolve()
        ledger.record("warn", ["async-validator:", ["field is required"]])
    })
    expect(() => ledger.assertEmpty()).toThrow("未明确断言")
})

it("明确匹配的预期诊断保留原参数内容并只输出一次标记, 无需吞掉控制台", async () => {
    const ledger = new DiagnosticLedger(),
        output = { warn: vi.fn(), error: vi.fn() }
    const original = output.warn
    const guard = guardConsole(output, ledger)
    await ledger.expectDuring([{ channel: "warn", args: ["async-validator:", ["field is required"]] }], () => {
        output.warn("async-validator:", ["field is required"])
    })
    expect(original).toHaveBeenCalledExactlyOnceWith("[expected console.warn] async-validator:", ["field is required"])
    ledger.assertEmpty()
    guard.restore()
})

it.each(["missing", "extra", "wrong-args"])("预期%s不会误消费或静默通过", async (mode) => {
    const ledger = new DiagnosticLedger()
    await expect(
        ledger.expectDuring([{ channel: "warn", args: ["expected"] }], () => {
            if (mode !== "missing") ledger.record("warn", [mode === "wrong-args" ? "different" : "expected"])
            if (mode === "extra") ledger.record("warn", ["extra"])
        }),
    ).rejects.toThrow("参数、顺序和次数")
    if (mode !== "missing") expect(() => ledger.assertEmpty()).toThrow("未明确断言")
})

it("局部预期也不能用宽泛参数匹配器放过任意诊断", async () => {
    const ledger = new DiagnosticLedger()
    await expect(
        ledger.expectDuring([{ channel: "warn", args: [expect.any(String)] }], () => {
            ledger.record("warn", ["unexpected"])
        }),
    ).rejects.toThrow("不接受模式匹配器")
    expect(() => ledger.assertEmpty()).toThrow("未明确断言")
})

it("局部Vue warnHandler仍收到事件但不能隐藏非法props, 错误仍进入账本并保留输出", () => {
    const ledger = new DiagnosticLedger(),
        output = { warn: vi.fn(), error: vi.fn() },
        handler = vi.fn()
    const renderer = createRenderer({
        createElement: () => ({}),
        createText: () => ({}),
        createComment: () => ({}),
        insert: () => undefined,
        remove: () => undefined,
        setText: () => undefined,
        setElementText: () => undefined,
        parentNode: () => null,
        nextSibling: () => null,
        patchProp: () => undefined,
    })
    const component = defineComponent({ props: { count: { type: Number, required: true } }, render: () => h("div") })
    const app = renderer.createApp(component, { count: "wrong" })
    app.config.warnHandler = handler
    app.use(createVueDiagnostics(ledger, output))
    app.mount({})
    app.unmount()
    expect(handler).toHaveBeenCalledOnce()
    expect(output.warn).toHaveBeenCalledOnce()
    expect(() => ledger.assertEmpty()).toThrow("未明确断言")
})

it.each(["error", "unhandledrejection"] as const)("window %s不能静默成功, 保留原事件传播及清理", (channel) => {
    const ledger = new DiagnosticLedger(),
        output = { warn: vi.fn(), error: vi.fn() }
    // 使用独立window事件目标, 使被测负向事件不污染当前用例自身的全局门禁.
    const target = new EventTarget() as Window
    const restore = guardWindow(target, ledger, output)
    const cause = new Error("window fixture")
    const event = channel === "error" ? new ErrorEvent("error", { error: cause, cancelable: true }) : new Event("unhandledrejection", { cancelable: true })
    if (channel === "unhandledrejection") Object.defineProperty(event, "reason", { value: cause })
    expect(target.dispatchEvent(event)).toBe(true)
    expect(event.defaultPrevented).toBe(false)
    expect(output.error).toHaveBeenCalledExactlyOnceWith(cause)
    expect(() => ledger.assertEmpty()).toThrow("未明确断言")
    restore()
    target.dispatchEvent(event)
    ledger.assertEmpty()
    expect(output.error).toHaveBeenCalledOnce()
})
