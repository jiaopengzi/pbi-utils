/**
 * FilePath    : pbi-utils\web\tests\features\code\CodeBlock.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证代码文本边界, 复制原文, 更新和长度降级.
 */
import { expect, it, vi } from "vitest"
import { mount } from "@vue/test-utils"
import CodeBlock from "@/features/code/CodeBlock.vue"
import { normalizeLanguage, paintCode } from "@/features/code/highlight"
import { ElMessage } from "element-plus"

it("白名单别名和未知语言不混淆", () => {
    expect(["DAX", "m", "pq", "powerquery", "TMDL", "html", ""].map(normalizeLanguage)).toEqual(["dax", "m", "m", "m", "tmdl", "plaintext", "plaintext"])
})
it("危险字符始终为数据, 更新不嵌套标签, 复制保持原文", async () => {
    const code = "SUM('中文'[金额])\n// </code><img src=x onerror=alert(1)><script>x</script>&\""
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.spyOn(navigator.clipboard, "writeText").mockImplementation(writeText)
    const wrapper = mount(CodeBlock, { props: { code, language: "dax" } })
    expect(wrapper.get("code").element.textContent).toBe(code)
    expect(wrapper.find("img, script").exists()).toBe(false)
    expect(wrapper.get("pre").attributes("data-lang")).toBe("dax")
    expect(wrapper.get("button").text()).toBe("复制 DAX")
    await wrapper.get("button").trigger("click")
    expect(writeText).toHaveBeenCalledWith(code)
    expect(wrapper.get("button").text()).toBe("复制 DAX")
    expect(wrapper.find('small[role="status"]').exists()).toBe(false)
    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain("已复制")
    await wrapper.setProps({ code: "let x = #date(2024, 2, 29) in x", language: "pq" })
    expect(wrapper.get("pre").attributes("data-lang")).toBe("m")
    expect(wrapper.get("button").text()).toBe("复制 M")
    expect(wrapper.get("code").text()).toBe("let x = #date(2024, 2, 29) in x")
    wrapper.unmount()
    ElMessage.closeAll()
    vi.restoreAllMocks()
})
it("超长文本保持原文且不运行高亮", () => {
    const node = document.createElement("code")
    const text = "<img>".repeat(7000)
    paintCode(node, text, "dax")
    expect(node.textContent).toBe(text)
    expect(node.children.length).toBe(0)
})

it("三种语言预览共用主题, 保留 CRLF 和危险字符, 重绘清除旧节点", () => {
    const node = document.createElement("code")
    const cases = [
        ["dax", "VAR 中文变量 = 1.5e-3\r\nRETURN 自定义.计算('O''Brien'[金额]]净额], \"字\"\"符\") -- <img>"],
        ["m", 'let #"源" = #date(2026, 10, 13), 文本 = Text.From("<script>") in #"源"'],
        ["tmdl", "table '销售'\r\n\tmeasure '总额' = SUM('销售'[金额])\r\n\t\tformatString: 0.00"],
    ]
    for (const [language, code] of cases) {
        paintCode(node, code!, language!)
        expect(node.textContent).toBe(code)
        expect(node.querySelector(".pbi-code-keyword")).not.toBeNull()
        expect(node.querySelector(".pbi-code-function")).not.toBeNull()
        expect(node.querySelector("script, img, span span")).toBeNull()
    }
    paintCode(node, '<img src="x">', "html")
    expect(node.textContent).toBe('<img src="x">')
    expect(node.childElementCount).toBe(0)
})
