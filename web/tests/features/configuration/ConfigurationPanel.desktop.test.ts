/**
 * FilePath    : pbi-utils\web\tests\features\configuration\ConfigurationPanel.desktop.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证连接 Desktop 的模型名称、时间智能回填、连续集合更新及未知提交恢复回读.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { ElMessageBox, type MessageBoxData } from "element-plus"
import { previewProject } from "@/bridge/preview"
import type { BridgeClient, ConfigurationContext, ModelConfigurationRequest } from "@/bridge/client"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import CalendarStep from "@/features/scaffold/CalendarStep.vue"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"

/** 返回已核验的连接模型上下文, 不发送文件路径或伪造文件保存事实. */
function context(): ConfigurationContext {
    const periods = {
        dateTable: "Dates",
        dateColumn: "Date",
        measureTable: "Sales",
        baseMeasures: ["Revenue"],
        templates: ["D3"],
        folder: "Periods",
        numberDigits: 2,
        percentDigits: 2,
    }
    return {
        targetId: "desktop-host-target",
        project: {
            ...structuredClone(previewProject),
            projectPath: "model://desktop-handle",
            connection: { kind: "desktop", label: "Power BI Desktop" },
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
            periods,
        },
        canWrite: true,
        canGenerateSample: false,
        isCreatedTarget: false,
    }
}

it("配置页移除文件盒并回填当前期间, 连续提交缩减和空集后仍可继续编辑", async () => {
    let current = context()
    const applied: ModelConfigurationRequest[] = []
    const request = vi.fn((method: string, parameters: { request?: ModelConfigurationRequest }) => {
        if (method === "configuration.apply") {
            applied.push(JSON.parse(JSON.stringify(parameters.request!)) as ModelConfigurationRequest)
            current = {
                ...current,
                project: {
                    ...current.project,
                    baselineId: `updated-${applied.length}`,
                    saveResult: {
                        modelSaved: true,
                        fileSaved: false,
                        desktopSynchronized: true,
                        message: "当前连接模型已更新；请在 Power BI Desktop 保存报表。",
                    },
                },
            }
        }
        return {
            id: method,
            promise: Promise.resolve(
                method === "scaffold.templates"
                    ? new ScaffoldMock().handle(method, {})
                    : method === "calendar.preferences"
                      ? { fields: ["Dates"], numberedColumns: true }
                      : current,
            ),
        }
    })
    const wrapper = mount(ConfigurationPanel, {
        props: { bridge: { request } as unknown as BridgeClient, project: current.project, section: "calendar", active: true },
    })
    try {
        await flushPromises()
        expect(wrapper.find(".scaffold-path").exists()).toBe(false)
        const step = wrapper.findComponent(CalendarStep)
        await wrapper.get('[role="tab"][id$="-periods"]').trigger("click")
        expect(step.props("periodsEnabled")).toBe(true)
        expect(step.props("periods").templates).toEqual(["D3"])
        const apply = () => wrapper.findAll("button").find((button) => button.text() === "应用时间智能")!
        step.props("periods").templates = ["D1"]
        await apply().trigger("click")
        await flushPromises()
        expect(applied[0]?.periods?.templates).toEqual(["D1"])
        expect(wrapper.emitted("updated")?.at(-1)?.[0]).toMatchObject({ hasPendingChanges: false, saveResult: { modelSaved: true, fileSaved: false } })
        expect(wrapper.find(".configuration-recovery").exists()).toBe(false)
        expect(apply().attributes("disabled")).toBeUndefined()
        step.props("periods").templates = []
        await apply().trigger("click")
        await flushPromises()
        expect(applied[1]?.periods?.templates).toEqual([])
        expect(request.mock.calls.filter(([method]) => ["configuration.complete", "configuration.discard"].includes(method))).toHaveLength(0)
    } finally {
        wrapper.unmount()
    }
})

it("未知提交保持配置只读, 确认重新读取后只请求 configuration.discard 并接受权威结果", async () => {
    let current = context()
    current.project.hasPendingChanges = true
    current.project.saveResult = {
        modelSaved: false,
        fileSaved: false,
        desktopSynchronized: false,
        errorCode: "EXECUTION_NEEDS_VERIFICATION",
        message: "提交结果待核验",
    }
    current.canWrite = false
    const request = vi.fn((method: string) => {
        if (method === "configuration.discard") current = context()
        return {
            id: method,
            promise: Promise.resolve(
                method === "scaffold.templates"
                    ? new ScaffoldMock().handle(method, {})
                    : method === "calendar.preferences"
                      ? { fields: ["Dates"], numberedColumns: true }
                      : current,
            ),
        }
    })
    const confirm = vi.spyOn(ElMessageBox, "confirm").mockResolvedValue("confirm" as MessageBoxData)
    const wrapper = mount(ConfigurationPanel, {
        props: { bridge: { request } as unknown as BridgeClient, project: current.project, section: "calendar", active: true },
    })
    try {
        await flushPromises()
        expect(
            wrapper
                .findAll("button")
                .find((button) => button.text() === "应用时间智能")
                ?.attributes("disabled"),
        ).toBeDefined()
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "重新读取模型")!
            .trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "configuration.discard")).toHaveLength(1)
        expect(request.mock.calls.filter(([method]) => ["configuration.apply", "configuration.complete"].includes(method))).toHaveLength(0)
        expect(wrapper.find(".configuration-recovery").exists()).toBe(false)
    } finally {
        wrapper.unmount()
        confirm.mockRestore()
    }
})
