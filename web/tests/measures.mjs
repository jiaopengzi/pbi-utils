/**
 * FilePath    : pbi-utils\web\tests\measures.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : CodeMirror 与度量值 CRUD 浏览器模拟验收, 记录原文、撤销、只读和桌面布局证据.
 */
/* oxlint-disable no-await-in-loop -- 界面操作和视口切换按同一用户流程顺序验收. */
import { chromium } from "playwright"
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const evidence = fileURLToPath(new URL("../../.docs/evidence/connections/codemirror-editor/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const issues = new Map()
const rows = []
/** 读取已显示的短文档各行原文, 不使用仅适用于原生表单的 inputValue. */
async function editorText(editor) {
    return editor.evaluate((node) => [...node.querySelectorAll(".cm-line")].map((line) => line.textContent).join("\n"))
}
/** 复制完整文档以检查虚拟化长文的原文, 不把可视 DOM 当作完整文档. */
async function copyEditor(page, editor) {
    await editor.press("Control+a")
    await editor.press("Control+c")
    return (await page.evaluate(() => navigator.clipboard.readText())).replaceAll("\r\n", "\n")
}
try {
    const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
    page.setDefaultTimeout(15000)
    await monitorRuntime(page, issues)
    await page.goto("http://127.0.0.1:5173/", { waitUntil: "networkidle" })
    await assertInlineConnections(page)
    await page.locator(".connection-center .desktop-file-button").click()
    await page.locator(".hero").waitFor()
    await page.getByRole("button", { name: "模型", exact: true }).click()
    const panel = page.locator(".measures-panel")
    const editor = panel.getByRole("textbox", { name: "DAX 表达式", exact: true })
    assert.equal(await editorText(editor), "\n    SUM ( '销售'[金额] )")
    assert.equal(await editor.getAttribute("contenteditable"), "true")
    const sample = "VAR 中文变量=1\nRETURN 中文变量+2"
    await editor.fill(sample)
    await editor.press("Control+a")
    await editor.press("Control+]")
    assert.equal(await editorText(editor), "    VAR 中文变量=1\n    RETURN 中文变量+2")
    await editor.press("Control+z")
    assert.equal(await editorText(editor), sample)
    await editor.press("Control+y")
    await editor.press("Control+a")
    await editor.press("Control+[")
    assert.equal(await editorText(editor), sample)
    await page.context().grantPermissions(["clipboard-read", "clipboard-write"])
    assert.equal(await copyEditor(page, editor), sample)
    const safeText = "VAR 收入 = SUM('销售'[金额]) + 1e-3\nRETURN \"<img src=x>\" // <script>alert(1)</script>"
    await editor.fill(safeText)
    assert.equal(await editorText(editor), safeText)
    assert.equal(await panel.locator(".code-editor img, .code-editor script").count(), 0)
    assert.ok((await panel.locator(".code-editor [class*='pbi-code-']").count()) >= 5)
    const syntaxColors = await panel
        .locator(".code-editor [class*='pbi-code-']")
        .evaluateAll((nodes) => [...new Set(nodes.map((node) => getComputedStyle(node).color))])
    assert.ok(syntaxColors.length >= 3, "关键字、字面量和注释应具有可见高亮")
    await editor.press("Tab")
    assert.equal(await editor.evaluate((node) => document.activeElement === node), false)
    assert.equal(await editorText(editor), safeText)
    rows.push({ scenario: "Tab 导航离开编辑器且不修改原文", mode: "Chromium keyboard with explicit mock model", status: "PASS" })

    // 合成事件只检查受控文本提交与节点生命周期, 不宣称真实 Windows 中文输入法通过.
    await editor.fill("SUM('销售'[金额])")
    assert.equal(await panel.getByRole("button", { name: "保存修改", exact: true }).isDisabled(), true)
    const originalNode = await editor.elementHandle()
    await editor.dispatchEvent("compositionstart")
    assert.equal(await panel.locator(".code-editor.is-composing").count(), 1)
    await editor.fill("VAR 销售 = 1\nRETURN 销售")
    assert.equal(await panel.getByRole("button", { name: "保存修改", exact: true }).isDisabled(), true)
    assert.equal(await editor.evaluate((node, original) => node === original, originalNode), true)
    await editor.dispatchEvent("compositionend")
    await panel.locator(".code-editor.is-composing").waitFor({ state: "detached" })
    assert.equal(await editorText(editor), "VAR 销售 = 1\nRETURN 销售")
    assert.equal(await panel.getByRole("button", { name: "保存修改", exact: true }).isEnabled(), true)
    assert.equal(await editor.evaluate((node, original) => node === original, originalNode), true)
    await originalNode.dispose()
    const longText = Array.from({ length: 60 }, (_, index) => `// 中文滚动 ${index} ${"long ".repeat(50)}`).join("\n") + "\n"
    await editor.fill(longText)
    assert.equal(await copyEditor(page, editor), longText)
    const scroller = panel.locator(".code-editor .cm-scroller")
    await scroller.evaluate((node) => {
        node.scrollTop = node.scrollHeight
        node.scrollLeft = node.scrollWidth
        node.dispatchEvent(new Event("scroll"))
    })
    const geometry = await panel.locator(".code-editor").evaluate((node) => {
        const scroll = node.querySelector(".cm-scroller")
        return {
            top: scroll.scrollTop,
            left: scroll.scrollLeft,
            width: scroll.clientWidth,
            scrollWidth: scroll.scrollWidth,
            height: scroll.clientHeight,
            scrollHeight: scroll.scrollHeight,
            pageWidth: document.documentElement.scrollWidth,
            viewport: innerWidth,
            font: getComputedStyle(node.querySelector(".cm-content")).fontFamily,
        }
    })
    assert.ok(geometry.top > 0 && geometry.left > 0)
    assert.ok(geometry.scrollWidth > geometry.width && geometry.scrollHeight > geometry.height)
    assert.ok(geometry.pageWidth <= geometry.viewport, "最小桌面尺寸的长行不撑宽页面")
    assert.match(geometry.font, /JBMono/)
    await editor.fill("VAR 收入 = SUM('销售'[金额])\nRETURN\n    DIVIDE(\n        收入,\n        100\n    ) // 本地高亮")
    await scroller.evaluate((node) => {
        node.scrollTop = 0
        node.scrollLeft = 0
        node.dispatchEvent(new Event("scroll"))
    })
    await editor.scrollIntoViewIfNeeded()
    await page.screenshot({ path: evidence + "code-editor.png" })
    rows.push({
        scenario: "CodeMirror DAX 高亮、原文复制、双向滚动与缩进撤销重做",
        mode: "Chromium with explicit mock model",
        status: "PASS",
        geometry,
    })
    rows.push({ scenario: "合成组合事件期间延迟父级更新且不重建编辑节点", mode: "synthetic composition events; real Windows IME UNRUN", status: "PASS" })
    await panel.getByRole("button", { name: "新建度量值", exact: true }).click()
    await panel.getByRole("textbox", { name: "度量值名称", exact: true }).fill("验收利润")
    await panel.getByRole("textbox", { name: "DAX 表达式", exact: true }).fill("1+2")
    await panel.getByRole("button", { name: "再添加一个", exact: true }).click()
    await panel.getByRole("textbox", { name: "度量值名称", exact: true }).nth(1).fill("验收订单")
    await panel.getByRole("textbox", { name: "DAX 表达式", exact: true }).nth(1).fill("COUNTROWS('销售')")
    await panel.getByRole("button", { name: "创建并保存", exact: true }).click()
    await panel.locator(".measure-row").filter({ hasText: "验收订单" }).waitFor()
    assert.equal(await panel.locator(".measure-row").count(), 3)
    await panel.locator(".measure-row").filter({ hasText: "验收利润" }).getByRole("button").click()
    assert.equal(await editorText(editor), "1+2")
    await editor.fill("999")
    await panel.locator(".measure-row").filter({ hasText: "验收订单" }).getByRole("button").click()
    assert.equal(await editorText(editor), "COUNTROWS('销售')")
    await editor.press("Control+z")
    assert.equal(await editorText(editor), "COUNTROWS('销售')")
    await panel.locator(".measure-row").filter({ hasText: "验收利润" }).getByRole("button").click()
    assert.equal(await editorText(editor), "1+2")
    rows.push({ scenario: "切换度量值后撤销不能恢复上一项草稿", mode: "explicit mock", status: "PASS" })
    await panel.getByRole("button", { name: "全选", exact: true }).click()
    assert.equal(await editorText(editor), "")
    assert.equal(await editor.getAttribute("aria-readonly"), "true")
    assert.equal(await editor.getAttribute("aria-disabled"), "true")
    assert.notEqual(await editor.getAttribute("contenteditable"), "true")
    await editor.evaluate((node) => node.focus())
    await page.keyboard.insertText("批量不得写入")
    assert.equal(await editorText(editor), "")
    rows.push({ scenario: "批量未勾选表达式时只读且拒绝键盘输入", mode: "Chromium keyboard with explicit mock", status: "PASS" })
    await panel.locator("label.el-checkbox").filter({ hasText: "修改显示文件夹" }).click()
    await panel.getByRole("textbox", { name: "显示文件夹", exact: true }).fill("批量验收")
    for (const [width, height] of [
        [1422, 800],
        [1920, 1080],
        [1422, 800],
    ]) {
        await page.setViewportSize({ width, height })
        await panel.getByRole("button", { name: "保存修改", exact: true }).scrollIntoViewIfNeeded()
        const viewportGeometry = await page.evaluate(() => ({
            width: innerWidth,
            pageWidth: document.documentElement.scrollWidth,
            sidebarHeight: document.querySelector(".sidebar").clientHeight,
            sidebarScrollHeight: document.querySelector(".sidebar").scrollHeight,
            font: getComputedStyle(document.querySelector(".code-editor .cm-content")).fontFamily,
        }))
        assert.ok(viewportGeometry.pageWidth <= width)
        assert.ok(viewportGeometry.sidebarScrollHeight <= viewportGeometry.sidebarHeight)
        assert.match(viewportGeometry.font, /JBMono/)
        rows.push({ scenario: "批量编辑与窗口缩放", mode: "explicit mock", status: "PASS", geometry: viewportGeometry })
        await page.screenshot({ path: evidence + `batch-${width}x${height}.png` })
    }
    await panel.getByRole("button", { name: "保存修改", exact: true }).click()
    await page.getByText("浏览器模拟保存结果，没有提交真实模型、保存文件或同步 Desktop。", { exact: true }).first().waitFor()
    await panel.locator(".measure-row").filter({ hasText: "验收利润" }).getByRole("button").click()
    assert.equal(await panel.getByRole("textbox", { name: "显示文件夹", exact: true }).inputValue(), "批量验收")
    await panel.getByRole("button", { name: "本地格式化 DAX", exact: true }).click()
    await page.getByText("浏览器模拟不执行 C# 本地格式化，请在桌面版使用", { exact: true }).waitFor()
    assert.equal(await editorText(editor), "1+2")
    await panel.getByRole("button", { name: "全选", exact: true }).click()
    await panel.getByRole("button", { name: "删除所选（3）", exact: true }).click()
    await panel.getByText("没有匹配的度量值", { exact: true }).waitFor()
    assert.equal(await panel.locator(".measure-row").count(), 0)
    rows.push({ scenario: "批量新增、单选回填、多选清空、批量属性、删除及模拟格式化标识", mode: "explicit mock", status: "PASS" })
    await collectRuntimeErrors(page, issues)
    assert.deepEqual([...issues.keys()], [])
    await writeFile(
        evidence + "browser.json",
        JSON.stringify({ rows, issues: [...issues.keys()], modelWrites: "explicit mock only", realWindowsIme: "UNRUN" }, null, 2),
    )
    console.log(JSON.stringify({ rows, issues: [...issues.keys()] }))
} finally {
    await browser.close()
}
