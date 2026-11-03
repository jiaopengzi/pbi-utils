/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\dataCategories.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 穷举文本模板度量值分类的表单接受与双语标签, 拒绝已移除的应用扩展.
 */
import { afterEach, expect, it } from "vitest"
import { readFileSync } from "node:fs"
import { dirname, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { mount } from "@vue/test-utils"
import { ElOption } from "element-plus"
import { setLocale } from "@/i18n"
import { measureError } from "@/features/scaffold/draft"
import MeasuresEditor from "@/features/scaffold/MeasuresEditor.vue"
import { templateMeasureCategories } from "@/features/scaffold/dataCategories"

const supported = [
    "Uncategorized",
    "Address",
    "Place",
    "City",
    "County",
    "StateOrProvince",
    "PostalCode",
    "Country",
    "Continent",
    "Latitude",
    "Longitude",
    "WebUrl",
    "ImageUrl",
    "Barcode",
]
afterEach(() => setLocale("zh-cn"))

it("前后端共享目录的业务分类与顺序一致", () => {
    const source = readFileSync(
        resolve(dirname(fileURLToPath(import.meta.url)), "../../../../src/PbiUtils.Application/TemplateMeasures/TemplateMeasureCategories.cs"),
        "utf8",
    )
    const modelCategories = source.match(/ModelCategories[\s\S]*?Array\.AsReadOnly<string>\(\[([\s\S]*?)\]\)/)?.[1]
    expect(modelCategories).toBeDefined()
    const values = [...modelCategories!.matchAll(/"([A-Za-z]+)"/g)].map((match) => match[1])
    expect(templateMeasureCategories).toEqual(values)
    expect(templateMeasureCategories).toEqual(supported)
})

it.each(supported)("文本模板度量值接受官方 Desktop 分类: %s", (dataCategory) => {
    expect(measureError([{ name: "custom", value: "value", dataCategory, enabled: true, description: "", displayFolder: "" }])).toBeUndefined()
})

it.each(["RefreshTime", "Time", "WebURL", " ImageUrl"])("表单拒绝已移除或不规范的类别: %s", (dataCategory) => {
    expect(measureError([{ name: "custom", value: "", dataCategory, enabled: true, description: "", displayFolder: "" }])).toEqual({ key: "category", row: 1 })
})

it.each(["zh-cn", "en-us"] as const)("分类下拉按Desktop顺序显示14项%s标签", (locale) => {
    setLocale(locale)
    const wrapper = mount(MeasuresEditor, {
        props: {
            modelValue: [{ name: "custom", value: "value", dataCategory: "Uncategorized", enabled: true, description: "", displayFolder: "" }],
            disabled: false,
        },
    })
    try {
        const options = wrapper.findAllComponents(ElOption)
        expect(options.map((option) => option.props("value"))).toEqual(supported)
        expect(options.map((option) => option.props("label"))).toEqual(
            locale === "zh-cn"
                ? ["未分类", "地址", "位置", "城市", "县", "省/自治区/直辖市", "邮政编码", "国家/地区", "洲", "纬度", "经度", "Web URL", "图像 URL", "条形码"]
                : [
                      "Uncategorized",
                      "Address",
                      "Place",
                      "City",
                      "County",
                      "State or province",
                      "Postal code",
                      "Country/region",
                      "Continent",
                      "Latitude",
                      "Longitude",
                      "Web URL",
                      "Image URL",
                      "Barcode",
                  ],
        )
        expect(wrapper.text()).not.toContain(locale === "zh-cn" ? "刷新时间" : "Refresh time")
    } finally {
        wrapper.unmount()
    }
})
