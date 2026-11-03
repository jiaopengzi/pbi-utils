/**
 * FilePath    : pbi-utils\web\tests\features\configuration\ConfigurationRecovery.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证未提交草稿与已提交部分保存的双语恢复提示, 保留权威错误与恢复动作.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { setLocale } from "@/i18n"
import { previewProject } from "@/bridge/preview"
import { BridgeError, type ConfigurationContext } from "@/bridge/client"
import ConfigurationRecovery from "@/features/configuration/ConfigurationRecovery.vue"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import { ElMessageBox, type MessageBoxData } from "element-plus"

afterEach(() => setLocale("zh-cn"))

/** 提交事实由宿主三阶段结果提供, 空结果仅表示仍持有未保存草稿. */
function context(modelSaved?: boolean, message = ""): ConfigurationContext {
    return {
        targetId: "recovery-target",
        project: {
            ...previewProject,
            hasPendingChanges: true,
            saveResult: modelSaved === undefined ? null : { modelSaved, fileSaved: false, desktopSynchronized: false, errorCode: "SAVE_FAILED", message },
        },
        details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
        canWrite: true,
        canGenerateSample: false,
        isCreatedTarget: false,
        isSimulation: true,
    }
}

it.each(["zh-cn", "en-us"] as const)("%s 未提交草稿不能误报模型已提交, 仍可继续保存或放弃", async (locale) => {
    setLocale(locale)
    const wrapper = mount(ConfigurationRecovery, { props: { context: context(), locked: false } })
    expect(document.body.textContent ?? "").toContain(locale === "zh-cn" ? "修改尚未提交" : "Changes have not been committed")
    expect(document.body.textContent ?? "").not.toContain(locale === "zh-cn" ? "模型已提交" : "The model was committed")
    const buttons = wrapper.findAll("button")
    expect(buttons).toHaveLength(2)
    expect(buttons[0]!.attributes("disabled")).toBeUndefined()
    await buttons[0]!.trigger("click")
    expect(wrapper.emitted("complete")).toHaveLength(1)
    wrapper.unmount()
})

it.each(["zh-cn", "en-us"] as const)("%s 已提交部分保存仍准确显示提交事实与继续保存入口", (locale) => {
    setLocale(locale)
    const wrapper = mount(ConfigurationRecovery, { props: { context: context(true), locked: false } })
    expect(document.body.textContent ?? "").toContain(locale === "zh-cn" ? "模型已提交" : "The model was committed")
    expect(wrapper.findAll("button")[0]!.attributes("disabled")).toBeUndefined()
    wrapper.unmount()
})

it.each(["zh-cn", "en-us"] as const)("%s 文件已保存但引擎未核实使用文件阶段提示, 保留继续完成入口", (locale) => {
    setLocale(locale)
    const value = context(false)
    value.project.saveResult!.fileSaved = true
    const wrapper = mount(ConfigurationRecovery, { props: { context: value, locked: false } })
    expect(document.body.textContent ?? "").toContain(locale === "zh-cn" ? "文件已保存" : "The file is saved")
    expect(document.body.textContent ?? "").not.toContain(locale === "zh-cn" ? "修改尚未提交" : "Changes have not been committed")
    expect(document.body.textContent ?? "").not.toContain(locale === "zh-cn" ? "模型已提交" : "The model was committed")
    expect(wrapper.findAll("button")[0]!.attributes("disabled")).toBeUndefined()
    wrapper.unmount()
})

