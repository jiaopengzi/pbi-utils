/**
 * FilePath    : pbi-utils\web\tests\app\useMeasureListResize.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证度量值列表拖动、键盘、自适应宽度和资源清理边界.
 */
import { afterEach, expect, it, vi } from "vitest"
import { mount } from "@vue/test-utils"
import { defineComponent, h, nextTick, ref } from "vue"
import { useMeasureListResize } from "@/composables/app/useMeasureListResize"

afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
    document.body.removeAttribute("style")
})

/** 构造确定的布局尺寸, 不依赖 jsdom 不提供的排版引擎. */
function bounds(left: number, width: number): DOMRect {
    return { x: left, y: 0, left, top: 0, right: left + width, bottom: 20, width, height: 20, toJSON: () => ({}) }
}

/** 挂载真实生命周期和 DOM 事件, 返回可控布局、观察者与下一帧队列. */
function resizeHarness(initialWidth = 1000) {
    let layoutWidth = initialWidth
    let notify: ResizeObserverCallback | undefined
    let frameId = 0
    const frames = new Map<number, FrameRequestCallback>()
    const disconnect = vi.fn()
    const observe = vi.fn()
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
        frames.set(++frameId, callback)
        return frameId
    })
    const cancelFrame = vi.fn((id: number) => frames.delete(id))
    vi.stubGlobal("cancelAnimationFrame", cancelFrame)
    vi.stubGlobal(
        "ResizeObserver",
        class {
            /** 保存当前布局观察回调, 由测试显式交付尺寸变化. */
            constructor(callback: ResizeObserverCallback) {
                notify = callback
            }
            observe = observe
            disconnect = disconnect
        },
    )
    const disabled = ref(false)
    let api!: ReturnType<typeof useMeasureListResize>
    const wrapper = mount(
        defineComponent({
            /** 将组合函数绑定到真实 DOM, 由事件 currentTarget 提供捕获目标. */
            setup() {
                api = useMeasureListResize(() => disabled.value)
                return () =>
                    h(
                        "div",
                        {
                            ref: (element) => {
                                if (element instanceof HTMLElement) element.getBoundingClientRect = () => bounds(0, layoutWidth)
                                api.layoutRef.value = element instanceof HTMLElement ? element : undefined
                            },
                        },
                        [
                            h("div", { ref: api.contentRef }, [
                                h("span", { class: "measure-content-label" }, "Measure"),
                                h("span", { class: "measure-content-label" }, "Nested measure"),
                            ]),
                            h("div", { class: "separator", onPointerdown: api.startResize, onKeydown: api.onKeydown, onDblclick: api.autoFit }),
                        ],
                    )
            },
        }),
        { attachTo: document.body },
    )
    const separator = wrapper.get(".separator").element as HTMLElement
    const setCapture = vi.fn()
    const releaseCapture = vi.fn()
    separator.setPointerCapture = setCapture
    separator.releasePointerCapture = releaseCapture
    separator.hasPointerCapture = () => true
    return {
        api,
        wrapper,
        separator,
        disabled,
        setCapture,
        releaseCapture,
        disconnect,
        cancelFrame,
        frames,
        resize: (value: number) => {
            layoutWidth = value
            notify?.([{ contentRect: { width: value } } as ResizeObserverEntry], {} as ResizeObserver)
        },
        runFrame: () => {
            const pending = [...frames.values()]
            frames.clear()
            for (const callback of pending) callback(0)
        },
    }
}

/** 发送带指针身份的可取消事件, 支持 jsdom 没有原生 PointerEvent 的环境. */
function pointer(target: EventTarget, type: string, clientX = 400, button = 0, pointerId = 7) {
    const event = new MouseEvent(type, { clientX, button, bubbles: true, cancelable: true })
    Object.defineProperty(event, "pointerId", { value: pointerId })
    target.dispatchEvent(event)
    return event
}

/** 通过真实键盘事件执行分隔条操作并返回默认行为状态. */
function key(target: EventTarget, value: string) {
    const event = new KeyboardEvent("keydown", { key: value, bubbles: true, cancelable: true })
    target.dispatchEvent(event)
    return event
}

