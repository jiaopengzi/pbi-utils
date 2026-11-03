/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\CalendarCodePreview.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证共享日期预览的原文复制, 输入失效, 请求排空与卸载边界.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils"
import { setLocale } from "@/i18n"
import CalendarStep from "@/features/scaffold/CalendarStep.vue"
import CodeEditor from "@/features/code/CodeEditor.vue"
import { createConfigurationDraft } from "@/features/configuration/draft"

const mounted: VueWrapper[] = []
afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount()
    setLocale("zh-cn")
    vi.useRealTimers()
    vi.restoreAllMocks()
})

/** 仅用可控宿主响应驱动真实共享组件, 不在测试中重新实现预览映射. */
function harness(enabled = true) {
    vi.useFakeTimers()
    const draft = createConfigurationDraft("zh-cn")
    const pending: { params: Record<string, unknown>; resolve: (value: unknown) => void; reject: (cause: Error) => void; id: string }[] = []
    const request = vi.fn((method: string, params: Record<string, unknown>) => {
        const id = `${method}-${request.mock.calls.length}`
        return {
            id,
            promise:
                method === "calendar.preview"
                    ? new Promise((resolve, reject) => pending.push({ params, resolve, reject, id }))
                    : Promise.resolve(method === "request.cancel" ? { accepted: true } : undefined),
        }
    })
    const wrapper = mount(CalendarStep, {
        props: {
            calendar: draft.calendar,
            periods: draft.periods,
            calendarEnabled: enabled,
            periodsEnabled: false,
            disabled: false,
            definitions: [],
            measures: [],
            bridge: { request } as never,
            previewContext: { kind: "model", targetId: "target-1", baselineId: "baseline-1" },
        } as never,
        global: {
            stubs: {
                CodeEditor: {
                    props: { modelValue: String, readonly: Boolean, label: String },
                    template: '<pre class="preview-test-editor">{{ modelValue }}</pre>',
                },
            },
        },
    })
    mounted.push(wrapper)
    return { wrapper, draft, request, pending }
}

/** 返回不含前端推断的宿主原文与上下文基线. */
function result(text = "// 原文\r\n(StartYear as number) => #table({}, {})", baseline = "baseline-1") {
    return {
        functionName: "BoundCalendar",
        functionExpression: text,
        invocationExpression: "let\r\n    Source = BoundCalendar(2024)\r\nin\r\n    Source",
        inputDigest: "a".repeat(64),
        contextBaseline: baseline,
        generatorVersion: "calendar-m-v3",
    }
}

it("日期关闭仍可预览并精确复制两段宿主代码, 标明不提交且不写偏好", async () => {
    const { wrapper, request, pending } = harness(false)
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(1)
    pending[0]!.resolve(result())
    await flushPromises()
    expect(wrapper.findAllComponents(CodeEditor)).toHaveLength(2)
    expect(wrapper.findAllComponents(CodeEditor).every((editor) => editor.props("readonly") === true)).toBe(true)
    expect(wrapper.text()).toContain("未启用")
    const copy = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    await wrapper.get('[data-calendar-copy="function"]').trigger("click")
    await wrapper.get('[data-calendar-copy="invocation"]').trigger("click")
    expect(copy.mock.calls.map(([value]) => value)).toEqual([result().functionExpression, result().invocationExpression])
    expect(
        request.mock.calls.some(([method]) => method === "calendar.savePreferences" || method === "configuration.apply" || method === "scaffold.preview"),
    ).toBe(false)
})

it("输入立刻失效, 旧请求取消后排空再只发送最新参数, 迟到结果不能恢复复制", async () => {
    const { wrapper, draft, request, pending } = harness()
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(1)
    draft.calendar.startYear = 2001
    await vi.advanceTimersByTimeAsync(300)
    draft.calendar.startYear = 2002
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(1)
    expect(request.mock.calls.filter(([method]) => method === "request.cancel")).toHaveLength(1)
    expect(request.mock.calls.find(([method]) => method === "request.cancel")?.[1]).toEqual({ requestId: pending[0]!.id })
    pending[0]!.resolve(result("// stale"))
    await flushPromises()
    expect(wrapper.text()).not.toContain("// stale")
    expect(pending).toHaveLength(2)
    expect((pending[1]!.params.calendar as { startYear: number }).startYear).toBe(2002)
    pending[1]!.resolve(result("// latest"))
    await flushPromises()
    expect(wrapper.text()).toContain("// latest")
    draft.calendar.endYear = 1900
    await flushPromises()
    expect(wrapper.findAllComponents(CodeEditor)).toHaveLength(0)
    expect(wrapper.find('[data-calendar-copy="function"]').exists()).toBe(false)
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(2)
})

