/**
 * FilePath    : pbi-utils\web\tests\features\configuration\ConfigurationPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用真实配置呈现器验证异步目标换绑后的偏好初始化与已编辑草稿隔离.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import CalendarStep from "@/features/scaffold/CalendarStep.vue"
import { previewProject } from "@/bridge/preview"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import type { ConfigurationContext, ProjectSnapshot } from "@/bridge/client"
import * as messageUi from "@/ui/message"
import { setLocale } from "@/i18n"
/** 构造目标独立的宿主上下文, 不捕获任何用例内状态. */
function context(value: ProjectSnapshot): ConfigurationContext {
    return {
        targetId: value.projectId,
        project: value,
        details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
        canWrite: true,
        canGenerateSample: false,
        isCreatedTarget: false,
        isSimulation: true,
    }
}
it.each([true, false])("直接进入日期配置时偏好早于真实目标=%s, 换目标初始化且保留编辑", async (early) => {
    let resolveContext!: (value: ConfigurationContext) => void, resolvePreferences!: (value: unknown) => void
    const project = { ...previewProject, projectId: "calendar-target-1", baselineId: "base-1" } as ProjectSnapshot
    const saved = { fields: ["Dates", "Status"], numberedColumns: false }
    const request = vi.fn((method: string) => ({
        id: method,
        promise:
            method === "scaffold.templates"
                ? Promise.resolve(new ScaffoldMock().handle(method, {}))
                : method === "configuration.context"
                  ? new Promise<ConfigurationContext>((resolve) => {
                        resolveContext = resolve
                    })
                  : method === "calendar.preferences"
                    ? early
                        ? Promise.resolve(saved)
                        : new Promise((resolve) => {
                              resolvePreferences = resolve
                          })
                    : Promise.resolve({}),
    }))
    const wrapper = mount(ConfigurationPanel, { props: { bridge: { request } as never, project, section: "calendar", active: true } })
    await flushPromises()
    resolveContext(context(project))
    await flushPromises()
    if (!early) {
        resolvePreferences(saved)
        await flushPromises()
    }
    const first = wrapper.findComponent(CalendarStep).props("calendar")
    expect(wrapper.findComponent(CalendarStep).props("previewContext")).toEqual({ kind: "model", targetId: project.projectId, baselineId: project.baselineId })
    expect(first.fields).toEqual(saved.fields)
    expect(first.numberedColumns).toBe(false)
    first.startYear = 2020
    first.fields = ["Dates", "Year"]
    const other = { ...project, projectId: "calendar-target-2", baselineId: "base-2" }
    await wrapper.setProps({ project: other })
    await flushPromises()
    expect(wrapper.findComponent(CalendarStep).props("previewContext")).toBeUndefined()
    resolveContext(context(other))
    await flushPromises()
    expect(wrapper.findComponent(CalendarStep).props("previewContext")).toEqual({ kind: "model", targetId: other.projectId, baselineId: other.baselineId })
    expect(wrapper.findComponent(CalendarStep).props("calendar").fields).toEqual(saved.fields)
    await wrapper.setProps({ project })
    await flushPromises()
    resolveContext(context(project))
    await flushPromises()
    expect(wrapper.findComponent(CalendarStep).props("calendar")).toBe(first)
    expect(first.fields).toEqual(["Dates", "Year"])
    expect(first.startYear).toBe(2020)
    wrapper.unmount()
})

