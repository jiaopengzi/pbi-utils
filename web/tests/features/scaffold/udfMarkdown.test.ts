/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfMarkdown.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 UDF 文档的 Markdown 语义、安全链接与禁止原始 HTML/外部媒体.
 */
import { expect, it, vi } from "vitest"
import { mount } from "@vue/test-utils"
import { renderMarkdown } from "@/features/markdown/markdown"
import UdfMarkdown from "@/features/scaffold/UdfMarkdown.vue"

/** 将已净化文档放入隔离节点, 仅检查输出语义, 不触发真实导航或网络. */
function documentFor(source: string): HTMLDivElement {
    const node = document.createElement("div")
    node.innerHTML = renderMarkdown(source, "udf-heading-")
    return node
}

it("渲染标题、强调、列表、引用、表格和保留原文空行的代码块", () => {
    const node = documentFor(
        "# Title\n\n**Bold** and `SUM`\n\n- First\n- Second\n\n> Note\n\n| Name | Value |\n| --- | --- |\n| A | 1 |\n\n```dax\n() => 1\n\n// line\n```\n",
    )
    expect(node.querySelector("h1")?.textContent).toBe("Title")
    expect(node.querySelector("strong")?.textContent).toBe("Bold")
    expect(node.querySelectorAll("li")).toHaveLength(2)
    expect(node.querySelector("blockquote")?.textContent).toContain("Note")
    expect(node.querySelectorAll("table td")).toHaveLength(2)
    expect(node.querySelector("pre code")?.textContent).toBe("() => 1\n\n// line\n")
})

it("原始HTML保持文本, 图片仅显示说明, 不产生脚本/事件/媒体/表单", () => {
    const source =
        '<script>alert(1)</script>\n<img src="https://tracker.invalid/pixel" onerror="alert(1)">\n<iframe src="file:///secret"></iframe>\n\n![Image description](https://tracker.invalid/pixel)\n\n![Vector](data:image/svg+xml,<svg/onload=alert(1)>)'
    const node = documentFor(source)
    expect(node.querySelector("script,img,iframe,video,audio,source,object,embed,style,input,form")).toBeNull()
    expect(node.textContent).toContain("<script>alert(1)</script>")
    expect(node.textContent).toContain("Image description")
    expect(node.querySelector("[onerror], [onload], [style], [src]")).toBeNull()
})

it.each([
    "javascript:alert%281%29",
    "data:text/html,payload",
    "file:///C:/secret",
    "vbscript:alert%281%29",
    "//tracker.invalid/path",
    "javascript&#58;alert%281%29",
    "https://user:secret@example.com/",
])("危险或不受支持链接仅保留文本: %s", (href) => {
    const node = documentFor(`[label](${href})`)
    expect(node.querySelector("a")).toBeNull()
    expect(node.textContent).toContain("label")
})

it("允许明确HTTP(S)与页面锚点, 属性内容无法逃逸", () => {
    const node = documentFor('[Docs](https://daxlib.org/package/Example/ "quoted & title") and [section](#usage)')
    expect(node.querySelector("a")?.getAttribute("href")).toBe("https://daxlib.org/package/Example/")
    expect(node.querySelectorAll("a")[1]?.getAttribute("href")).toBe("#usage")
    expect(node.querySelector("a")?.getAttribute("rel")).toBe("noopener noreferrer")
})

it("文档链接真实点击阻止默认导航, 保留当前应用地址", async () => {
    const wrapper = mount(UdfMarkdown, { attachTo: document.body, props: { source: "[Docs](https://daxlib.org/package/Example/) and [section](#usage)" } })
    const before = location.href
    for (const anchor of wrapper.findAll("a")) {
        const event = new MouseEvent("click", { bubbles: true, cancelable: true })
        expect(anchor.element.dispatchEvent(event)).toBe(false)
        expect(event.defaultPrevented).toBe(true)
    }
    expect(location.href).toBe(before)
    wrapper.unmount()
})

