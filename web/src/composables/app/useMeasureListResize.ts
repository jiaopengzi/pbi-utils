/**
 * FilePath    : pbi-utils\web\src\composables\app\useMeasureListResize.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 管理度量值列表分隔条的有界拖动、键盘适宽和尺寸资源生命周期.
 */
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue"

/** 将像素样式转换为有限数值, 自动值和空值视为无附加空间. */
function pixels(value: string) {
    return Number.parseFloat(value) || 0
}

/** 拖动期间阻止新文本选区, 不删除用户已有选区. */
function preventSelection(event: Event) {
    event.preventDefault()
}

/** 累计祖先至内容容器的固定右侧空间, 缓存样式且不把弹性剩余宽度算作内容. */
function ancestorRightSpace(element: HTMLElement | null, content: HTMLElement, insets: Map<HTMLElement, number>) {
    let space = 0
    for (let ancestor = element; ancestor; ancestor = ancestor.parentElement) {
        let inset = insets.get(ancestor)
        if (inset === undefined) {
            const style = getComputedStyle(ancestor)
            inset = pixels(style.paddingRight) + pixels(style.borderRightWidth) + (ancestor === content ? 0 : pixels(style.marginRight))
            insets.set(ancestor, inset)
        }
        space += inset
        if (ancestor === content) break
    }
    return space
}

/**
 * 为列表与编辑器提供分隔条状态和事件, disabled 返回 true 时终止并拒绝调整.
 * 调用方将 layoutRef/contentRef 绑定到整体布局和层级内容, 将 width 用作列表列宽.
 * 自动适宽读取 .measure-content-label 的自然宽度, 不更改选择或业务草稿; 不发起外部请求.
 */