it("目标和语言更新使旧响应失效, 基线不符拒绝展示, 卸载只取消自身预览", async () => {
    const { wrapper, pending, request } = harness()
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(1)
    await wrapper.setProps({ previewContext: { kind: "model", targetId: "target-2", baselineId: "baseline-2" } } as never)
    setLocale("en-us")
    await vi.advanceTimersByTimeAsync(300)
    pending[0]!.resolve(result("// old target"))
    await flushPromises()
    expect(pending).toHaveLength(2)
    expect(pending[1]!.params.locale).toBe("en-us")
    pending[1]!.resolve(result("// wrong baseline"))
    await flushPromises()
    expect(wrapper.text()).not.toContain("// wrong baseline")
    expect(wrapper.find('[data-calendar-copy="function"]').exists()).toBe(false)
    await wrapper.setProps({ previewContext: undefined } as never)
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(2)
    await wrapper.setProps({ previewContext: { kind: "model", targetId: "target-3", baselineId: "baseline-3" } } as never)
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(3)
    wrapper.unmount()
    mounted.splice(mounted.indexOf(wrapper), 1)
    expect(request.mock.calls.at(-1)).toEqual(["request.cancel", { requestId: pending[2]!.id }])
    pending[2]!.resolve(result("// disposed", "baseline-3"))
    await flushPromises()
})

it("预览拒绝两段原文合计超出3MiB的宿主结果且不截断", async () => {
    const { wrapper, pending } = harness()
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(1)
    pending[0]!.resolve({ ...result("中".repeat(700000)), invocationExpression: "中".repeat(400000) })
    await flushPromises()
    expect(wrapper.findAllComponents(CodeEditor)).toHaveLength(0)
    expect(wrapper.find('[data-calendar-copy="function"]').exists()).toBe(false)
})

it("业务锁立即撤销已展示代码, 失败仅保留局部重试且不触发全局busy或写操作", async () => {
    const { wrapper, pending, request } = harness()
    await vi.advanceTimersByTimeAsync(300)
    pending[0]!.resolve(result("// current"))
    await flushPromises()
    expect(wrapper.find('[data-calendar-copy="function"]').exists()).toBe(true)
    await wrapper.setProps({ disabled: true })
    expect(wrapper.find('[data-calendar-copy="function"]').exists()).toBe(false)
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(1)
    await wrapper.setProps({ disabled: false })
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(2)
    pending[1]!.reject(new Error("需要重新读取模型基线"))
    await flushPromises()
    expect(wrapper.get(".calendar-code-preview").text()).toContain("需要重新读取模型基线")
    await wrapper.get(".calendar-code-preview button").trigger("click")
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(3)
    expect(wrapper.emitted("busy")).toBeUndefined()
    expect(request.mock.calls.every(([method]) => ["calendar.preview", "calendar.preferences", "request.cancel"].includes(method))).toBe(true)
})

it("模拟数据上下文变化立即撤销复制并仅重新只读预览日期配置", async () => {
    const { wrapper, pending, request } = harness()
    await vi.advanceTimersByTimeAsync(300)
    pending[0]!.resolve(result("// before sample change"))
    await flushPromises()
    expect(wrapper.find('[data-calendar-copy="function"]').exists()).toBe(true)
    await wrapper.setProps({ previewInvalidationKey: '{"enabled":true,"sample":{"startDate":"2024-01-01"}}' } as never)
    expect(wrapper.find('[data-calendar-copy="function"]').exists()).toBe(false)
    await vi.advanceTimersByTimeAsync(300)
    expect(pending).toHaveLength(2)
    expect(pending[1]!.params).toEqual(pending[0]!.params)
    expect(request.mock.calls.every(([method]) => ["calendar.preview", "calendar.preferences", "request.cancel"].includes(method))).toBe(true)
})
