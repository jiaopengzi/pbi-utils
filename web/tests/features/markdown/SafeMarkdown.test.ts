/**
 * FilePath    : pbi-utils\web\tests\features\markdown\SafeMarkdown.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 共享文档的安全 Markdown、流式片段更新、局部锚点及共享代码着色行为.
 */
import { mount } from "@vue/test-utils"
import { expect, it, vi } from "vitest"
import SafeMarkdown from "@/features/markdown/SafeMarkdown.vue"

it("共享正文显示 Markdown 语义, 围栏与行内代码保留原文且使用本地着色", () => {
    const code = "VAR Result = SUM ( 'Sales'[Amount] )\nRETURN Result\n"
    const wrapper = mount(SafeMarkdown, {
        props: {
            source: `# Analysis\n\n**Total** with \`SUM\` and *notes*.\n\n- First\n- Second\n\n3. Third\n\n> Verify the result.\n\n| Name | Value |\n| --- | --- |\n| Total | 42 |\n\n\`\`\`dax\n${code}\`\`\``,
        },
    })
    expect(wrapper.find("h1").attributes("id")).toBe("markdown-heading-analysis")
    expect(wrapper.find("strong").text()).toBe("Total")
    expect(wrapper.find("em").text()).toBe("notes")
    expect(wrapper.find("p code").text()).toBe("SUM")
    expect(wrapper.findAll("ul > li")).toHaveLength(2)
    expect(wrapper.find("ol").attributes("start")).toBe("3")
    expect(wrapper.find("blockquote").text()).toBe("Verify the result.")
    expect(wrapper.findAll("table td").map((cell) => cell.text())).toEqual(["Total", "42"])
    expect(wrapper.find("pre code").element.textContent).toBe(code)
    expect(wrapper.find("pre code span").exists()).toBe(true)
    wrapper.unmount()
})

it("流式片段从未闭合代码到完整列表和表格更新, 根节点及已有阅读位置保持稳定", async () => {
    const wrapper = mount(SafeMarkdown, { props: { source: "# Analysis\n\n```dax\nVAR Result =" } })
    const root = wrapper.element
    root.scrollTop = 120
    root.scrollLeft = 16
    expect(wrapper.find("pre code").element.textContent).toBe("VAR Result =\n")
    await wrapper.setProps({ source: "# Analysis\n\n```dax\nVAR Result = 1\nRETURN Result\n```\n\n- Fi" })
    expect(wrapper.element).toBe(root)
    expect([root.scrollTop, root.scrollLeft]).toEqual([120, 16])
    expect(wrapper.find("pre code").element.textContent).toBe("VAR Result = 1\nRETURN Result\n")
    expect(wrapper.find("li").text()).toBe("Fi")
    await wrapper.setProps({ source: "# Analysis\n\n- First\n- Second\n\n| Name | Value |\n| --- | --- |\n| Total | 1 |" })
    expect(wrapper.findAll("li").map((item) => item.text())).toEqual(["First", "Second"])
    expect(wrapper.findAll("td").map((cell) => cell.text())).toEqual(["Total", "1"])
    expect(wrapper.find("pre").exists()).toBe(false)
    await wrapper.setProps({ source: "" })
    expect(wrapper.text()).toBe("")
    expect(wrapper.find("h1,li,td,pre").exists()).toBe(false)
    wrapper.unmount()
})

it("未闭合标签、HTML 注入和远程媒体在流式的每一阶段均只能显示文本", async () => {
    const wrapper = mount(SafeMarkdown, { props: { source: "" } })
    const chunks = [
        '<img src="https://tracker.invalid/pixel" onerror="',
        'alert(1)"><script>window.attack = 1</script>\n',
        '<iframe src="https://tracker.invalid"></iframe><svg onload="alert(1)"></svg>\n\n',
        '![tracking](https://tracker.invalid/pixel)\n\n<a href="javascript:alert(1)">raw link</a>\n',
        '<video autoplay src="https://tracker.invalid/video"></video><form><input autofocus></form>',
    ]
    let source = ""
    await chunks.reduce(async (pending, chunk) => {
        await pending
        source += chunk
        await wrapper.setProps({ source })
        expect(wrapper.find("script,img,iframe,svg,video,audio,source,object,embed,style,form,input").exists()).toBe(false)
        expect(wrapper.find("[onerror],[onload],[src],[style],[autofocus]").exists()).toBe(false)
    }, Promise.resolve())
    expect(wrapper.text()).toContain("<script>window.attack = 1</script>")
    expect(wrapper.text()).toContain("tracking")
    expect(wrapper.find("a").exists()).toBe(false)
    wrapper.unmount()
})

it.each([
    "javascript:alert%281%29",
    "JaVaScRiPt:alert%281%29",
    "javascript&#58;alert%281%29",
    "data:text/html,payload",
    "file:///C:/secret",
    "vbscript:alert%281%29",
    "//tracker.invalid/path",
    "https://user:secret@example.com/",
    "../outside",
])("危险或不受支持的 文档链接仅保留显示文字: %s", (href) => {
    const wrapper = mount(SafeMarkdown, { props: { source: `[Label](${href})` } })
    expect(wrapper.find("a").exists()).toBe(false)
    expect(wrapper.text()).toContain("Label")
    wrapper.unmount()
})

it("链接点击与辅助点击遵循宿主导航限制, 锚点只滚动当前文档", () => {
    const first = mount(SafeMarkdown, { props: { source: "# Result" }, attachTo: document.body })
    const wrapper = mount(SafeMarkdown, {
        props: { source: "# Result\n\n[Result](#result) [Docs](https://learn.microsoft.com/dax/) [Broken](#%ZZ)" },
        attachTo: document.body,
    })
    const firstScroll = vi.fn()
    const scroll = vi.fn()
    Object.defineProperty(first.find("h1").element, "scrollIntoView", { value: firstScroll })
    Object.defineProperty(wrapper.find("h1").element, "scrollIntoView", { value: scroll })
    const before = location.href
    for (const anchor of wrapper.findAll("a")) {
        for (const [type, button] of [
            ["click", 0],
            ["auxclick", 1],
        ] as const) {
            const event = new MouseEvent(type, { bubbles: true, cancelable: true, button })
            expect(anchor.element.dispatchEvent(event)).toBe(false)
            expect(event.defaultPrevented).toBe(true)
        }
    }
    expect(scroll).toHaveBeenCalledExactlyOnceWith({ block: "start" })
    expect(firstScroll).not.toHaveBeenCalled()
    expect(location.href).toBe(before)
    expect(wrapper.findAll("a")[1]!.attributes("rel")).toBe("noopener noreferrer")
    first.unmount()
    wrapper.unmount()
})

it("语言和流式内容替换移除过期着色, 超长代码仍保留全文", async () => {
    const wrapper = mount(SafeMarkdown, { props: { source: "```dax\nSUM ( Sales[Amount] )\n```" } })
    expect(wrapper.find("pre code span").exists()).toBe(true)
    await wrapper.setProps({ source: "```unknown\n<img src=remote onerror=attack>\n```" })
    expect(wrapper.find("pre code").element.textContent).toBe("<img src=remote onerror=attack>\n")
    expect(wrapper.find("pre code span,img").exists()).toBe(false)
    const code = `VAR Result = 1\n${"// retained\n".repeat(3000)}`
    await wrapper.setProps({ source: `\`\`\`dax\n${code}\`\`\`` })
    expect(wrapper.find("pre code").element.textContent).toBe(code)
    expect(wrapper.find("pre code span").exists()).toBe(false)
    wrapper.unmount()
})