it("默认宽度为 360, 右键不会开始拖动或改变文本选择", () => {
    const test = resizeHarness()
    expect(test.api.width.value).toBe(360)
    expect(test.api.maxWidth.value).toBe(668)
    pointer(test.separator, "pointerdown", 400, 2)
    pointer(window, "pointermove", 800)
    expect(test.api.width.value).toBe(360)
    expect(test.api.resizing.value).toBe(false)
    expect(test.setCapture).not.toHaveBeenCalled()
    expect(document.body.style.userSelect).toBe("")
    test.wrapper.unmount()
})

it("主键拖动限制列表和编辑器最小宽度并忽略其他指针", () => {
    const test = resizeHarness()
    document.body.style.setProperty("user-select", "text", "important")
    document.body.style.cursor = "help"
    expect(pointer(test.separator, "pointerdown").defaultPrevented).toBe(true)
    expect(test.api.resizing.value).toBe(true)
    expect(test.setCapture).toHaveBeenCalledWith(7)
    expect(document.body.style.userSelect).toBe("none")
    const select = new Event("selectstart", { cancelable: true, bubbles: true })
    document.body.dispatchEvent(select)
    expect(select.defaultPrevented).toBe(true)
    pointer(window, "pointermove", 1500, 0, 8)
    expect(test.api.width.value).toBe(360)
    pointer(window, "pointermove", 1500)
    expect(test.api.width.value).toBe(668)
    pointer(window, "pointermove", -500)
    expect(test.api.width.value).toBe(260)
    pointer(window, "pointerup", 400, 0, 8)
    expect(test.api.resizing.value).toBe(true)
    pointer(window, "pointerup")
    expect(test.api.resizing.value).toBe(false)
    expect(test.releaseCapture).toHaveBeenCalledWith(7)
    expect(document.body.style.userSelect).toBe("text")
    expect(document.body.style.getPropertyPriority("user-select")).toBe("important")
    expect(document.body.style.cursor).toBe("help")
    pointer(window, "pointermove", 1000)
    expect(test.api.width.value).toBe(260)
    const restoredSelect = new Event("selectstart", { cancelable: true, bubbles: true })
    document.body.dispatchEvent(restoredSelect)
    expect(restoredSelect.defaultPrevented).toBe(false)
    test.wrapper.unmount()
})

it.each(["pointercancel", "lostpointercapture"])("%s 结束拖动并恢复页面样式", (type) => {
    const test = resizeHarness()
    pointer(test.separator, "pointerdown")
    pointer(type === "lostpointercapture" ? test.separator : window, type)
    expect(test.api.resizing.value).toBe(false)
    expect(document.body.style.userSelect).toBe("")
    pointer(window, "pointermove", 900)
    expect(test.api.width.value).toBe(360)
    test.wrapper.unmount()
})

it("方向键按 20px 调整, Home/End 到边界且 Tab 保留默认行为", () => {
    const test = resizeHarness()
    expect(key(test.separator, "ArrowRight").defaultPrevented).toBe(true)
    expect(test.api.width.value).toBe(380)
    key(test.separator, "ArrowLeft")
    expect(test.api.width.value).toBe(360)
    key(test.separator, "Home")
    key(test.separator, "ArrowLeft")
    expect(test.api.width.value).toBe(260)
    key(test.separator, "End")
    key(test.separator, "ArrowRight")
    expect(test.api.width.value).toBe(668)
    expect(key(test.separator, "Tab").defaultPrevented).toBe(false)
    test.wrapper.unmount()
})

it("自动适宽包含层级缩进、复选框和自然文字宽度且保留编辑器空间", () => {
    const test = resizeHarness()
    const content = test.api.contentRef.value!
    content.getBoundingClientRect = () => bounds(12, 336)
    content.style.paddingRight = "6px"
    content.style.borderRight = "2px solid"
    const label = content.querySelector<HTMLElement>(".measure-content-label")!
    label.getBoundingClientRect = () => bounds(62, 180)
    Object.defineProperty(label, "scrollWidth", { configurable: true, value: 410 })
    const nested = content.querySelectorAll<HTMLElement>(".measure-content-label")[1]!
    nested.getBoundingClientRect = () => bounds(90, 100)
    Object.defineProperty(nested, "scrollWidth", { configurable: true, value: 460 })
    key(test.separator, "Enter")
    expect(test.api.width.value).toBe(570)
    Object.defineProperty(label, "scrollWidth", { configurable: true, value: 1400 })
    test.separator.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }))
    expect(test.api.width.value).toBe(668)
    test.wrapper.unmount()
})