it("文档中的 DAX/M/TMDL 围栏复用本地着色, 切换文档保留完整原文并清除旧着色", async () => {
    const dax = "EVALUATE SUMMARIZECOLUMNS('Sales'[Amount], \"Total\", SUM('Sales'[Amount]))\n\n// <script>alert(1)</script>\n"
    const wrapper = mount(UdfMarkdown, { props: { source: `### Basic Usage\n\n\`\`\`dax\n${dax}\`\`\`` } })
    const code = wrapper.find("pre code")
    expect(code.element.textContent).toBe(dax)
    expect(code.findAll("span").length).toBeGreaterThan(0)
    expect(wrapper.find("script").exists()).toBe(false)
    await wrapper.setProps({ source: "```powerquery\nlet Value = 42 in Value\n```" })
    expect(wrapper.find("pre code").element.textContent).toBe("let Value = 42 in Value\n")
    expect(wrapper.find("pre code span").exists()).toBe(true)
    await wrapper.setProps({ source: "```tmdl\nmeasure Total = SUM(Sales[Amount])\n```" })
    expect(wrapper.find("pre code span").exists()).toBe(true)
    await wrapper.setProps({ source: "```unknown\n<b>Plain text</b>\n```" })
    expect(wrapper.find("pre code").element.textContent).toBe("<b>Plain text</b>\n")
    expect(wrapper.find("pre code span, pre code b").exists()).toBe(false)
    wrapper.unmount()
})

it("超长代码围栏保留全部原文并按共享预览边界降级, 文档嵌套列表和有序列表保持语义", () => {
    const expression = `EVALUATE { 1 }\n${"// content\n".repeat(3000)}`
    const wrapper = mount(UdfMarkdown, {
        props: { source: `## Function Reference\n\n3. Schema\n   - Tables\n   - Columns\n\n\`\`\`dax\n${expression}\`\`\`` },
    })
    expect(wrapper.find("ol").attributes("start")).toBe("3")
    expect(wrapper.findAll("ol > li > ul > li")).toHaveLength(2)
    expect(wrapper.find("pre code").element.textContent).toBe(expression)
    expect(wrapper.find("pre code span").exists()).toBe(false)
    wrapper.unmount()
})

it("文档标题产生稳定且去重的局部锚点, 格式化文字和中文标题保持可定位", () => {
    const source = "# **Usage**\n\n## Usage\n\n## Usage-1\n\n## Usage\n\n## 中文说明\n\n[说明](#%E4%B8%AD%E6%96%87%E8%AF%B4%E6%98%8E)"
    const node = documentFor(source)
    expect([...node.querySelectorAll("h1,h2")].map((heading) => heading.id)).toEqual([
        "udf-heading-usage",
        "udf-heading-usage-1",
        "udf-heading-usage-1-1",
        "udf-heading-usage-2",
        "udf-heading-中文说明",
    ])
    expect(node.querySelector("a")?.getAttribute("href")).toBe("#%E4%B8%AD%E6%96%87%E8%AF%B4%E6%98%8E")
    expect(documentFor("# Usage").querySelector("h1")?.id).toBe("udf-heading-usage")
})

it("只在当前文档中滚动标题锚点, 外部链接和畸形锚点仍禁止导航", () => {
    const first = mount(UdfMarkdown, { attachTo: document.body, props: { source: "# Usage" } })
    const wrapper = mount(UdfMarkdown, {
        attachTo: document.body,
        props: { source: "[Usage](#usage) [中文](#中文说明) [Broken](#%ZZ) [External](https://daxlib.org/)\n\n# Usage\n\n# 中文说明" },
    })
    const scroll = vi.fn()
    const firstScroll = vi.fn()
    Object.defineProperty(first.find("h1").element, "scrollIntoView", { value: firstScroll })
    for (const heading of wrapper.findAll("h1")) Object.defineProperty(heading.element, "scrollIntoView", { value: scroll })
    const before = location.href
    for (const anchor of wrapper.findAll("a")) {
        const event = new MouseEvent("click", { bubbles: true, cancelable: true })
        expect(anchor.element.dispatchEvent(event)).toBe(false)
    }
    expect(scroll).toHaveBeenCalledTimes(2)
    expect(firstScroll).not.toHaveBeenCalled()
    expect(location.href).toBe(before)
    first.unmount()
    wrapper.unmount()
})
