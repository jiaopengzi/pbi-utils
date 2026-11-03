/**
 * FilePath    : pbi-utils\web\tests\features\configuration\templateMeasureMode.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证模板度量来源的共享编辑、宿主能力、目标草稿和独立提交边界.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils"
import { markRaw } from "vue"
import type { BridgeClient, ConfigurationContext, ModelConfigurationRequest } from "@/bridge/client"
import { previewProject } from "@/bridge/preview"
import { setLocale } from "@/i18n"
import ConfigurationFields from "@/features/configuration/ConfigurationFields.vue"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import MeasuresMode from "@/features/scaffold/MeasuresMode.vue"
import { configurationRequest, configurationSections, createConfigurationDraft } from "@/features/configuration/draft"
import { saveDraft } from "@/features/scaffold/draft"
import { ScaffoldMock } from "@/features/scaffold/scaffoldMock"

const mounted: VueWrapper[] = []
afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount()
    setLocale("zh-cn")
})

it("关闭步骤或在途锁定时两种来源都具有真实禁用态并拒绝事件", async () => {
    const wrapper = mount(MeasuresMode, { props: { modelValue: "tmdl", disabled: true, configAvailable: true } })
    mounted.push(wrapper)
    expect(wrapper.get('input[value="tmdl"]').attributes("disabled")).toBeDefined()
    expect(wrapper.get('input[value="config"]').attributes("disabled")).toBeDefined()
    await wrapper.get('input[value="config"]').trigger("change")
    expect(wrapper.emitted("update:modelValue")).toBeUndefined()
})

/** 创建包含单个自定义模板参数的宿主事实, 不授予文件访问或模型写授权. */
function context(canUseMeasureConfig = true): ConfigurationContext {
    return {
        targetId: "measure-mode-target",
        project: {
            ...structuredClone(previewProject),
            model: {
                ...structuredClone(previewProject.model),
                templateMeasureConfiguration: {
                    mode: "config",
                    measures: [{ name: "Custom", value: "保留值", dataCategory: "Uncategorized", enabled: false, description: "说明", displayFolder: "目录" }],
                },
            },
        },
        details: { tables: [], defaultDateTable: null, defaultDateColumn: null, defaultMeasureTable: null },
        canWrite: true,
        canGenerateSample: false,
        canUseMeasureConfig,
        measureConfiguration: {
            mode: canUseMeasureConfig ? "config" : "tmdl",
            measures: [{ name: "Custom", value: "保留值", dataCategory: "Uncategorized", enabled: false, description: "说明", displayFolder: "目录" }],
        },
        measureConfigurationRevision: canUseMeasureConfig ? "a".repeat(64) : null,
        configMeasures: canUseMeasureConfig
            ? [{ name: "Custom", value: "保留值", dataCategory: "Uncategorized", enabled: false, description: "说明", displayFolder: "目录" }]
            : null,
        measureConfigUnavailableReason: canUseMeasureConfig ? null : "layout",
        isCreatedTarget: false,
    }
}

/** 运行真实配置页, 仅替代宿主读取与提交终态; missingConfiguration 模拟宿主已核验可创建的缺失配置, 返回页面、请求记录与可变宿主事实. */
async function modelHarness(available = true, mode: "tmdl" | "config" = "config", missingConfiguration = false) {
    const current = context(available)
    current.project.model.templateMeasureConfiguration!.mode = mode
    current.measureConfiguration!.mode = available ? mode : "tmdl"
    if (missingConfiguration) {
        current.measureConfigurationRevision = "missing"
        current.configMeasures = null
    }
    const applied: ModelConfigurationRequest[] = []
    const request = vi.fn((method: string, params: { request?: ModelConfigurationRequest }) => {
        if (method === "configuration.apply") applied.push(JSON.parse(JSON.stringify(params.request!)) as ModelConfigurationRequest)
        return {
            id: method,
            promise: Promise.resolve(method === "scaffold.templates" ? new ScaffoldMock().handle(method, {}) : structuredClone(current)),
        }
    })
    const bridge = markRaw({ request } as unknown as BridgeClient)
    const wrapper = mount(ConfigurationPanel, { props: { bridge, project: current.project, section: "measures", active: true } })
    mounted.push(wrapper)
    await flushPromises()
    return { wrapper, current, applied, request }
}

