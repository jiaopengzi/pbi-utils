/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\RefreshTimeTemplateQuery.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证刷新时间辅助表的动态 M 原文、双语说明、只读预览、操作锁和独立分组校验.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { setLocale } from "@/i18n"
import TemplateQueries from "@/features/scaffold/TemplateQueries.vue"
import CodeEditor from "@/features/code/CodeEditor.vue"
import templateQueries from "@/features/scaffold/templateQueries.json"
import { validateScaffoldBase } from "@/features/scaffold/validation"

const originalWriteText = navigator.clipboard.writeText
afterEach(() => {
    setLocale("zh-cn")
    vi.restoreAllMocks()
    expect(navigator.clipboard.writeText).toBe(originalWriteText)
})

it.each(["zh-cn", "en-us"] as const)("RefreshTime 在 %s 中默认未勾选, 支持独立分组、只读预览与复制动态原文", async (locale) => {
    const refresh = templateQueries.find((query) => query.id === "RefreshTime")
    expect(refresh).toBeDefined()
    expect(refresh!.queryGroup).toBe("model")
    expect(refresh!.expression).toContain("type table [RefreshTime = text]")
    expect(refresh!.expression).toContain("DateTime.LocalNow()")
    expect(refresh!.expression).toContain("#(000021BB) #[date]")
    expect(refresh!.expression).toContain('"zh-CN"')
    expect(refresh!.expression).not.toContain("File.Contents")
    const writeText = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    setLocale(locale)
    const wrapper = mount(TemplateQueries, { props: { queries: templateQueries, modelValue: [], groups: {}, disabled: false } })
    try {
        const checkbox = wrapper.get('[data-template-query="RefreshTime"] input')
        const group = wrapper.get('[data-template-group="RefreshTime"] input')
        expect((checkbox.element as HTMLInputElement).checked).toBe(false)
        expect((group.element as HTMLInputElement).value).toBe("model")
        const hint = wrapper.get(".template-queries > p").text()
        expect(hint).toContain("Hour24")
        expect(hint).toContain("MQHY")
        expect(hint).toContain("RefreshTime")
        expect(hint).toContain("TypeDetails")
        expect(wrapper.text()).toContain(locale === "zh-cn" ? "本地刷新时间" : "Local refresh time")
        const label = locale === "zh-cn" ? "预览 RefreshTime 的 M 代码" : "Preview M code for RefreshTime"
        await wrapper.get(`[aria-label="${label}"]`).trigger("click")
        expect(wrapper.findAllComponents(CodeEditor)).toHaveLength(1)
        expect(wrapper.findComponent(CodeEditor).props("modelValue")).toBe(refresh!.expression)
        expect(wrapper.findComponent(CodeEditor).props("readonly")).toBe(true)
        await wrapper.get('[data-template-copy="RefreshTime"]').trigger("click")
        expect(writeText).toHaveBeenCalledExactlyOnceWith(refresh!.expression)
        await group.setValue("")
        expect(wrapper.emitted("update:groups")?.at(-1)).toEqual([{ RefreshTime: "" }])
        expect(wrapper.emitted("update:modelValue")).toBeUndefined()
        await wrapper.setProps({ disabled: true })
        await checkbox.setValue(true)
        await group.setValue("不能修改")
        await wrapper.get('[data-template-copy="RefreshTime"]').trigger("click")
        await flushPromises()
        expect(wrapper.emitted("update:modelValue")).toBeUndefined()
        expect(wrapper.emitted("update:groups")).toHaveLength(1)
        expect(writeText).toHaveBeenCalledTimes(1)
        expect(wrapper.findComponent(CodeEditor).props("modelValue")).toBe(refresh!.expression)
        expect((checkbox.element as HTMLInputElement).disabled).toBe(true)
        expect((group.element as HTMLInputElement).disabled).toBe(true)
    } finally {
        wrapper.unmount()
    }
})

it("RefreshTime 仅在勾选时校验分组, 对应行错误保留可定位字段与原始草稿", () => {
    const input = {
        template: { id: "builtin:blank", name: "blank", source: "builtin", baseline: "a".repeat(64), description: "", warnings: [] },
        projectName: "MyProject",
        output: "C:/host-selected",
        templateQueries: ["RefreshTime"],
        templateQueryGroups: { RefreshTime: "   " },
        templateQueryDefinitions: templateQueries,
    }
    const issue = validateScaffoldBase(input)
    expect(issue?.field).toBe("templateQueryGroups")
    expect(issue?.row).toBe(3)
    expect(validateScaffoldBase({ ...input, templateQueries: [] })).toBeUndefined()
    expect(validateScaffoldBase({ ...input, templateQueryGroups: { RefreshTime: "" } })).toBeUndefined()
    expect(validateScaffoldBase({ ...input, templateQueryGroups: { RefreshTime: "刷新 分组" } })).toBeUndefined()
})