it.each(["error", "recovery"] as const)("隐藏配置加载 %s 不弹通知, 激活后才显示当前事实", async (kind) => {
    const sent = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    const project = { ...previewProject, projectId: `hidden-${kind}`, baselineId: "hidden-base" }
    const current = context(project)
    current.project = { ...project, hasPendingChanges: kind === "recovery" }
    let errorText = "hidden configuration load failed"
    const request = vi.fn((method: string) => ({
        id: method,
        promise:
            method === "scaffold.templates"
                ? kind === "error"
                    ? Promise.reject(new Error(errorText))
                    : Promise.resolve(new ScaffoldMock().handle(method, {}))
                : Promise.resolve(current),
    }))
    const wrapper = mount(ConfigurationPanel, { props: { bridge: { request } as never, project, section: "measures", active: false } })
    try {
        await flushPromises()
        expect(sent).not.toHaveBeenCalled()
        await wrapper.setProps({ active: true })
        await flushPromises()
        expect(sent.mock.calls).toEqual([
            [
                kind === "error" ? "hidden configuration load failed" : "修改尚未提交, 未保存草稿已保留; 请处理当前错误后继续保存或放弃草稿",
                kind === "error" ? "error" : "warning",
            ],
        ])
        expect(wrapper.find(".el-alert").exists()).toBe(false)
        await wrapper.setProps({ active: false })
        setLocale("en-us")
        await flushPromises()
        expect(sent).toHaveBeenCalledTimes(1)
        await wrapper.setProps({ active: true })
        await flushPromises()
        expect(sent).toHaveBeenCalledTimes(1)
        await wrapper.setProps({ active: false })
        errorText = "changed hidden configuration failure"
        current.project = {
            ...current.project,
            baselineId: "hidden-new-baseline",
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "changed hidden recovery" },
        }
        await wrapper.setProps({ project: current.project })
        await flushPromises()
        expect(sent).toHaveBeenCalledTimes(1)
        await wrapper.setProps({ active: true })
        await flushPromises()
        expect(sent.mock.calls.at(-1)).toEqual([kind === "error" ? errorText : "changed hidden recovery", kind === "error" ? "error" : "warning"])
        expect(sent).toHaveBeenCalledTimes(2)
    } finally {
        wrapper.unmount()
        sent.mockRestore()
        setLocale("zh-cn")
    }
})

it.each(["before", "after"] as const)("旧目标读取在新目标结果 %s 到达时不覆盖当前草稿或预览身份", async (order) => {
    vi.useFakeTimers()
    const first = { ...previewProject, projectId: "late-first", baselineId: "late-first-base" }
    const second = { ...previewProject, projectId: "late-second", baselineId: "late-second-base" }
    const pending: ((value: ConfigurationContext) => void)[] = []
    const request = vi.fn((method: string) => ({
        id: `${method}-${pending.length}`,
        promise:
            method === "scaffold.templates"
                ? Promise.resolve(new ScaffoldMock().handle(method, {}))
                : method === "configuration.context"
                  ? new Promise<ConfigurationContext>((resolve) => pending.push(resolve))
                  : Promise.resolve({ fields: ["Dates"], numberedColumns: true }),
    }))
    const wrapper = mount(ConfigurationPanel, {
        props: { bridge: { request } as never, project: first, section: "calendar", active: true },
        global: { stubs: { CodeEditor: { props: ["modelValue"], template: "<pre>{{ modelValue }}</pre>" } } },
    })
    try {
        await flushPromises()
        await wrapper.setProps({ project: second })
        await flushPromises()
        expect(pending).toHaveLength(2)
        if (order === "before") {
            pending[0]!(context(first))
            await flushPromises()
            expect(wrapper.getComponent(CalendarStep).props("previewContext")).toBeUndefined()
        }
        pending[1]!(context(second))
        await flushPromises()
        const draft = wrapper.getComponent(ConfigurationFields).props("draft")
        draft.calendar.tableName = "KeepSecondDraft"
        if (order === "after") {
            pending[0]!(context(first))
            await flushPromises()
        }
        expect(wrapper.getComponent(ConfigurationFields).props("draft")).toBe(draft)
        expect(draft.calendar.tableName).toBe("KeepSecondDraft")
        expect(wrapper.getComponent(CalendarStep).props("previewContext")).toEqual({ kind: "model", targetId: second.projectId, baselineId: second.baselineId })
        expect([draft.measuresEnabled, draft.calendarEnabled, draft.periodsEnabled, draft.udfsEnabled]).toEqual([true, true, true, true])
        expect(request.mock.calls.filter(([method]) => method === "configuration.apply")).toHaveLength(0)
    } finally {
        wrapper.unmount()
        vi.useRealTimers()
    }
})