it("新建默认 TMDL, 关闭度量段或提交其他段不携带 Config, 原模式草稿保留", () => {
    const draft = createConfigurationDraft("zh-cn")
    expect(draft.measuresMode).toBe("tmdl")
    draft.measuresEnabled = true
    draft.measuresMode = "config"
    expect(configurationRequest(draft, "zh-cn", "measures").measuresMode).toBe("config")
    expect(configurationRequest(draft, "zh-cn", "calendar").measuresMode).toBeUndefined()
    draft.measuresEnabled = false
    expect(configurationRequest(draft, "zh-cn").measuresMode).toBeUndefined()
    expect(draft.measuresMode).toBe("config")
})

it("新建串行保存将所选来源交给宿主草稿, 不把 Config 当成客户端偏好", async () => {
    const draft = createConfigurationDraft("zh-cn")
    draft.measuresEnabled = true
    draft.measuresMode = "config"
    const calls: { section: string; value: unknown }[] = []
    const request = vi.fn((_method, parameters) => {
        calls.push(parameters)
        return { promise: Promise.resolve({ draftId: "mode-draft", revision: calls.length }) }
    })
    await saveDraft({ request } as never, undefined, { templateId: "blank" }, configurationSections(draft, "zh-cn"))
    expect(calls.find((item) => item.section === "measuresMode")).toMatchObject({ value: "config" })
})

it.each(["zh-cn", "en-us"] as const)("新建共享字段在 %s 可切换 Config, 明示 Desktop 刷新且切换不改六字段", async (locale) => {
    setLocale(locale)
    const draft = createConfigurationDraft(locale)
    draft.measuresEnabled = true
    draft.measures = context().project.model.templateMeasureConfiguration!.measures
    const original = structuredClone(JSON.parse(JSON.stringify(draft.measures)))
    const wrapper = mount(ConfigurationFields, {
        props: {
            draft,
            bridge: markRaw({} as BridgeClient),
            step: "measures",
            visited: new Set(["measures"]),
            idPrefix: "measure-mode",
            disabled: false,
            locked: false,
            definitions: [],
        },
    })
    mounted.push(wrapper)
    await wrapper.get('input[value="config"]').setValue(true)
    expect(draft.measuresMode).toBe("config")
    expect(draft.measures).toEqual(original)
    expect(wrapper.text()).toContain(locale === "zh-cn" ? "不会自动刷新" : "does not refresh automatically")
    expect(wrapper.text()).toContain("Power BI Desktop")
    await wrapper.get('input[value="tmdl"]').setValue(true)
    expect(draft.measuresMode).toBe("tmdl")
    expect(draft.measures).toEqual(original)
})

it("模型初次绑定优先宿主 Config 和完整六字段, 同目标刷新保留编辑模式和参数", async () => {
    const { wrapper, current } = await modelHarness()
    const fields = wrapper.getComponent(ConfigurationFields)
    const draft = fields.props("draft")
    expect(draft.measuresMode).toBe("config")
    expect(draft.measures).toEqual(current.project.model.templateMeasureConfiguration!.measures)
    expect(wrapper.get('input[value="config"]').attributes("disabled")).toBeUndefined()
    draft.measuresMode = "tmdl"
    draft.measures[0]!.value = "用户编辑"
    current.project.baselineId = "updated-same-target"
    await wrapper.setProps({ project: current.project })
    await flushPromises()
    expect(fields.props("draft")).toBe(draft)
    expect(draft.measuresMode).toBe("tmdl")
    expect(draft.measures[0]!.value).toBe("用户编辑")
    expect(current.project.model.templateMeasureConfiguration!.measures[0]!.value).toBe("保留值")
})

