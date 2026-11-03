/**
 * FilePath    : pbi-utils\web\tests\ui\feedback.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证全部显式终态统一交给浮动消息, 空输入及卸载迟到结果不触发通知.
 */
import { beforeEach, expect, it, vi } from "vitest"
import { effectScope, nextTick } from "vue"
import { useFeedback } from "@/composables/ui/useFeedback"
import { showMessage } from "@/ui/message"

vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))
beforeEach(() => {
    vi.mocked(showMessage).mockClear()
})

it("反馈通道只提供显式消息, 不再暴露页内状态或清理状态能力", () => {
    const feedback = useFeedback()
    expect(feedback).not.toHaveProperty("status")
    expect(feedback).not.toHaveProperty("clearFeedback")
    expect(showMessage).not.toHaveBeenCalled()
    feedback.showFeedback({ message: "操作已完成" })
    expect(showMessage).toHaveBeenCalledExactlyOnceWith("操作已完成", "success")
})

it("创建和重新创建通道不发送消息, 释放后迟到结果不会触发提示", async () => {
    const scope = effectScope()
    const feedback = scope.run(() => useFeedback())!
    await nextTick()
    expect(showMessage).not.toHaveBeenCalled()
    scope.stop()
    feedback.showFeedback({ message: "迟到成功" })
    expect(showMessage).not.toHaveBeenCalled()
    const newScope = effectScope()
    const newFeedback = newScope.run(() => useFeedback())!
    expect(showMessage).not.toHaveBeenCalled()
    newFeedback.showFeedback({ message: "新操作完成" })
    expect(showMessage).toHaveBeenCalledExactlyOnceWith("新操作完成", "success")
    newScope.stop()
})

it.each(["error", "warning", "info", "success"] as const)("显式%s终态统一转交message.ts而非留页, 空消息不产生通知", (type) => {
    const feedback = useFeedback()
    feedback.showFeedback({ message: "请先核验模型提交结果", type })
    expect(showMessage).toHaveBeenCalledExactlyOnceWith("请先核验模型提交结果", type)
    feedback.showFeedback({ message: "  " })
    expect(showMessage).toHaveBeenCalledTimes(1)
})
