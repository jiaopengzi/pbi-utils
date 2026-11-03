/**
 * FilePath    : pbi-utils\web\tests\features\configuration\templateMeasureSources.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模型模板度量的真实来源草稿、分模式编辑、文件修订和显式重新读取边界.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElMessageBox, type MessageBoxData } from "element-plus"
import { BridgeError, type BridgeClient, type ConfigurationContext, type ModelConfigurationRequest } from "@/bridge/client"
import type { TemplateMeasureDefinition } from "@/features/scaffold/contracts"
import { previewProject } from "@/bridge/preview"
import { hostText, setLocale } from "@/i18n"
import * as messageUi from "@/ui/message"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import { configurationRequest, createConfigurationDraft } from "@/features/configuration/draft"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"
import { invalidateModelReads } from "@/features/model/modelReadCache"

const mounted: VueWrapper[] = []
afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount()
    setLocale("zh-cn")
    vi.restoreAllMocks()
})

/** 创建独立的完整六字段记录, 返回普通数据供测试宿主明确区分文件与模型来源. */
function measure(name: string, value: string): TemplateMeasureDefinition {
    return { name, value, dataCategory: "Uncategorized", enabled: false, description: `${name}说明`, displayFolder: `${name}目录` }
}

/** 创建完整的新协议宿主事实; 引擎 Config 值与有效文件值故意不同以捕获错误来源. */
function sourceContext(): ConfigurationContext {
    const project = structuredClone(previewProject)
    project.model.templateMeasureConfiguration = { mode: "config", measures: [measure("Engine", "引擎值")] }
    return {
        targetId: "measure-file-target",
        project,
        details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
        canWrite: true,
        canGenerateSample: false,
        canUseMeasureConfig: true,
        measureConfiguration: { mode: "config", measures: [measure("File", "文件值")] },
        measureConfigurationRevision: "a".repeat(64),
        configMeasures: [measure("File", "文件值")],
        measureConfigUnavailableReason: null,
        isCreatedTarget: false,
    }
}

/** 挂载真实模型配置页, 只替换宿主 I/O; 返回草稿、显式操作和可控异步读取, 不模拟页面业务. */
async function harness(current = sourceContext()) {
    const applied: ModelConfigurationRequest[] = []
    const methods: string[] = []
    let contextReads = 0
    let read = () => Promise.resolve(structuredClone(current))
    let apply = () => Promise.resolve(structuredClone(current))
    let sequence = 0
    const request = (method: string, params: { request?: ModelConfigurationRequest }) => {
        methods.push(method)
        if (method === "configuration.apply") applied.push(JSON.parse(JSON.stringify(params.request!)) as ModelConfigurationRequest)
        if (method === "configuration.context") contextReads++
        return {
            id: `${method}-${++sequence}`,
            promise:
                method === "scaffold.templates"
                    ? Promise.resolve(new ScaffoldMock().handle(method, {}))
                    : method === "configuration.context"
                      ? read()
                      : method === "request.cancel"
                        ? Promise.resolve({ accepted: true })
                        : apply(),
        }
    }
    const bridge = markRaw({ request } as unknown as BridgeClient)
    const wrapper = mount(ConfigurationPanel, { props: { bridge, project: current.project, section: "measures", active: true } })
    mounted.push(wrapper)
    await flushPromises()
    return {
        wrapper,
        bridge,
        current,
        applied,
        methods,
        contextReads: () => contextReads,
        draft: () => wrapper.getComponent(ConfigurationFields).props("draft"),
        apply: async () => {
            await wrapper
                .findAll("button")
                .find((item) => item.text() === "写入模板度量值" || item.text() === "Apply template measures")!
                .trigger("click")
            await flushPromises()
        },
        reload: async () => {
            await wrapper
                .findAll("button")
                .find((item) => item.text() === "重新读取模板配置" || item.text() === "Reload template configuration")!
                .trigger("click")
            await flushPromises()
        },
        refresh: async () => {
            current.project = { ...current.project, baselineId: `${current.project.baselineId}-refresh` }
            await wrapper.setProps({ project: current.project })
            await flushPromises()
        },
        setRead: (value: typeof read) => {
            read = value
        },
        setApply: (value: typeof apply) => {
            apply = value
        },
    }
}