it("宿主未提供可用本机配置路径时使用 TMDL, 保留模型参数并允许显式提交", async () => {
    const { wrapper, applied } = await modelHarness(false)
    expect(wrapper.get('input[value="config"]').attributes("disabled")).toBeDefined()
    expect(wrapper.getComponent(ConfigurationFields).props("draft")).toMatchObject({ measuresMode: "tmdl", measures: [{ name: "Custom", value: "保留值" }] })
    const apply = () => wrapper.findAll("button").find((item) => item.text() === "写入模板度量值")!
    expect(apply().attributes("disabled")).toBeUndefined()
    await apply().trigger("click")
    await flushPromises()
    expect(applied[0]).toMatchObject({ measuresMode: "tmdl", measures: [{ name: "Custom", value: "保留值" }] })
})

it("已有 TMDL 无文件能力时仍能直接写入模板度量值", async () => {
    const { wrapper, applied } = await modelHarness(false, "tmdl")
    const apply = wrapper.findAll("button").find((item) => item.text() === "写入模板度量值")!
    expect(apply.attributes("disabled")).toBeUndefined()
    await apply.trigger("click")
    await flushPromises()
    expect(applied[0]).toMatchObject({ measuresMode: "tmdl", measures: [{ name: "Custom", enabled: false }] })
})

it("宿主允许缺失配置时自由切换保留独立草稿, 切换无请求且显式应用携带 missing 修订", async () => {
    const { wrapper, applied, request } = await modelHarness(true, "tmdl", true)
    const draft = wrapper.getComponent(ConfigurationFields).props("draft")
    expect(wrapper.get('input[value="config"]').attributes("disabled")).toBeUndefined()
    draft.measures[0]!.value = "TMDL独立编辑"
    request.mockClear()
    await wrapper.get('input[value="config"]').setValue(true)
    expect(draft.measures[0]!.value).toBe("保留值")
    draft.measures[0]!.value = "Config独立编辑"
    await wrapper.get('input[value="tmdl"]').setValue(true)
    expect(draft.measures[0]!.value).toBe("TMDL独立编辑")
    await wrapper.get('input[value="config"]').setValue(true)
    expect(draft.measures[0]!.value).toBe("Config独立编辑")
    await flushPromises()
    expect(request).not.toHaveBeenCalled()
    expect(applied).toEqual([])
    await wrapper
        .findAll("button")
        .find((item) => item.text() === "写入模板度量值")!
        .trigger("click")
    await flushPromises()
    expect(applied).toEqual([
        {
            locale: "zh-cn",
            measuresMode: "config",
            measureConfigurationRevision: "missing",
            measures: [{ name: "Custom", value: "Config独立编辑", dataCategory: "Uncategorized", enabled: false, description: "说明", displayFolder: "目录" }],
            calendar: null,
            periods: null,
            sample: null,
            udfs: null,
        },
    ])
    expect(request.mock.calls.map(([method]) => method)).toEqual(["configuration.context", "configuration.apply"])
})

it("模型允许 Config 时只提交模式与完整度量段, 六字段关闭项仍保留", async () => {
    const { wrapper, applied, current } = await modelHarness()
    await wrapper
        .findAll("button")
        .find((item) => item.text() === "写入模板度量值")!
        .trigger("click")
    await flushPromises()
    expect(applied).toEqual([
        {
            locale: "zh-cn",
            measuresMode: "config",
            measureConfigurationRevision: "a".repeat(64),
            measures: current.project.model.templateMeasureConfiguration!.measures,
            calendar: null,
            periods: null,
            sample: null,
            udfs: null,
        },
    ])
})

it("提交前宿主撤销 Config 能力时本次不写入, 回退 TMDL 并保留六字段编辑", async () => {
    const { wrapper, current, applied } = await modelHarness()
    wrapper.getComponent(ConfigurationFields).props("draft").measures[0]!.value = "用户编辑"
    current.canUseMeasureConfig = false
    current.measureConfiguration = { mode: "tmdl", measures: current.project.model.templateMeasureConfiguration!.measures }
    current.measureConfigurationRevision = null
    current.configMeasures = null
    current.measureConfigUnavailableReason = "layout"
    await wrapper
        .findAll("button")
        .find((item) => item.text() === "写入模板度量值")!
        .trigger("click")
    await flushPromises()
    expect(applied).toEqual([])
    expect(wrapper.getComponent(ConfigurationFields).props("draft")).toMatchObject({ measuresMode: "tmdl", measures: [{ value: "用户编辑" }] })
})
