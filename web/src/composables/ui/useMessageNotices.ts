/**
 * FilePath    : pbi-utils\web\src\composables\ui\useMessageNotices.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 将页面状态统一转换为去重浮动消息, 同一应用内切页或切语言不重放.
 */
import { getCurrentInstance, watch } from "vue"
import { locale } from "@/i18n/state"
import { hostText } from "@/i18n"
import { showMessage } from "@/ui/message"
import type { FeedbackType } from "./useFeedback"

export interface MessageNotice {
    key: string
    message: string
    type?: FeedbackType
    /** 静态说明在同一应用内只显示一次; 错误与实际业务状态不启用此选项. */
    once?: boolean
    /** 拼接或带参数的双语文案使用稳定业务标识, 避免仅翻译变化触发重放. */
    identity?: string
}
type NoticeSnapshot = { notices: Map<string, MessageNotice>; shownOnce: Set<string> }
const applications = new WeakMap<object, Map<string, NoticeSnapshot>>()
const standalone = new Map<string, NoticeSnapshot>()

/**
 * 将当前通道的有效状态交给 message.ts; getter 返回空项可清除对应去重基线.
 * 同一应用的 channel/key 只在内容或类型变化时提示, 已知翻译或 identity 相同不重放;
 * 同时挂载的不同业务目标应使用不同 channel. 作用域释放自动停止监听, 不处理迟到结果.
 * options.ready 为 false 时暂停读取与更新基线, 用于异步加载或隐藏页签, 不等同于已加载的空状态.
 */
export function useMessageNotices(
    channel: string,
    getNotices: () => readonly (MessageNotice | false | null | undefined)[],
    options: { ready?: () => boolean } = {},
): void {
    const context = getCurrentInstance()?.appContext
    let channels = context ? applications.get(context) : standalone
    if (!channels) {
        channels = new Map()
        applications.set(context!, channels)
    }
    const history = channels
    watch(
        () => ({ locale: locale.value, notices: options.ready?.() === false ? undefined : getNotices() }),
        (value) => {
            if (!value.notices) return
            const previous = history.get(channel)
            const current = new Map<string, MessageNotice>()
            const shownOnce = previous?.shownOnce ?? new Set<string>()
            const pending: MessageNotice[] = []
            for (const notice of value.notices) {
                if (!notice || !notice.message.trim()) continue
                current.set(notice.key, notice)
                if (notice.once && shownOnce.has(notice.key)) continue
                const old = previous?.notices.get(notice.key)
                const sameContent = old && (old.identity ?? hostText(old.message)) === (notice.identity ?? hostText(notice.message))
                if (!old || !sameContent || old.type !== notice.type) pending.push(notice)
                if (notice.once) shownOnce.add(notice.key)
            }
            history.set(channel, { notices: current, shownOnce })
            // ElMessage 同步挂载会排空 Vue 已排队的渲染; 先发布基线, 防止监听重入时再次弹出同一消息.
            for (const notice of pending) showMessage(notice.message, notice.type ?? "warning")
        },
        { immediate: true, flush: "post" },
    )
}