it.each(["section", "active"] as const)("同模型与基线不变时经 %s 重新进入模板页复用内存, 显式重读才更新外部文件", async (entry) => {
    const current = sourceContext()
    current.project.projectPath = "model://reenter-template-source"
    current.project.connection = { kind: "desktop", label: "Desktop", instanceId: "reenter-instance", modelId: "reenter-model" }
    const view = await harness(current)
    const baseline = current.project.baselineId
    const reads = view.contextReads()
    await view.wrapper.setProps(entry === "section" ? { section: "sample" } : { active: false })
    current.measureConfiguration = { mode: "config", measures: [measure("File", "离页后外部值")] }
    current.configMeasures = [measure("File", "离页后外部值")]
    current.measureConfigurationRevision = "b".repeat(64)
    await view.wrapper.setProps(entry === "section" ? { section: "measures" } : { active: true })
    await flushPromises()
    expect(view.contextReads()).toBe(reads)
    expect(view.draft()).toMatchObject({ measures: [{ value: "文件值" }], measureConfigurationRevision: "a".repeat(64) })
    await view.reload()
    expect(view.draft()).toMatchObject({ measures: [{ value: "离页后外部值" }], measureConfigurationRevision: "b".repeat(64) })
    expect(view.contextReads()).toBe(reads + 1)
    expect(current.project.baselineId).toBe(baseline)
    expect(view.applied).toEqual([])
})

it("刷新模型即使同基线也重读模板来源, 继续保留本次脏草稿", async () => {
    const view = await harness()
    view.draft().measures[0]!.value = "刷新前编辑"
    const reads = view.contextReads()
    view.current.measureConfigurationRevision = "b".repeat(64)
    invalidateModelReads(view.bridge)
    await flushPromises()
    expect(view.contextReads()).toBe(reads + 1)
    expect(view.draft()).toMatchObject({ measures: [{ value: "刷新前编辑" }], measureConfigurationRevision: "a".repeat(64) })
    expect(document.body.textContent).toContain("模板配置来源已变化")
})

it.each(["section", "active"] as const)("经 %s 重新进入模板页保留脏草稿, 应用前仍检查外部文件漂移", async (entry) => {
    const view = await harness()
    view.draft().measures[0]!.value = "离页前未保存编辑"
    const reads = view.contextReads()
    await view.wrapper.setProps(entry === "section" ? { section: "sample" } : { active: false })
    view.current.measureConfiguration = { mode: "config", measures: [measure("File", "外部修改值")] }
    view.current.configMeasures = [measure("File", "外部修改值")]
    view.current.measureConfigurationRevision = "b".repeat(64)
    await view.wrapper.setProps(entry === "section" ? { section: "measures" } : { active: true })
    await flushPromises()
    expect(view.contextReads()).toBe(reads)
    await view.apply()
    expect(view.contextReads()).toBeGreaterThan(reads)
    expect(view.draft()).toMatchObject({ measures: [{ value: "离页前未保存编辑" }], measureConfigurationRevision: "a".repeat(64) })
    expect(document.body.textContent).toContain("模板配置来源已变化")
    expect(view.applied).toEqual([])
})

it("初次绑定读取有效 Config 文件六字段, 不以旧引擎参数替代或修改引擎快照", async () => {
    const view = await harness()
    expect(view.draft()).toMatchObject({
        measuresMode: "config",
        measures: [{ name: "File", value: "文件值", description: "File说明", displayFolder: "File目录" }],
    })
    expect(view.current.project.model.templateMeasureConfiguration).toMatchObject({ mode: "config", measures: [{ name: "Engine", value: "引擎值" }] })
    await view.apply()
    expect(view.applied[0]).toMatchObject({
        measuresMode: "config",
        measureConfigurationRevision: "a".repeat(64),
        measures: [{ name: "File", value: "文件值" }],
    })
})