it("自动适宽包含每层祖先的右侧内边距、边框和外边距, 不保留弹性空白", () => {
    const test = resizeHarness()
    const content = test.api.contentRef.value!
    content.style.cssText = "padding: 0 12px 0 0; border-right: 2px solid;"
    content.getBoundingClientRect = () => bounds(0, test.api.width.value)
    content.innerHTML = `
        <ul style="margin: 0 4px 0 0; padding: 0 3px 0 18px; border-right: 2px solid;">
            <li style="margin: 0 2px 0 0; padding: 0 5px 0 0; border-right: 1px solid;">
                <div style="margin: 0 3px 0 0; padding: 0 8px 0 0; border-right: 1px solid;">
                    <input type="checkbox">
                    <button style="margin: 0 4px 0 0; padding: 0 15px 0 0; border: 0; border-right: 1px solid;">
                        <span style="margin-right: 3px; padding-right: 2px; border-right: 1px solid;">
                            <strong class="measure-content-label" style="margin-right: 2px;">Nested measure</strong>
                        </span>
                    </button>
                </div>
            </li>
        </ul>`
    const label = content.querySelector<HTMLElement>(".measure-content-label")!
    // 起点包含缩进和复选框, 右侧各层固定空间合计 71px; 初始列表仍有额外弹性空白.
    label.getBoundingClientRect = () => bounds(75, Math.max(0, Math.min(382, test.api.width.value - 75 - 71)))
    Object.defineProperty(label, "scrollWidth", { configurable: true, value: 382 })
    test.api.width.value = 640
    test.api.autoFit()
    expect(test.api.width.value).toBe(528)
    expect(label.getBoundingClientRect().width).toBeGreaterThanOrEqual(label.scrollWidth)
    Object.defineProperty(label, "scrollWidth", { configurable: true, value: 1400 })
    test.api.autoFit()
    expect(test.api.width.value).toBe(test.api.maxWidth.value)
    test.wrapper.unmount()
})

it("尺寸变化合并至下一帧, 只收缩越界宽度且卸载后忽略通知", () => {
    const test = resizeHarness()
    test.resize(600)
    test.resize(620)
    expect(test.frames.size).toBe(1)
    expect(test.api.width.value).toBe(360)
    test.runFrame()
    expect(test.api.width.value).toBe(288)
    test.resize(1200)
    test.runFrame()
    expect(test.api.width.value).toBe(288)
    test.resize(600)
    expect(test.frames.size).toBe(1)
    test.wrapper.unmount()
    expect(test.frames.size).toBe(0)
    expect(test.cancelFrame).toHaveBeenCalledOnce()
    expect(test.disconnect).toHaveBeenCalledOnce()
    test.resize(1500)
    expect(test.frames.size).toBe(0)
    expect(test.api.width.value).toBe(288)
})

it("在途禁用结束拖动, 拒绝新拖动和键盘操作并在卸载释放捕获", async () => {
    const test = resizeHarness()
    pointer(test.separator, "pointerdown")
    test.disabled.value = true
    await nextTick()
    expect(test.api.resizing.value).toBe(false)
    expect(document.body.style.userSelect).toBe("")
    pointer(test.separator, "pointerdown")
    key(test.separator, "End")
    expect(test.api.width.value).toBe(360)
    expect(test.api.resizing.value).toBe(false)
    test.disabled.value = false
    await nextTick()
    pointer(test.separator, "pointerdown")
    test.wrapper.unmount()
    expect(test.api.resizing.value).toBe(false)
    expect(document.body.style.userSelect).toBe("")
    expect(test.releaseCapture).toHaveBeenCalledTimes(2)
    pointer(window, "pointermove", 900)
    expect(test.api.width.value).toBe(360)
})