it.each((["zh-cn", "en-us"] as const).flatMap((locale) => [false, true].map((modelSaved) => ({ locale, modelSaved }))))(
    "$locale 提交结果待核验优先于 modelSaved=$modelSaved, 不允许重试或放弃",
    ({ locale, modelSaved }) => {
        setLocale(locale)
        const value = context(modelSaved)
        value.project.saveResult!.errorCode = "EXECUTION_NEEDS_VERIFICATION"
        const wrapper = mount(ConfigurationRecovery, { props: { context: value, locked: false } })
        expect(document.body.textContent ?? "").toContain(locale === "zh-cn" ? "提交结果待核验" : "commit result requires verification")
        expect(document.body.textContent ?? "").not.toContain(locale === "zh-cn" ? "修改尚未提交" : "Changes have not been committed")
        expect(wrapper.findAll("button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
        wrapper.unmount()
    },
)

it("无草稿且保存完成时不显示恢复提示或动作", () => {
    const value = context()
    value.project.hasPendingChanges = false
    const wrapper = mount(ConfigurationRecovery, { props: { context: value, locked: false } })
    expect(wrapper.find(".configuration-recovery").exists()).toBe(false)
    expect(wrapper.findAll("button")).toHaveLength(0)
    wrapper.unmount()
})

it.each([false, true])("权威保存错误优先于兜底提示, 不因 modelSaved=%s 隐藏错误", (modelSaved) => {
    const wrapper = mount(ConfigurationRecovery, { props: { context: context(modelSaved, "authoritative save context"), locked: false } })
    expect(document.body.textContent ?? "").toContain("authoritative save context")
    wrapper.unmount()
})

it.each(["zh-cn", "en-us"] as const)("%s Desktop 提交未知只提供重新读取模型, 不提供继续保存或放弃", async (locale) => {
    setLocale(locale)
    const value = context(false)
    value.project.projectPath = "model://desktop-unknown"
    value.project.connection = { kind: "desktop", label: "Power BI Desktop" }
    value.project.saveResult!.errorCode = "EXECUTION_NEEDS_VERIFICATION"
    const confirm = vi.spyOn(ElMessageBox, "confirm").mockResolvedValue("confirm" as MessageBoxData)
    const wrapper = mount(ConfigurationRecovery, { props: { context: value, locked: false } })
    try {
        const buttons = wrapper.findAll("button")
        expect(buttons).toHaveLength(1)
        expect(buttons[0]!.text()).toBe(locale === "zh-cn" ? "重新读取模型" : "Reload model")
        expect(buttons[0]!.attributes("disabled")).toBeUndefined()
        await buttons[0]!.trigger("click")
        await flushPromises()
        expect(confirm.mock.calls[0]?.[0]).toContain(locale === "zh-cn" ? "不会撤销已发生的修改" : "does not undo committed changes")
        expect(wrapper.emitted("discard")).toHaveLength(1)
        expect(wrapper.emitted("complete")).toBeUndefined()
    } finally {
        wrapper.unmount()
        confirm.mockRestore()
    }
})

it("提交前挂起查询拒绝保留原错与草稿, 继续保存调用完成接口且不重新应用配置", async () => {
    const initial = context()
    initial.project.hasPendingChanges = false
    let current = initial
    const message = "Desktop 存在未应用的 Power Query 更改, 请先核对并处理后重试; 未自动应用或丢弃查询"
    const request = vi.fn((method: string) => {
        let promise: Promise<unknown>
        if (method === "configuration.apply") {
            current = context()
            promise = Promise.reject(new BridgeError("DESKTOP_QUERY_CHANGES_PENDING", message))
        } else if (method === "configuration.complete") {
            current = { ...current, project: { ...current.project, hasPendingChanges: false } }
            promise = Promise.resolve(current)
        } else promise = Promise.resolve(method === "scaffold.templates" ? new ScaffoldMock().handle(method, {}) : current)
        return { id: method, promise }
    })
    const wrapper = mount(ConfigurationPanel, { props: { bridge: { request } as never, project: initial.project, section: "measures", active: true } })
    try {
        await flushPromises()
        expect(wrapper.find('section[id$="-measures"] [data-optional]').exists()).toBe(false)
        const apply = wrapper.findAll("button").find((button) => button.text() === "写入模板度量值")!
        await apply.trigger("click")
        await flushPromises()
        expect(document.body.textContent ?? "").toContain(message)
        expect(document.body.textContent ?? "").toContain("修改尚未提交")
        expect(document.body.textContent ?? "").not.toContain("模型已提交")
        expect(apply.attributes("disabled")).toBeDefined()
        expect(wrapper.emitted("updated")?.at(-1)?.[0]).toMatchObject({ hasPendingChanges: true, saveResult: null })
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "继续保存")!
            .trigger("click")
        await flushPromises()
        expect(request.mock.calls.filter(([method]) => method === "configuration.complete")).toHaveLength(1)
        expect(request.mock.calls.filter(([method]) => method === "configuration.apply")).toHaveLength(1)
        expect(wrapper.find(".configuration-recovery").exists()).toBe(false)
    } finally {
        wrapper.unmount()
    }
})