it("TMDL 第一次切到 Config 读取有效文件, 两种来源来回切换保留各自本次编辑", async () => {
    const current = sourceContext()
    current.project.model.templateMeasureConfiguration!.mode = "tmdl"
    current.measureConfiguration = { mode: "tmdl", measures: [measure("Engine", "引擎值")] }
    const view = await harness(current)
    view.draft().measures[0]!.value = "TMDL编辑"
    await view.wrapper.get('input[value="config"]').setValue(true)
    expect(view.draft().measures).toEqual([measure("File", "文件值")])
    view.draft().measures[0]!.value = "Config编辑"
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    expect(view.draft().measures).toEqual([measure("Engine", "TMDL编辑")])
    await view.wrapper.get('input[value="config"]').setValue(true)
    expect(view.draft().measures).toEqual([measure("File", "Config编辑")])
})

it("同目标干净草稿跟随文件新值与修订, 提交使用新读取的修订", async () => {
    const view = await harness()
    view.current.configMeasures = [measure("File", "外部新值")]
    view.current.measureConfiguration = { mode: "config", measures: [measure("File", "外部新值")] }
    view.current.measureConfigurationRevision = "b".repeat(64)
    await view.refresh()
    expect(view.draft().measures).toEqual([measure("File", "外部新值")])
    await view.apply()
    expect(view.applied[0]).toMatchObject({ measureConfigurationRevision: "b".repeat(64), measures: [{ value: "外部新值" }] })
})

it("有编辑的草稿保留旧值和读取修订, 提交前发现文件漂移则阻止并提示重新读取", async () => {
    const view = await harness()
    view.draft().measures[0]!.value = "未保存编辑"
    view.current.measureConfiguration = { mode: "config", measures: [measure("File", "外部新值")] }
    view.current.configMeasures = [measure("File", "外部新值")]
    view.current.measureConfigurationRevision = "b".repeat(64)
    await view.refresh()
    expect(view.draft().measures).toEqual([measure("File", "未保存编辑")])
    expect(view.draft().measureConfigurationRevision).toBe("a".repeat(64))
    await view.apply()
    expect(view.applied).toEqual([])
    expect(document.body.textContent).toContain("重新读取模板配置")
})

it("预检后文件再漂移被宿主拒绝时, 失败回读仍保留原编辑和旧修订且不重放写入", async () => {
    const view = await harness()
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    view.draft().measures[0]!.value = "未提交TMDL编辑"
    await view.wrapper.get('input[value="config"]').setValue(true)
    view.draft().measures[0]!.value = "预检前编辑"
    view.setApply(() => {
        view.current.measureConfigurationRevision = "b".repeat(64)
        view.current.measureConfiguration = { mode: "config", measures: [measure("File", "并发文件值")] }
        view.current.configMeasures = [measure("File", "并发文件值")]
        return Promise.reject(new BridgeError("STALE_CONFIGURATION_SOURCE", "配置文件已变化"))
    })
    await view.apply()
    expect(view.applied).toHaveLength(1)
    expect(view.applied[0]).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "预检前编辑" }] })
    expect(view.contextReads()).toBe(3)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "预检前编辑" }] })
    expect(document.body.textContent).toContain("配置文件已变化")
    expect(document.body.textContent).toContain("模板配置来源已变化")
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "未提交TMDL编辑" }] })
})

it.each([false, true])("完成写入回执 noChanges=%s 以宿主返回值与修订重新绑定当前草稿", async (noChanges) => {
    const view = await harness()
    view.draft().measures[0]!.value = "已提交编辑"
    view.setApply(() => {
        view.current.measureConfigurationRevision = "b".repeat(64)
        view.current.measureConfiguration = { mode: "config", measures: [measure("File", "权威回读值")] }
        view.current.configMeasures = [measure("File", "权威回读值")]
        return Promise.resolve({ ...structuredClone(view.current), noChanges })
    })
    await view.apply()
    expect(view.applied).toHaveLength(1)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "b".repeat(64), measures: [{ value: "权威回读值" }] })
})

