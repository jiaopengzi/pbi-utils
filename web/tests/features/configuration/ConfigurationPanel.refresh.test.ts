/**
 * FilePath    : pbi-utils\web\tests\features\configuration\ConfigurationPanel.refresh.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证配置应用前权威刷新、草稿保留、目标隔离与取消边界.
 */
import { expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { BridgeError, type BridgeClient, type ConfigurationContext, type Methods } from "@/bridge/client"
import { previewProject } from "@/bridge/preview"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import CalendarStep from "@/features/scaffold/CalendarStep.vue"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"

type ModelConfigurationApply = Methods["configuration.apply"]["params"]

/** 构造同一 Desktop 目标的日期配置, 完整目录供真实前端校验使用. */
function desktopContext(): ConfigurationContext {
    return {
        targetId: "refresh-desktop-target",
        project: {
            ...structuredClone(previewProject),
            projectPath: "model://refresh-desktop-handle",
            connection: { kind: "desktop", label: "Power BI Desktop", instanceId: "refresh-instance" },
            hasPendingChanges: false,
        },
        details: {
            tables: [
                { name: "Dates", columns: [{ name: "Date", dataType: "DateTime" }], measures: [], isDate: true },
                { name: "Sales", columns: [], measures: ["Revenue"], isDate: false },
            ],
            defaultDateTable: "Dates",
            defaultDateColumn: "Date",
            defaultMeasureTable: "Sales",
            periods: {
                dateTable: "Dates",
                dateColumn: "Date",
                measureTable: "Sales",
                baseMeasures: ["Revenue"],
                templates: ["D3"],
                folder: "Periods",
                numberDigits: 2,
                percentDigits: 2,
            },
        },
        canWrite: true,
        canGenerateSample: false,
        isCreatedTarget: false,
    }
}

/** 挂载真实配置页, 只替换外部宿主请求; 可控制下一次权威上下文与应用回执. */
async function setup() {
    let current = desktopContext()
    let nextContext: Promise<ConfigurationContext> | undefined
    const applied: ModelConfigurationApply[] = []
    const request = vi.fn((method: string, parameters: unknown) => {
        let result: unknown = current
        if (method === "scaffold.templates") result = new ScaffoldMock().handle(method, {})
        else if (method === "calendar.preferences") result = { fields: ["Dates"], numberedColumns: true }
        else if (method === "configuration.context" && nextContext) {
            result = nextContext
            nextContext = undefined
        } else if (method === "configuration.apply") applied.push(parameters as ModelConfigurationApply)
        else if (method === "request.cancel") result = { accepted: true }
        return { id: `${method}-${request.mock.calls.length}`, promise: Promise.resolve(result) }
    })
    const wrapper = mount(ConfigurationPanel, {
        props: { bridge: { request } as unknown as BridgeClient, project: current.project, section: "calendar", active: true },
    })
    await flushPromises()
    await wrapper.get('[role="tab"][id$="-periods"]').trigger("click")
    const apply = () => wrapper.findAll("button").find((button) => button.text() === "应用时间智能")!
    const step = () => wrapper.getComponent(CalendarStep)
    return {
        wrapper,
        request,
        applied,
        apply,
        step,
        change(value: ConfigurationContext) {
            current = value
        },
        defer(value: Promise<ConfigurationContext>) {
            nextContext = value
        },
        context: () => current,
    }
}

it("Desktop 外部变化后先刷新再一次应用, 保留同目标草稿并提交新基线", async () => {
    const fixture = await setup()
    try {
        const draft = fixture.step().props("periods")
        draft.templates = ["D1"]
        draft.folder = "用户编辑的目录"
        fixture.change({ ...fixture.context(), project: { ...fixture.context().project, baselineId: "external-refresh-baseline" } })
        await fixture.apply().trigger("click")
        await flushPromises()
        expect(fixture.applied).toHaveLength(1)
        expect(fixture.applied[0]).toMatchObject({
            targetId: "refresh-desktop-target",
            baselineId: "external-refresh-baseline",
            request: { periods: { templates: ["D1"], folder: "用户编辑的目录" } },
        })
        expect(fixture.step().props("periods")).toBe(draft)
        expect(fixture.step().props("previewContext")).toEqual({ kind: "model", targetId: "refresh-desktop-target", baselineId: "external-refresh-baseline" })
        expect(fixture.wrapper.emitted("updated")?.at(-1)?.[0]).toMatchObject({ baselineId: "external-refresh-baseline" })
    } finally {
        fixture.wrapper.unmount()
    }
})

it("最新模型已删除原基础度量时按新目录校验, 保留草稿且不提交", async () => {
    const fixture = await setup()
    try {
        const latest = fixture.context()
        fixture.change({
            ...latest,
            project: { ...latest.project, baselineId: "removed-measure" },
            details: { ...latest.details, tables: latest.details.tables.map((table) => Object.assign({}, table, { measures: [] })) },
        })
        await fixture.apply().trigger("click")
        await flushPromises()
        expect(fixture.applied).toHaveLength(0)
        expect(fixture.step().props("periods").baseMeasures).toEqual(["Revenue"])
        expect(fixture.wrapper.find('[data-scaffold-invalid="true"]').exists()).toBe(true)
    } finally {
        fixture.wrapper.unmount()
    }
})

it.each(["targetId", "projectId", "projectPath", "instanceId", "modelId"] as const)("预检查 %s 换绑后不将原草稿写入其他目标", async (field) => {
    const fixture = await setup()
    try {
        const latest = structuredClone(fixture.context())
        if (field === "targetId") latest.targetId = "another-target"
        else if (field === "instanceId") latest.project.connection!.instanceId = "another-instance"
        else if (field === "modelId") latest.project.connection!.modelId = "another-model"
        else latest.project[field] = "another-project"
        fixture.change(latest)
        await fixture.apply().trigger("click")
        await flushPromises()
        expect(fixture.applied).toHaveLength(0)
    } finally {
        fixture.wrapper.unmount()
    }
})

it.each(["pending", "readonly", "failed"] as const)("预检查 %s 状态不继续应用", async (state) => {
    const fixture = await setup()
    try {
        if (state === "failed") fixture.defer(Promise.reject(new BridgeError("READ_FAILED", "读取失败")))
        else {
            const latest = fixture.context()
            fixture.change({ ...latest, canWrite: state !== "readonly", project: { ...latest.project, hasPendingChanges: state === "pending" } })
        }
        await fixture.apply().trigger("click")
        await flushPromises()
        expect(fixture.applied).toHaveLength(0)
        if (state === "failed") expect(document.body.textContent).toContain("读取失败")
    } finally {
        fixture.wrapper.unmount()
    }
})

it("预检查取消被接受后仍等待读取终态, 读取晚成功也不提交", async () => {
    const fixture = await setup()
    try {
        let finish!: (value: ConfigurationContext) => void
        fixture.defer(
            new Promise((resolve) => {
                finish = resolve
            }),
        )
        await fixture.apply().trigger("click")
        await flushPromises()
        await new DOMWrapper(document.querySelector<HTMLElement>(".operation-feedback__cancel")!).trigger("click")
        await flushPromises()
        expect(fixture.wrapper.attributes("aria-busy")).toBe("true")
        finish(fixture.context())
        await flushPromises()
        expect(fixture.applied).toHaveLength(0)
        expect(fixture.wrapper.attributes("aria-busy")).toBe("false")
    } finally {
        fixture.wrapper.unmount()
    }
})

it("预检查期间卸载撤销当前请求, 迟到读取不提交或发布更新", async () => {
    const fixture = await setup()
    let finish!: (value: ConfigurationContext) => void
    fixture.defer(
        new Promise((resolve) => {
            finish = resolve
        }),
    )
    await fixture.apply().trigger("click")
    await flushPromises()
    fixture.wrapper.unmount()
    finish(fixture.context())
    await flushPromises()
    expect(fixture.applied).toHaveLength(0)
    expect(fixture.wrapper.emitted("updated")).toBeUndefined()
    expect(fixture.request.mock.calls.some(([method]) => method === "request.cancel")).toBe(true)
})
