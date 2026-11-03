/**
 * FilePath    : pbi-utils\web\src\composables\app\useLearningView.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 将学习页占位区域同步给隔离原生浏览器, 在滚动、弹窗及卸载时保持区域与所有权.
 */
import { onMounted, onUnmounted, ref, watch, type Ref } from "vue"
import type { BridgeClient, Methods } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback } from "@/composables/ui/useFeedback"

/** 只读检查所有 Element Plus 模态遮罩; 原生子控件必须让出 Vue 弹窗区域. */
function modalVisible(): boolean {
    return [...document.querySelectorAll<HTMLElement>(".el-overlay")].some((node) => {
        const style = getComputedStyle(node)
        return style.display !== "none" && style.visibility !== "hidden" && node.getBoundingClientRect().width > 0
    })
}

/**
 * 只同步固定学习浏览器的显示区域; 参数是可信页面占位节点和禁用状态, 不传网站地址.
 * @returns 显式模拟与失败状态; 请求失败走共享反馈, 卸载后的迟到回复不能恢复视图.
 */
export function useLearningView(bridge: BridgeClient, viewport: Ref<HTMLElement | undefined>, disabled: () => boolean) {
    const { t } = useI18n()
    const { showFeedback } = useFeedback()
    const simulation = ref(false)
    const failed = ref(false)
    let mounted = true
    let frame: number | undefined
    let observer: ResizeObserver | undefined
    let mutations: MutationObserver | undefined
    let previous = ""

    /** 将可见区域裁剪到滚动容器和真实视口, 不把隐藏或越界节点显示在原生层. */
    function bounds(): Methods["learning.update"]["params"] {
        const viewportWidth = Math.max(1, window.innerWidth)
        const viewportHeight = Math.max(1, window.innerHeight)
        const rect = viewport.value?.getBoundingClientRect()
        const parent = viewport.value?.closest(".content")?.getBoundingClientRect()
        const header = document.querySelector(".workspace-header")?.getBoundingClientRect()
        const left = Math.min(viewportWidth, Math.max(0, rect?.left ?? 0, parent?.left ?? 0))
        const top = Math.max(0, rect?.top ?? 0, parent?.top ?? 0, header?.bottom ?? 0)
        const right = Math.min(viewportWidth, rect?.right ?? 0, parent?.right ?? viewportWidth)
        const bottom = Math.min(viewportHeight, rect?.bottom ?? 0, parent?.bottom ?? viewportHeight)
        const width = Math.max(0, right - left)
        const height = Math.max(0, bottom - top)
        const visible = !disabled() && !modalVisible() && width >= 1 && height >= 1 && top < viewportHeight
        return { visible, left, top: Math.min(top, viewportHeight), width, height: visible ? height : 0, viewportWidth, viewportHeight }
    }

    /** 同一布局只发一次请求; 真实失败可见, 迟到终态与卸载不产生反馈或重新显示. */
    async function synchronize(): Promise<void> {
        frame = undefined
        if (!mounted || failed.value) return
        const value = bounds()
        const identity = JSON.stringify(value)
        if (identity === previous) return
        previous = identity
        try {
            const result = await bridge.request("learning.update", value).promise
            if (mounted) simulation.value = result.isSimulation
        } catch {
            if (!mounted || failed.value) return
            failed.value = true
            showFeedback({ message: t("learning.failed"), type: "error" })
        }
    }

    /** 合并同帧滚动、尺寸和 DOM 通知, 卸载时取消排队帧. */
    function schedule(): void {
        if (mounted && frame === undefined) frame = requestAnimationFrame(() => void synchronize())
    }

    /** 初始化失败后由用户显式重试, 不由错误反馈的 DOM 变化自动循环创建浏览器. */
    function retry(): void {
        failed.value = false
        previous = ""
        schedule()
    }

    watch(disabled, schedule)
    onMounted(() => {
        if (typeof ResizeObserver !== "undefined") {
            observer = new ResizeObserver(schedule)
            if (viewport.value) observer.observe(viewport.value)
        }
        mutations = new MutationObserver(schedule)
        mutations.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["class", "style"] })
        window.addEventListener("resize", schedule)
        document.addEventListener("scroll", schedule, true)
        schedule()
    })
    onUnmounted(() => {
        mounted = false
        if (frame !== undefined) cancelAnimationFrame(frame)
        observer?.disconnect()
        mutations?.disconnect()
        window.removeEventListener("resize", schedule)
        document.removeEventListener("scroll", schedule, true)
        // 释放原生控件不等待网络, 宿主用代次阻止初始化迟到后恢复旧页面.
        void bridge.request("learning.close", {}).promise.catch(() => undefined)
    })
    return { simulation, failed, retry }
}