it.each(["tmdl", "config"] as const)("只提交 %s 后保留另一模式的独立编辑, 并以本轮成功修订继续提交", async (submittedMode) => {
    const view = await harness()
    const otherMode = submittedMode === "tmdl" ? "config" : "tmdl"
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    view.draft().measures[0]!.value = "TMDL独立编辑"
    await view.wrapper.get('input[value="config"]').setValue(true)
    view.draft().measures[0]!.value = "Config独立编辑"
    await view.wrapper.get(`input[value="${submittedMode}"]`).setValue(true)
    const revision = submittedMode === "config" ? "b".repeat(64) : "missing"
    view.setApply(() => {
        const returned = [measure("Committed", "本轮权威回读值")]
        view.current.project.model.templateMeasureConfiguration = { mode: submittedMode, measures: returned }
        view.current.measureConfiguration = { mode: submittedMode, measures: returned }
        view.current.configMeasures = submittedMode === "config" ? returned : null
        view.current.measureConfigurationRevision = revision
        return Promise.resolve(structuredClone(view.current))
    })
    await view.apply()
    expect(view.applied).toHaveLength(1)
    expect(view.draft()).toMatchObject({ measuresMode: submittedMode, measureConfigurationRevision: revision, measures: [{ value: "本轮权威回读值" }] })
    await view.wrapper.get(`input[value="${otherMode}"]`).setValue(true)
    expect(view.draft()).toMatchObject({
        measuresMode: otherMode,
        measureConfigurationRevision: revision,
        measures: [{ value: otherMode === "config" ? "Config独立编辑" : "TMDL独立编辑" }],
    })
    await view.apply()
    expect(view.applied).toHaveLength(2)
    expect(view.applied[1]).toMatchObject({
        measuresMode: otherMode,
        measureConfigurationRevision: revision,
        measures: [{ value: otherMode === "config" ? "Config独立编辑" : "TMDL独立编辑" }],
    })
})

it("NoChanges 返回不同文件修订不能证明本轮写入, 另一模式编辑仍保留原修订", async () => {
    const view = await harness()
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    view.draft().measures[0]!.value = "NoChanges前TMDL编辑"
    await view.wrapper.get('input[value="config"]').setValue(true)
    view.setApply(() => {
        view.current.measureConfigurationRevision = "b".repeat(64)
        return Promise.resolve({ ...structuredClone(view.current), noChanges: true })
    })
    await view.apply()
    expect(view.draft().measureConfigurationRevision).toBe("b".repeat(64))
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "NoChanges前TMDL编辑" }] })
    await view.apply()
    expect(view.applied).toHaveLength(1)
})

it("成功提交后普通自动刷新不把后续外部修订借给保留的另一模式编辑, 显式重读仍可替换两模式", async () => {
    const view = await harness()
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    view.draft().measures[0]!.value = "成功后保留TMDL编辑"
    await view.wrapper.get('input[value="config"]').setValue(true)
    view.draft().measures[0]!.value = "本轮Config编辑"
    view.setApply(() => {
        view.current.measureConfigurationRevision = "b".repeat(64)
        return Promise.resolve(structuredClone(view.current))
    })
    await view.apply()
    view.current.measureConfigurationRevision = "c".repeat(64)
    view.current.project.model.templateMeasureConfiguration = { mode: "config", measures: [measure("Engine", "后续引擎值")] }
    await view.refresh()
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "b".repeat(64), measures: [{ value: "成功后保留TMDL编辑" }] })
    await view.apply()
    expect(view.applied).toHaveLength(1)
    await view.reload()
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "c".repeat(64), measures: [{ value: "后续引擎值" }] })
})

it("成功提交可更新另一模式的干净草稿与修订", async () => {
    const view = await harness()
    view.draft().measures[0]!.value = "本轮Config编辑"
    view.setApply(() => {
        view.current.project.model.templateMeasureConfiguration = { mode: "config", measures: [measure("Engine", "干净来源权威值")] }
        view.current.measureConfigurationRevision = "b".repeat(64)
        return Promise.resolve(structuredClone(view.current))
    })
    await view.apply()
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "b".repeat(64), measures: [{ value: "干净来源权威值" }] })
})

const cleanupWarning = "模型与配置资产已完成, 但本轮临时槽清理未完成; 已保留清理责任, 不要重复提交。"

