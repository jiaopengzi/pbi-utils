/**
 * FilePath    : pbi-utils\web\src\composables\ui\useFeedback.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 统一显式终态浮动反馈及持续操作消息, 防止页面恢复或卸载后重放提示.
 */
import { computed, getCurrentScope, h, onScopeDispose, render, shallowRef, watch, type ComputedRef } from "vue"
import { showMessage } from "@/ui/message"
import OperationMessages from "@/ui/OperationMessages.vue"

export type FeedbackType = "success" | "error" | "warning" | "info"
export type FeedbackRequest = { message: string; type?: FeedbackType }
export interface OperationMessageState {
    message: string
    progress?: number
    cancel?: () => void | Promise<void>
    cancelLabel?: string
    cancelDisabled?: boolean
}
export interface OperationMessageEntry {
    id: number
    source: ComputedRef<OperationMessageState | undefined>
}
const operationEntries = shallowRef<OperationMessageEntry[]>([])
let operationContainer: HTMLElement | undefined
let operationId = 0

/** 首个操作挂载全局消息栈, 所有通道排空后释放根节点; 不监听或消费页面键盘. */
function syncOperationHost(): void {
    if (!operationEntries.value.length) {
        if (operationContainer) {
            render(null, operationContainer)
            operationContainer.remove()
            operationContainer = undefined
        }
    } else if (!operationContainer) {
        operationContainer = document.createElement("div")
        document.body.append(operationContainer)
        render(h(OperationMessages, { entries: operationEntries }), operationContainer)
    }
}

/**
 * 把一个操作通道的在途状态映射到同一条持续浮动消息, getter 返回 undefined 才结束.
 * 阶段、进度、取消状态及语言变化原位更新, 不重复弹出; 作用域释放只关闭自己的消息.
 */
export function useOperationMessage(getState: () => OperationMessageState | undefined): void {
    const state = computed(getState)
    let entry: OperationMessageEntry | undefined
    /** 关闭当前通道, 保留其他并行操作及一次性结果消息. */
    function close(): void {
        if (!entry) return
        operationEntries.value = operationEntries.value.filter((item) => item.id !== entry!.id)
        entry = undefined
        syncOperationHost()
    }
    const stop = watch(
        state,
        (value) => {
            if (!value?.message.trim()) close()
            else if (!entry) {
                entry = { id: ++operationId, source: state }
                operationEntries.value = [...operationEntries.value, entry]
                syncOperationHost()
            }
        },
        { immediate: true },
    )
    if (getCurrentScope())
        onScopeDispose(() => {
            stop()
            close()
        })
}

/**
 * 为一个操作通道提供显式浮动反馈, 成功、错误、警告和取消均交给 message.ts.
 * 不保存展示状态或监听业务快照, 因此挂载、切页和语言切换不会重放消息; 作用域释放后忽略迟到结果.
 * 待核验与恢复入口由原业务状态持有, 持续进度和取消仍使用 useOperationMessage.
 * @returns 只在显式调用时发送消息的通道, 不暴露页内告警状态.
 */
export function useFeedback() {
    let disposed = false
    if (getCurrentScope())
        onScopeDispose(() => {
            disposed = true
        })

    /**
     * 将非空终态交给共享浮动提示; 不保存状态, 空输入或作用域释放后的迟到结果不产生通知.
     * @param feedback 显式终态文本与可选消息级别, 未指定级别时使用成功态.
     * @returns 不返回持久展示状态, 不改变业务恢复或取消边界.
     */
    function showFeedback(feedback: FeedbackRequest): void {
        if (disposed) return
        if (!feedback.message.trim()) return
        showMessage(feedback.message, feedback.type ?? "success")
    }

    return { showFeedback }
}
