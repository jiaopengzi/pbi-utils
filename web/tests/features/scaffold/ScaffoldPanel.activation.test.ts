/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\ScaffoldPanel.activation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证新建四个可选段关闭保留、重挂及模型会话隔离, 关闭内容不进入创建请求.
 */
import { expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import ScaffoldPanel from "@/features/scaffold/ScaffoldPanel.vue"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"

/** 只在传输边界记录实际发送内容, 其余行为仍由现有显式宿主模拟和真实组件完成. */
class RecordingTransport extends MockTransport {
    requests: { method: string; params: Record<string, unknown> }[] = []
    /** JSON 快照对应 BridgeClient 已发送的信封, 不让后续草稿变化改写证据. */
    override postMessage(value: unknown): void {
        this.requests.push(JSON.parse(JSON.stringify(value)) as { method: string; params: Record<string, unknown> })
        // 原模拟传输接收单参数信封, 不是浏览器 window.postMessage.
        // oxlint-disable-next-line unicorn/require-post-message-target-origin
        super.postMessage(value)
    }
}

// 本用例跨完整新建四段、模型及新建三次真实表单挂载, 沿用既有 ScaffoldPanel 行为套件的有限 15s 预算.
it("新建四段往返和重挂保留关闭草稿, 模型开启不串入新建, 关闭的无效段仍提交 null", async () => {
    const transport = new RecordingTransport()
    const bridge = markRaw(new BridgeClient(transport))
    const options = { props: { bridge }, global: { stubs: { CodeEditor: { props: ["modelValue"], template: "<pre>{{ modelValue }}</pre>" } } } }
    let wrapper = mount(ScaffoldPanel, options)
    const scopes = {
        measures: "#section-scaffold-measures",
        calendar: '[data-calendar-panel="calendar"]',
        periods: '[data-calendar-panel="periods"]',
        udfs: "#section-scaffold-udfs",
    }
    /** 通过真实导航进入对应业务段再操作开关, 不直接调用组件私有处理器. */
    async function toggle(section: keyof typeof scopes, enabled: boolean): Promise<void> {
        await wrapper.get(`[data-step="scaffold-${section === "periods" ? "calendar" : section}"]`).trigger("click")
        await flushPromises()
        if (section === "calendar" || section === "periods") await wrapper.get(`[role="tab"][id$="-${section}"]`).trigger("click")
        await wrapper.get(`${scopes[section]} [data-optional] input`).setValue(enabled)
        await flushPromises()
    }
    try {
        await flushPromises()
        const draft = wrapper.getComponent(ConfigurationFields).props("draft")
        expect([draft.measuresEnabled, draft.calendarEnabled, draft.periodsEnabled, draft.udfsEnabled]).toEqual([false, false, false, false])
        await toggle("measures", true)
        await wrapper.get(".scaffold-measures tbody tr input").setValue("NewDraftMeasure")
        await wrapper.get('.scaffold-measures tbody tr input[role="switch"]').setValue(false)
        await toggle("measures", false)
        await toggle("calendar", true)
        await wrapper.get('[data-scaffold-field="tableName"] input').setValue("NewDraftCalendar")
        draft.calendar.overrides = [{ date: "2026-10-01", isWorkday: false, name: "关闭仍保留" }]
        await toggle("calendar", false)
        await toggle("periods", true)
        await wrapper.get('[data-scaffold-field="periodFolder"] input').setValue("NewDraftPeriods")
        draft.periods.templates = []
        await toggle("periods", false)
        await toggle("udfs", true)
        draft.udfs = [{ source: "local", packageId: "c".repeat(32), functions: ["New.Draft"], baseline: "d".repeat(64), version: "1.0.0" }]
        await toggle("udfs", false)
        wrapper.unmount()

        const model = mount(ConfigurationPanel, { props: { bridge, project: structuredClone(previewProject), section: "measures", active: true } })
        try {
            await flushPromises()
            const modelDraft = model.getComponent(ConfigurationFields).props("draft")
            expect(modelDraft).not.toBe(draft)
            expect([modelDraft.measuresEnabled, modelDraft.calendarEnabled, modelDraft.periodsEnabled, modelDraft.udfsEnabled]).toEqual([
                true,
                true,
                true,
                true,
            ])
            expect(modelDraft.measures[0]?.name).not.toBe("NewDraftMeasure")
        } finally {
            model.unmount()
        }

        wrapper = mount(ScaffoldPanel, options)
        await flushPromises()
        expect(wrapper.getComponent(ConfigurationFields).props("draft")).toBe(draft)
        expect([draft.measuresEnabled, draft.calendarEnabled, draft.periodsEnabled, draft.udfsEnabled]).toEqual([false, false, false, false])
        const optionalSections = ["measures", "calendar", "periods", "udfs"] as const
        for (const section of optionalSections) {
            const control = wrapper.get(`${scopes[section]} [data-optional] input`).element as HTMLInputElement
            expect(control.checked).toBe(false)
        }
        // 导航与草稿共享同一表单, 必须串行操作真实开关, 不能并发点击步骤.
        await optionalSections.reduce((queue, section) => queue.then(() => toggle(section, true)), Promise.resolve())
        expect(draft.measures[0]).toMatchObject({ name: "NewDraftMeasure", enabled: false })
        expect(draft.calendar).toMatchObject({ tableName: "NewDraftCalendar", overrides: [{ isWorkday: false, name: "关闭仍保留" }] })
        expect(draft.periods).toMatchObject({ folder: "NewDraftPeriods", templates: [] })
        expect(draft.udfs[0]).toMatchObject({ functions: ["New.Draft"] })
        await optionalSections.reduce((queue, section) => queue.then(() => toggle(section, false)), Promise.resolve())
        draft.measures[0]!.name = ""
        draft.calendar.endYear = draft.calendar.startYear - 1
        draft.periods.dateTable = ""
        draft.udfs[0]!.baseline = "invalid"
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "选择目录")!
            .trigger("click")
        await flushPromises()
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "创建项目")!
            .trigger("click")
        await flushPromises()
        expect(transport.requests.filter((item) => item.method === "scaffold.create")).toHaveLength(1)
        for (const section of ["measures", "calendar", "periods", "udfs"])
            expect(
                transport.requests.filter((item) => item.method === "scaffold.updateDraft" && item.params.section === section).map((item) => item.params.value),
            ).toEqual([null])
        expect(draft.calendar.overrides[0]?.isWorkday).toBe(false)
        expect(draft.udfs[0]?.baseline).toBe("invalid")
        expect(transport.requests.filter((item) => item.method === "configuration.apply")).toHaveLength(0)
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
}, 15000)