it.each(["zh-cn", "en-us"] as const)("%s 模板提交成功后的清理告警仅展示一次, 模式语言切换或无变更不重放历史告警", async (locale) => {
    setLocale(locale)
    const sent = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    const current = sourceContext()
    current.project.projectPath = "model://cleanup-warning"
    current.project.connection = { kind: "desktop", label: "Desktop", instanceId: "cleanup-instance", modelId: "cleanup-model" }
    current.project.warnings = ["历史静态告警"]
    const view = await harness(current)
    sent.mockClear()
    view.setApply(() => {
        view.current.project = {
            ...view.current.project,
            baselineId: "cleanup-completed",
            warnings: ["历史静态告警", cleanupWarning, cleanupWarning],
            hasPendingChanges: false,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: "" },
        }
        return Promise.resolve(structuredClone(view.current))
    })
    await view.apply()
    expect(sent.mock.calls.filter((call) => call[1] === "warning")).toEqual([[hostText(cleanupWarning), "warning"]])
    expect(sent.mock.calls.filter((call) => call[1] === "success")).toHaveLength(1)
    expect(sent.mock.calls.filter((call) => call[1] === "error")).toEqual([])
    expect(view.wrapper.find(".configuration-recovery").exists()).toBe(false)
    expect(view.applied).toHaveLength(1)
    expect(view.wrapper.emitted("updated")?.at(-1)?.[0]).toMatchObject({ hasPendingChanges: false, saveResult: { desktopSynchronized: true } })
    const count = sent.mock.calls.length
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    await view.wrapper.get('input[value="config"]').setValue(true)
    setLocale(locale === "zh-cn" ? "en-us" : "zh-cn")
    await view.refresh()
    expect(sent.mock.calls).toHaveLength(count)
    view.setApply(() => Promise.resolve({ ...structuredClone(view.current), noChanges: true }))
    await view.apply()
    expect(sent.mock.calls.filter((call) => call[1] === "warning")).toHaveLength(1)
    expect(sent.mock.calls.filter((call) => call[1] === "success")).toHaveLength(2)
    expect(view.applied).toHaveLength(2)
})

it("模板空 warnings 成功只发送一次成功反馈, 不补发警告或自动重提", async () => {
    const sent = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    const current = sourceContext()
    current.project.warnings = []
    const view = await harness(current)
    sent.mockClear()
    await view.apply()
    expect(sent.mock.calls).toEqual([["配置已应用", "success"]])
    expect(view.applied).toHaveLength(1)
})

it.each(["zh-cn", "en-us"] as const)("%s Desktop 模板恢复回读确定完成时展示清理告警, 不新增待核验或重放配置", async (locale) => {
    setLocale(locale)
    const sent = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    vi.spyOn(ElMessageBox, "confirm").mockResolvedValue("confirm" as MessageBoxData)
    const current = sourceContext()
    current.project.projectPath = "model://cleanup-recovery"
    current.project.connection = { kind: "desktop", label: "Desktop", instanceId: "recovery-instance", modelId: "recovery-model" }
    current.project.warnings = []
    current.project.hasPendingChanges = true
    current.project.saveResult = { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "", errorCode: "EXECUTION_NEEDS_VERIFICATION" }
    current.canWrite = false
    current.canUseMeasureConfig = false
    const view = await harness(current)
    sent.mockClear()
    view.setApply(() => {
        view.current.project = {
            ...view.current.project,
            warnings: [cleanupWarning, cleanupWarning],
            hasPendingChanges: false,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: "" },
        }
        view.current.canWrite = true
        return Promise.resolve(structuredClone(view.current))
    })
    await view.wrapper
        .findAll("button")
        .find((item) => item.text() === "重新读取模型" || item.text() === "Reload model")!
        .trigger("click")
    await flushPromises()
    expect(sent.mock.calls.filter((call) => call[1] === "warning")).toEqual([[hostText(cleanupWarning), "warning"]])
    expect(sent.mock.calls.filter((call) => call[1] === "success")).toHaveLength(1)
    expect(view.wrapper.find(".configuration-recovery").exists()).toBe(false)
    expect(view.wrapper.emitted("updated")?.at(-1)?.[0]).toMatchObject({ hasPendingChanges: false, saveResult: { desktopSynchronized: true } })
    expect(view.methods.filter((method) => method === "configuration.discard")).toHaveLength(1)
    expect(view.applied).toEqual([])
})

