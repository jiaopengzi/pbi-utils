/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfLibraryCard.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证包卡片元数据、整包选择及禁用事件边界.
 */
import { expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { ElCheckbox } from "element-plus"
import UdfLibraryCard from "@/features/scaffold/UdfLibraryCard.vue"
import { setUiSettings } from "@/i18n"
import type { UdfCatalogPackage } from "@/features/scaffold/contracts"

const pkg: UdfCatalogPackage = {
    id: "PQL.Assert",
    version: "0.7.1",
    hash: "a".repeat(64),
    description: "Model testing utilities",
    authors: "John Kerski",
    tags: ["Testing", "Validation"],
    committedAt: null,
    iconKey: null,
    functionCount: 24,
}

it("卡片以包为单位展示元数据, 未知日期不借用缓存时间", async () => {
    const wrapper = mount(UdfLibraryCard, { props: { pkg, query: "testing", checked: false, disabled: false } })
    try {
        expect(wrapper.attributes("data-package")).toBe(pkg.id)
        for (const text of [pkg.id, pkg.description, ...pkg.tags]) expect(wrapper.text()).toContain(text)
        for (const text of [pkg.version, `作者: ${pkg.authors}`, "版本更新日期: -"]) expect(wrapper.get(".udf-package-version").text()).toContain(text)
        expect(wrapper.get("footer").text()).toContain("24 个函数")
        expect(wrapper.find("mark").text().toLowerCase()).toBe("testing")
        await wrapper.findComponent(ElCheckbox).find("input").setValue(true)
        expect(wrapper.emitted("select")).toEqual([[true]])
        await wrapper.findAll("button")[0]!.trigger("click")
        expect(wrapper.emitted("versions")).toEqual([[]])
        await wrapper.findAll("button")[1]!.trigger("click")
        expect(wrapper.emitted("details")).toEqual([[]])
    } finally {
        wrapper.unmount()
    }
})

it("禁用和预发行候选不会从迟到事件产生选择", async () => {
    const wrapper = mount(UdfLibraryCard, { props: { pkg, query: "", checked: false, disabled: true } })
    try {
        wrapper.findComponent(ElCheckbox).vm.$emit("change", true)
        await Promise.all(wrapper.findAll("button").map((button) => button.trigger("click")))
        expect(wrapper.emitted("select")).toBeUndefined()
        expect(wrapper.emitted("versions")).toBeUndefined()
        expect(wrapper.emitted("details")).toBeUndefined()
        await wrapper.setProps({ disabled: false, candidate: true })
        wrapper.findComponent(ElCheckbox).vm.$emit("change", true)
        expect(wrapper.emitted("select")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it("卡片显示Git 精确版本最后修改提交时间在本机的日期, 不展示获取时间", () => {
    const committedAt = "2026-10-20T03:28:40.731292+08:00"
    const wrapper = mount(UdfLibraryCard, { props: { pkg: { ...pkg, committedAt }, query: "", checked: false, disabled: false } })
    try {
        const expected = new Intl.DateTimeFormat("sv-SE", { year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date(committedAt))
        expect(wrapper.find(".udf-package-version").text()).toContain(expected)
    } finally {
        wrapper.unmount()
    }
})

it("英文使用Version updated标签, 未知Git时间仅显示短横线", () => {
    setUiSettings({ locale: "en-us", showRecentFiles: true, daxFormatStyle: "long" })
    const wrapper = mount(UdfLibraryCard, { props: { pkg, query: "", checked: false, disabled: false } })
    try {
        expect(wrapper.find(".udf-package-version").text()).toContain(`Authors: ${pkg.authors}`)
        expect(wrapper.find(".udf-package-version").text()).toContain("Version updated: -")
    } finally {
        wrapper.unmount()
    }
})
