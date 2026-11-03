/**
 * FilePath    : pbi-utils\web\tests\ui\MessageNotices.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证浮动状态消息的去重、语言切换、重新挂载和作用域清理.
 */
import { effectScope, h, nextTick, ref, render } from "vue"
import { afterEach, describe, expect, it, vi } from "vitest"
import { setLocale, t } from "@/i18n"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { showMessage } from "@/ui/message"

vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))
afterEach(() => {
    setLocale("zh-cn")
    vi.clearAllMocks()
})

describe("浮动状态提示", () => {
    it("消息同步挂载引发的渲染排空不会重入弹出同一提示", () => {
        const revision = ref(0)
        const scope = effectScope()
        const container = document.createElement("div")
        vi.mocked(showMessage).mockImplementationOnce(() => {
            revision.value++
            render(h("div"), container)
        })
        scope.run(() =>
            useMessageNotices("test-reentrant", () => {
                void revision.value
                return [{ key: "hint", message: "同一条提示", once: true }]
            }),
        )
        expect(showMessage).toHaveBeenCalledTimes(1)
        scope.stop()
        render(null, container)
    })
    it("静态说明在隐藏和重新显示后不重放, 独立新说明仍提示", async () => {
        const active = ref(true),
            second = ref(false)
        const scope = effectScope()
        scope.run(() =>
            useMessageNotices("test-static", () => [
                active.value && { key: "hint", message: "静态说明", type: "info", once: true },
                second.value && { key: "second", message: "另一条说明", once: true },
            ]),
        )
        active.value = false
        await nextTick()
        active.value = true
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(1)
        second.value = true
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(2)
        scope.stop()
    })
    it("同一状态不重复, 状态清除后的新事件可以再次提示", async () => {
        const message = ref("失败")
        const scope = effectScope()
        scope.run(() => useMessageNotices("test-state", () => [{ key: "error", message: message.value, type: "error" }]))
        expect(showMessage).toHaveBeenCalledExactlyOnceWith("失败", "error")
        message.value = ""
        await nextTick()
        message.value = "失败"
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(2)
        scope.stop()
        message.value = "迟到结果"
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(2)
    })

    it("切页重新挂载同一状态不重放, 实际错误改变仍提示", async () => {
        const message = ref("原错误")
        const mount = () => {
            const scope = effectScope()
            scope.run(() => useMessageNotices("test-remount", () => [{ key: "error", message: message.value }]))
            return scope
        }
        mount().stop()
        const scope = mount()
        expect(showMessage).toHaveBeenCalledTimes(1)
        message.value = "新错误"
        await nextTick()
        expect(showMessage).toHaveBeenLastCalledWith("新错误", "warning")
        expect(showMessage).toHaveBeenCalledTimes(2)
        scope.stop()
    })

    it("异步重新加载同一状态不重放, 已加载的空状态仍可清除基线", async () => {
        const ready = ref(false),
            message = ref("")
        const mount = () => {
            const scope = effectScope()
            scope.run(() => useMessageNotices("test-async-remount", () => [{ key: "warning", message: message.value }], { ready: () => ready.value }))
            return scope
        }
        let scope = mount()
        message.value = "存储警告"
        ready.value = true
        await nextTick()
        scope.stop()
        ready.value = false
        message.value = ""
        scope = mount()
        message.value = "存储警告"
        ready.value = true
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(1)
        message.value = ""
        await nextTick()
        message.value = "存储警告"
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(2)
        scope.stop()
    })

    it("语言切换只更新去重基线, 新增状态仍显示", async () => {
        const error = ref(false)
        const scope = effectScope()
        scope.run(() =>
            useMessageNotices("test-locale", () => [
                { key: "hint", message: t("scaffold.simulation"), type: "info" },
                error.value && { key: "error", message: "新错误", type: "error" },
            ]),
        )
        setLocale("en-us")
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(1)
        error.value = true
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(2)
        expect(showMessage).toHaveBeenLastCalledWith("新错误", "error")
        scope.stop()
    })

    it("同一轮切语言和收到新错误时仍提示真实的新错误", async () => {
        const message = ref("原始错误")
        const scope = effectScope()
        scope.run(() => useMessageNotices("test-locale-error", () => [{ key: "error", message: message.value, type: "error" }]))
        setLocale("en-us")
        message.value = "另一条真实错误"
        await nextTick()
        expect(showMessage).toHaveBeenCalledTimes(2)
        expect(showMessage).toHaveBeenLastCalledWith("另一条真实错误", "error")
        scope.stop()
    })
})