it("模板动作错误后的权威回读确定完成时补显清理告警, 不重放配置或新增恢复状态", async () => {
    const sent = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    const current = sourceContext()
    current.project.projectPath = "model://cleanup-error-readback"
    current.project.connection = { kind: "desktop", label: "Desktop", instanceId: "error-readback-instance", modelId: "error-readback-model" }
    current.project.warnings = []
    const view = await harness(current)
    sent.mockClear()
    view.setApply(() => {
        view.current.project = {
            ...view.current.project,
            warnings: [cleanupWarning],
            hasPendingChanges: false,
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: true, message: "" },
        }
        return Promise.reject(new BridgeError("RESPONSE_FAILED", "操作回执读取失败"))
    })
    await view.apply()
    expect(sent.mock.calls.filter((call) => call[1] === "warning")).toEqual([[cleanupWarning, "warning"]])
    expect(view.wrapper.find(".configuration-recovery").exists()).toBe(false)
    expect(view.applied).toHaveLength(1)
    expect(view.contextReads()).toBe(3)
})

it("未确定完成的模板回执不能因携带告警提前展示完成告警或解除原待处理状态", async () => {
    const sent = vi.spyOn(messageUi, "showMessage").mockImplementation(() => undefined)
    const view = await harness()
    sent.mockClear()
    view.setApply(() =>
        Promise.resolve({
            ...structuredClone(view.current),
            canWrite: false,
            canUseMeasureConfig: false,
            project: { ...structuredClone(view.current.project), warnings: [cleanupWarning], hasPendingChanges: true },
        }),
    )
    await view.apply()
    expect(sent.mock.calls.filter((call) => call[0] === cleanupWarning || call[1] === "success")).toEqual([])
    expect(view.wrapper.find(".configuration-recovery").exists()).toBe(true)
    expect(view.applied).toHaveLength(1)
})

it.each(["targetId", "modelId"] as const)("成功形状的回执 %s 换绑后不授予保留草稿新修订或发布其他模型", async (field) => {
    const current = sourceContext()
    current.project.connection = { kind: "desktop", label: "Desktop", instanceId: "apply-instance", modelId: "apply-model" }
    const view = await harness(current)
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    view.draft().measures[0]!.value = "原模型TMDL编辑"
    await view.wrapper.get('input[value="config"]').setValue(true)
    view.draft().measures[0]!.value = "原模型Config编辑"
    const rebound = structuredClone(current)
    rebound.measureConfigurationRevision = "b".repeat(64)
    if (field === "targetId") rebound.targetId = "unexpected-apply-target"
    else rebound.project.connection!.modelId = "unexpected-apply-model"
    view.setApply(() => Promise.resolve(rebound))
    await view.apply()
    expect(view.applied).toHaveLength(1)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "原模型Config编辑" }] })
    await view.wrapper.get('input[value="tmdl"]').setValue(true)
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "原模型TMDL编辑" }] })
    expect(view.wrapper.emitted("updated")).toEqual([[current.project]])
})

it("只读宿主保留 Config 来源展示, 两种模式与提交按钮均真实禁用且不发请求", async () => {
    const current = { ...sourceContext(), canWrite: false, canUseMeasureConfig: false, measureConfigUnavailableReason: null }
    const view = await harness(current)
    expect(view.draft()).toMatchObject({ measuresMode: "config", measures: [{ value: "文件值" }] })
    for (const mode of ["tmdl", "config"]) expect(view.wrapper.get(`input[value="${mode}"]`).attributes("disabled")).toBeDefined()
    const reads = view.contextReads()
    await view.wrapper.get('input[value="tmdl"]').trigger("change")
    await view.apply()
    expect(view.draft().measuresMode).toBe("config")
    expect(view.contextReads()).toBe(reads)
    expect(view.applied).toEqual([])
})

