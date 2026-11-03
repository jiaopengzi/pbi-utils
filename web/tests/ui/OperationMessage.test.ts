/**
 * FilePath    : pbi-utils\web\tests\ui\OperationMessage.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证全局在途消息的原位更新、独立生命周期、取消排空和卸载迟到边界.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises } from "@vue/test-utils"
import { effectScope, ref, type EffectScope } from "vue"
import { ElMessage } from "element-plus"
import { setLocale } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import { useOperationMessage } from "@/composables/ui/useFeedback"

const scopes: EffectScope[] = []
afterEach(() => {
    scopes.splice(0).forEach((value) => value.stop())
    ElMessage.closeAll()
    vi.restoreAllMocks()
})
/** 创建独立组件作用域, 每条通道只拥有自己的全局状态. */
function scope(run: () => void): EffectScope {
    const value = effectScope()
    scopes.push(value)
    value.run(run)
    return value
}

it("阶段、进度和语言更新同一消息, 完成后释放栈根, 不写业务正文", async () => {
    const active = ref(false),
        progress = ref(12)
    const { t } = useI18n()
    scope(() => useOperationMessage(() => (active.value ? { message: t("app.refreshingModel"), progress: progress.value } : undefined)))
    expect(document.querySelector(".operation-messages")).toBeNull()
    active.value = true
    await flushPromises()
    const node = document.querySelector(".operation-message")!
    expect(node.getAttribute("role")).toBe("status")
    expect(node.textContent).toContain("正在刷新当前模型")
    progress.value = 77
    setLocale("en-us")
    await flushPromises()
    expect(document.querySelectorAll(".operation-message")).toHaveLength(1)
    expect(document.querySelector(".operation-message")).toBe(node)
    expect(node.textContent).toContain("Refreshing")
    expect(node.querySelector('[role="progressbar"]')?.getAttribute("aria-valuenow")).toBe("77")
    active.value = false
    await flushPromises()
    expect(document.querySelector(".operation-messages")).toBeNull()
})

it("并行操作独立关闭, 不关闭其他操作和完成消息", async () => {
    const first = scope(() => useOperationMessage(() => ({ message: "第一条" })))
    scope(() => useOperationMessage(() => ({ message: "第二条" })))
    ElMessage({ message: "已完成", duration: 0 })
    await flushPromises()
    const second = document.querySelectorAll(".operation-message")[1]!
    first.stop()
    await flushPromises()
    expect(document.querySelectorAll(".operation-message")).toHaveLength(1)
    expect(document.querySelector(".operation-message")).toBe(second)
    expect(document.querySelector(".el-message")?.textContent).toContain("已完成")
})

it("Escape 不关闭在途消息也不拦截页面键盘, 取消确认仍等待业务终态", async () => {
    const active = ref(true),
        disabled = ref(false)
    let finish!: () => void
    const cancel = vi.fn(
        () =>
            new Promise<void>((resolve) => {
                finish = resolve
            }),
    )
    const keyboard = vi.fn()
    document.addEventListener("keydown", keyboard)
    try {
        scope(() => useOperationMessage(() => (active.value ? { message: "正在处理", cancel, cancelDisabled: disabled.value } : undefined)))
        await flushPromises()
        document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }))
        expect(keyboard).toHaveBeenCalledOnce()
        const button = new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!)
        await button.trigger("click")
        await button.trigger("click")
        expect(cancel).toHaveBeenCalledOnce()
        disabled.value = true
        finish()
        await flushPromises()
        expect(button.attributes("disabled")).toBeDefined()
        expect(document.querySelector(".operation-feedback")).not.toBeNull()
        active.value = false
        await flushPromises()
        expect(document.querySelector(".operation-feedback")).toBeNull()
    } finally {
        document.removeEventListener("keydown", keyboard)
    }
})

it("卸载后取消失败不重放迟到错误或重建已关闭消息", async () => {
    let fail!: (error: Error) => void
    const value = scope(() =>
        useOperationMessage(() => ({
            message: "正在处理",
            cancel: () =>
                new Promise<void>((_, reject) => {
                    fail = reject
                }),
        })),
    )
    await flushPromises()
    await new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!).trigger("click")
    value.stop()
    fail(new Error("迟到失败"))
    await flushPromises()
    expect(document.body.textContent).not.toContain("迟到失败")
    expect(document.querySelector(".operation-messages")).toBeNull()
})
