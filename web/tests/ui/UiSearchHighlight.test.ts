/**
 * FilePath    : pbi-utils\web\tests\ui\UiSearchHighlight.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证共享搜索标记不解释特殊字符, 保留 Unicode 原文和重复命中.
 */
import { expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { h } from "vue"
import UiSearchHighlight from "@/ui/UiSearchHighlight.vue"

it.each([
    ["Sales sales SALES", "sale", ["Sale", "sale", "SALE"]],
    ["[收入].* (收入)+", ".* [收入] +", ["[收入]", ".*", "+"]],
    ["İ收入😀收入", "收入", ["收入", "收入"]],
    ["收入收益", "收入 收", ["收入", "收"]],
    ["<img src=x onerror=alert(1)>", "img", ["img"]],
    ["Sales", "  \t ", []],
] as const)("安全标记 %s 的字面查询 %s", (text, query, expected) => {
    // 使用真实父元素读取完整文本, 避免测试工具对 Fragment 的每个文本节点独立 trim.
    const wrapper = mount({ render: () => h("span", [h(UiSearchHighlight, { text, query })]) })
    try {
        expect(wrapper.text()).toBe(text)
        expect(wrapper.findAll("mark").map((part) => part.text())).toEqual(expected)
        expect(wrapper.find("img").exists()).toBe(false)
    } finally {
        wrapper.unmount()
    }
})