it.each(["zh-cn", "en-us"] as const)("%s 显式重新读取替换模板草稿和旧修订, 不触发模型写入", async (locale) => {
    setLocale(locale)
    const view = await harness()
    view.draft().measures[0]!.value = "丢弃编辑"
    view.current.measureConfiguration = { mode: "config", measures: [measure("File", "重新读取值")] }
    view.current.configMeasures = [measure("File", "重新读取值")]
    view.current.measureConfigurationRevision = "b".repeat(64)
    await view.reload()
    expect(view.draft()).toMatchObject({ measuresMode: "config", measureConfigurationRevision: "b".repeat(64), measures: [{ value: "重新读取值" }] })
    expect(view.applied).toEqual([])
})

it.each(["layout", "invalid"] as const)("Config 因 %s 不可用时选择 TMDL 并禁止 Config, 旧模型 Config 不阻止显式 TMDL 写入", async (reason) => {
    const current = sourceContext()
    current.canUseMeasureConfig = false
    current.measureConfiguration = { mode: "tmdl", measures: [measure("Engine", "可核验模型值")] }
    current.measureConfigurationRevision = null
    current.configMeasures = null
    current.measureConfigUnavailableReason = reason
    const view = await harness(current)
    expect(view.draft()).toMatchObject({ measuresMode: "tmdl", measures: [{ name: "Engine", value: "可核验模型值" }] })
    expect(view.wrapper.get('input[value="config"]').attributes("disabled")).toBeDefined()
    await view.apply()
    expect(view.applied[0]).toMatchObject({ measuresMode: "tmdl", measureConfigurationRevision: null, measures: [{ value: "可核验模型值" }] })
})

it("有效布局缺少配置文件仍可首次选择 Config 并提交 missing 修订", async () => {
    const current = sourceContext()
    current.measureConfiguration = { mode: "tmdl", measures: [measure("Engine", "首次创建值")] }
    current.measureConfigurationRevision = "missing"
    current.configMeasures = null
    const view = await harness(current)
    await view.wrapper.get('input[value="config"]').setValue(true)
    expect(view.wrapper.get('input[value="config"]').attributes("disabled")).toBeUndefined()
    await view.apply()
    expect(view.applied[0]).toMatchObject({ measuresMode: "config", measureConfigurationRevision: "missing", measures: [{ value: "首次创建值" }] })
})

it("有效文件中的空数组也是 Config 来源, 不用 TMDL 参数补回已删除的记录", async () => {
    const current = sourceContext()
    current.measureConfiguration = { mode: "tmdl", measures: [measure("Engine", "引擎值")] }
    current.configMeasures = []
    const view = await harness(current)
    await view.wrapper.get('input[value="config"]').setValue(true)
    expect(view.draft().measures).toEqual([])
    await view.apply()
    expect(view.applied[0]).toMatchObject({ measuresMode: "config", measureConfigurationRevision: "a".repeat(64), measures: [] })
})

it("模型文件修订只随开启的模板度量动作提交, 日期与关闭段不携带文件身份", () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.measuresEnabled = true
    draft.measureConfigurationRevision = "a".repeat(64)
    expect(configurationRequest(draft, "zh-cn", "measures").measureConfigurationRevision).toBe("a".repeat(64))
    expect(configurationRequest(draft, "zh-cn", "calendar").measureConfigurationRevision).toBeUndefined()
    draft.measuresEnabled = false
    expect(configurationRequest(draft, "zh-cn").measureConfigurationRevision).toBeUndefined()
})

it("重新读取失败保留原草稿和旧修订, 不触发写入", async () => {
    const view = await harness()
    view.draft().measures[0]!.value = "保留编辑"
    view.setRead(() => Promise.reject(new Error("读取失败")))
    await view.reload()
    expect(view.draft()).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "保留编辑" }] })
    expect(view.applied).toEqual([])
    expect(document.body.textContent).toContain("读取失败")
})

it("重新读取的迟到响应在卸载后不覆盖已保留草稿", async () => {
    const view = await harness()
    const draft = view.draft()
    draft.measures[0]!.value = "卸载前编辑"
    let resolve!: (value: ConfigurationContext) => void
    view.setRead(
        () =>
            new Promise((complete) => {
                resolve = complete
            }),
    )
    await view.reload()
    view.wrapper.unmount()
    mounted.splice(mounted.indexOf(view.wrapper), 1)
    const late = sourceContext()
    late.measureConfiguration = { mode: "config", measures: [measure("File", "迟到值")] }
    resolve(late)
    await flushPromises()
    expect(draft.measures).toEqual([measure("File", "卸载前编辑")])
    expect(view.applied).toEqual([])
})