export function useMeasureListResize(disabled: () => boolean = () => false) {
    const layoutRef = shallowRef<HTMLElement>()
    const contentRef = shallowRef<HTMLElement>()
    const width = ref(360)
    const resizing = ref(false)
    const minWidth = 260
    const layoutWidth = ref(0)
    const maxWidth = computed(() => (layoutWidth.value > 0 ? Math.max(minWidth, layoutWidth.value - 320 - 12) : 360))
    let observer: ResizeObserver | undefined
    let frame: number | undefined
    let disposed = false
    let drag:
        | {
              pointerId: number
              target: HTMLElement
              startX: number
              startWidth: number
              body: HTMLElement
              selection: string
              selectionPriority: string
              cursor: string
              cursorPriority: string
          }
        | undefined

    /** 将请求宽度限制到当前列表和编辑器可用边界, 返回实际宽度. */
    function clamp(value: number) {
        return Math.max(minWidth, Math.min(maxWidth.value, value))
    }

    /** 读取布局内容盒并收缩越界宽度, 隐藏布局不覆盖上次可用尺寸. */
    function measureLayout() {
        const layout = layoutRef.value
        if (!layout) return
        const style = getComputedStyle(layout)
        const available =
            layout.getBoundingClientRect().width -
            pixels(style.paddingLeft) -
            pixels(style.paddingRight) -
            pixels(style.borderLeftWidth) -
            pixels(style.borderRightWidth)
        if (available <= 0) return
        layoutWidth.value = available
        width.value = clamp(width.value)
    }

    /** 同帧观察通知只排队一次, 避免在尺寸交付阶段回写布局. */
    function scheduleMeasure() {
        if (disposed || frame !== undefined) return
        frame = requestAnimationFrame(() => {
            frame = undefined
            if (!disposed) measureLayout()
        })
    }

    /** 结束本轮拖动并恢复原样式、监听器和捕获, 可重复调用. */
    function stopResize() {
        const current = drag
        if (!current) return
        drag = undefined
        resizing.value = false
        window.removeEventListener("pointermove", moveResize, true)
        window.removeEventListener("pointerup", endResize, true)
        window.removeEventListener("pointercancel", endResize, true)
        window.removeEventListener("blur", stopResize)
        window.removeEventListener("selectstart", preventSelection, true)
        current.target.removeEventListener("lostpointercapture", endResize)
        current.body.style.setProperty("user-select", current.selection, current.selectionPriority)
        current.body.style.setProperty("cursor", current.cursor, current.cursorPriority)
        if (current.target.hasPointerCapture?.(current.pointerId)) current.target.releasePointerCapture(current.pointerId)
    }

    /** 仅接受当前指针的结束事件, 其他指针不能终止本轮拖动. */
    function endResize(event: PointerEvent) {
        if (event.pointerId === drag?.pointerId) stopResize()
    }

    /** 以起点位移调整宽度, 禁用和越界均不触及业务状态. */
    function moveResize(event: PointerEvent) {
        if (!drag || event.pointerId !== drag.pointerId) return
        if (disabled()) {
            stopResize()
            return
        }
        event.preventDefault()
        width.value = clamp(drag.startWidth + event.clientX - drag.startX)
    }

    /** 左键开始有界拖动, 失去有效指针时由窗口监听保持清理能力. */
    function startResize(event: PointerEvent) {
        if (event.button !== 0 || disabled() || drag || !layoutRef.value || !(event.currentTarget instanceof HTMLElement)) return
        const target = event.currentTarget
        try {
            target.setPointerCapture?.(event.pointerId)
        } catch (error) {
            // 指针已释放时仍可依赖窗口结束事件, 其他捕获错误保留原始失败.
            if (!(error instanceof DOMException && error.name === "NotFoundError")) throw error
        }
        event.preventDefault()
        measureLayout()
        const body = document.body
        drag = {
            pointerId: event.pointerId,
            target,
            startX: event.clientX,
            startWidth: width.value,
            body,
            selection: body.style.getPropertyValue("user-select"),
            selectionPriority: body.style.getPropertyPriority("user-select"),
            cursor: body.style.getPropertyValue("cursor"),
            cursorPriority: body.style.getPropertyPriority("cursor"),
        }
        resizing.value = true
        body.style.setProperty("user-select", "none", "important")
        body.style.setProperty("cursor", "col-resize", "important")
        window.addEventListener("pointermove", moveResize, true)
        window.addEventListener("pointerup", endResize, true)
        window.addEventListener("pointercancel", endResize, true)
        window.addEventListener("blur", stopResize)
        window.addEventListener("selectstart", preventSelection, true)
        target.addEventListener("lostpointercapture", endResize)
    }

    /** 按已渲染标签适宽, 包含复选框、层级缩进和所有祖先的右侧固定留白. */
    function autoFit() {
        const content = contentRef.value
        if (disabled() || !content) return
        measureLayout()
        const contentBounds = content.getBoundingClientRect()
        const outerSpace = Math.max(0, width.value - contentBounds.width)
        const insets = new Map<HTMLElement, number>()
        let naturalWidth = minWidth
        for (const label of content.querySelectorAll<HTMLElement>(".measure-content-label")) {
            const labelBounds = label.getBoundingClientRect()
            const labelStyle = getComputedStyle(label)
            const labelBorder = pixels(labelStyle.borderLeftWidth) + pixels(labelStyle.borderRightWidth)
            const rightSpace = pixels(labelStyle.marginRight) + ancestorRightSpace(label.parentElement, content, insets)
            naturalWidth = Math.max(
                naturalWidth,
                labelBounds.left -
                    contentBounds.left +
                    content.scrollLeft +
                    Math.max(label.scrollWidth + labelBorder, labelBounds.width) +
                    rightSpace +
                    outerSpace,
            )
        }
        width.value = clamp(Math.ceil(naturalWidth))
    }

    /** 处理分隔条方向/Home/End/Enter 键, 保留 Tab 和系统修饰键行为. */
    function onKeydown(event: KeyboardEvent) {
        if (disabled() || event.altKey || event.ctrlKey || event.metaKey) return
        if (!["ArrowLeft", "ArrowRight", "Home", "End", "Enter"].includes(event.key)) return
        event.preventDefault()
        event.stopPropagation()
        measureLayout()
        if (event.key === "Enter") autoFit()
        else if (event.key === "Home") width.value = minWidth
        else if (event.key === "End") width.value = maxWidth.value
        else width.value = clamp(width.value + (event.key === "ArrowRight" ? 20 : -20))
    }

    watch(
        layoutRef,
        (layout) => {
            observer?.disconnect()
            observer = undefined
            if (!layout || disposed) return
            measureLayout()
            if (typeof ResizeObserver !== "undefined") {
                observer = new ResizeObserver(scheduleMeasure)
                observer.observe(layout)
            }
        },
        { flush: "post" },
    )
    watch(disabled, (value) => {
        if (value) stopResize()
    })
    window.addEventListener("resize", scheduleMeasure)
    onBeforeUnmount(() => {
        disposed = true
        stopResize()
        observer?.disconnect()
        window.removeEventListener("resize", scheduleMeasure)
        if (frame !== undefined) cancelAnimationFrame(frame)
    })

    return { layoutRef, contentRef, width, resizing, minWidth, maxWidth, startResize, autoFit, onKeydown }
}