it("取消重新读取后排空原请求, 迟到的新来源不替换旧草稿", async () => {
    const view = await harness()
    const draft = view.draft()
    draft.measures[0]!.value = "取消前编辑"
    let resolve!: (value: ConfigurationContext) => void
    view.setRead(
        () =>
            new Promise((complete) => {
                resolve = complete
            }),
    )
    await view.reload()
    const cancel = [...document.body.querySelectorAll("button")].find((button) => button.textContent?.trim() === "取消")
    expect(cancel).toBeDefined()
    cancel!.click()
    await flushPromises()
    const late = sourceContext()
    late.measureConfiguration = { mode: "config", measures: [measure("File", "取消后迟到值")] }
    resolve(late)
    await flushPromises()
    expect(draft).toMatchObject({ measureConfigurationRevision: "a".repeat(64), measures: [{ value: "取消前编辑" }] })
    expect(view.applied).toEqual([])
})

it("切换模型目标后旧重新读取响应不能覆盖新目标草稿", async () => {
    const view = await harness()
    let resolve!: (value: ConfigurationContext) => void
    view.setRead(
        () =>
            new Promise((complete) => {
                resolve = complete
            }),
    )
    await view.reload()
    const next = sourceContext()
    next.targetId = "another-template-target"
    next.project.projectId = "another-project"
    next.project.baselineId = "another-baseline"
    next.measureConfiguration = { mode: "config", measures: [measure("Next", "新目标值")] }
    view.setRead(() => Promise.resolve(structuredClone(next)))
    await view.wrapper.setProps({ project: next.project })
    await flushPromises()
    expect(view.draft().measures).toEqual([measure("Next", "新目标值")])
    resolve(sourceContext())
    await flushPromises()
    expect(view.draft().measures).toEqual([measure("Next", "新目标值")])
    expect(view.applied).toEqual([])
})

it("重新读取返回同项目和实例但不同 modelId 时保留原草稿", async () => {
    const current = sourceContext()
    current.project.connection = { kind: "desktop", label: "Desktop", instanceId: "same-instance", modelId: "original-model" }
    const view = await harness(current)
    view.draft().measures[0]!.value = "原目标编辑"
    const rebound = structuredClone(current)
    rebound.project.connection!.modelId = "another-model"
    rebound.measureConfiguration = { mode: "config", measures: [measure("Other", "其他模型值")] }
    view.setRead(() => Promise.resolve(rebound))
    await view.reload()
    expect(view.draft().measures).toEqual([measure("File", "原目标编辑")])
    expect(view.applied).toEqual([])
})

it.each(["targetId", "projectId", "modelId"] as const)("预检发现 %s 换绑后错误回读不能发布其他目标或替换原草稿", async (field) => {
    const current = sourceContext()
    current.project.connection = { kind: "desktop", label: "Desktop", instanceId: "original-instance", modelId: "original-model" }
    const view = await harness(current)
    const draft = view.draft()
    draft.measures[0]!.value = "原目标保留编辑"
    const rebound = structuredClone(current)
    if (field === "targetId") rebound.targetId = "unexpected-template-target"
    else if (field === "projectId") rebound.project.projectId = "unexpected-project"
    else rebound.project.connection!.modelId = "unexpected-model"
    rebound.measureConfiguration = { mode: "config", measures: [measure("Other", "其他目标值")] }
    view.setRead(() => Promise.resolve(structuredClone(rebound)))
    await view.apply()
    expect(view.applied).toEqual([])
    expect(view.draft()).toBe(draft)
    expect(view.draft().measures[0]!.value).toBe("原目标保留编辑")
    expect(view.wrapper.emitted("updated")).toBeUndefined()
    expect(view.wrapper.getComponent(ConfigurationFields).props("disabled")).toBe(true)
})
