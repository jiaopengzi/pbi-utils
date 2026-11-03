/**
 * FilePath    : pbi-utils\web\tests\responsive.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证桌面最小内容区、全屏和还原布局, 显式 mock 不替代真实 WebView2 验收.
 */

/* oxlint-disable no-await-in-loop -- 同一个窗口的缩放、导航和弹窗必须顺序执行, 保留最大化/还原的状态. */

import { chromium } from "playwright"
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const evidence = fileURLToPath(new URL("../../.docs/evidence/connections/desktop-responsive-800/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const rows = []
const issues = new Map()
const pages = [
    ["连接页", ".connection-center"],
    ["模型", ".object-detail"],
    ["报表", ".content .el-empty"],
    ["用户", ".el-table"],
    ["设置", ".settings-workspace"],
    ["新建", ".new-placeholder"],
    ["关于", ".environment-descriptions"],
]

/** 验证外层无横向溢出、侧栏仍可用和列表/详情并排, 表格可在自身内部滚动. */
async function geometry(page, width, height) {
    const value = await page.evaluate(() => {
        const main = document.querySelector("main")
        const sidebar = document.querySelector(".sidebar")
        const columns = [...document.querySelectorAll(".object-layout, .form-row, .stats-grid")].map((element) => {
            const [first, second] = element.children
            const a = first?.getBoundingClientRect()
            const b = second?.getBoundingClientRect()
            return { className: element.className, sideBySide: !b || (b.left >= a.right && Math.abs(b.top - a.top) <= 1) }
        })
        return {
            viewport: { width: innerWidth, height: innerHeight },
            pageWidth: document.documentElement.scrollWidth,
            mainWidth: main.clientWidth,
            mainScrollWidth: main.scrollWidth,
            shellHeight: document.querySelector(".shell").getBoundingClientRect().height,
            sidebarWidth: sidebar.getBoundingClientRect().width,
            sidebarHeight: sidebar.clientHeight,
            sidebarScrollHeight: sidebar.scrollHeight,
            columns,
        }
    })
    assert.equal(value.viewport.width, width)
    assert.equal(value.viewport.height, height)
    assert.ok(value.pageWidth <= width, JSON.stringify(value))
    assert.ok(value.mainScrollWidth <= value.mainWidth + 1, JSON.stringify(value))
    assert.equal(value.shellHeight, height)
    assert.ok(value.sidebarScrollHeight <= value.sidebarHeight, "侧栏不应出现纵向滚动条: " + JSON.stringify(value))
    assert.ok(
        value.columns.every((column) => column.sideBySide),
        JSON.stringify(value),
    )
    return value
}

/** 滚动后完整看到目标, 不靠隐藏溢出来冒充操作可达. */
async function reachable(locator, height) {
    await locator.scrollIntoViewIfNeeded()
    const box = await locator.boundingBox()
    assert.ok(box && box.y >= -1 && box.y + box.height <= height + 1, JSON.stringify(box))
}

try {
    // 在同一个页面反复缩放并回到最小尺寸, 覆盖最大化/还原时的状态保留.
    const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
    page.setDefaultTimeout(10000)
    await monitorRuntime(page, issues)
    await page.goto("http://127.0.0.1:5173", { waitUntil: "networkidle" })
    await page.getByText("示例报表 (模拟数据)", { exact: true }).waitFor()
    assert.equal(await page.locator(".sidebar-footer p").innerText(), "浏览器模拟预览")
    await assertInlineConnections(page)
    await page.locator(".connection-center .desktop-file-button").click()
    await page.locator(".hero").waitFor()

    for (const [width, height] of [
        [1422, 800],
        [1440, 800],
        [1440, 900],
        [1441, 900],
        [1919, 1080],
        [1920, 1080],
        [2560, 1080],
        [2560, 1440],
        [3840, 2160],
        [1422, 800],
    ]) {
        await page.setViewportSize({ width, height })
        for (const [name, selector] of pages) {
            await page.getByRole("navigation").getByRole("button", { name, exact: true }).click()
            await page.locator(selector).first().waitFor()
            await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
            const value = await geometry(page, width, height)
            assert.equal(await page.locator(".workspace-footer").count(), 0)
            const action = page.locator("main button:visible").last()
            if (await action.count()) await reachable(action, height)
            await reachable(page.getByRole("button", { name: "折叠侧栏", exact: true }), height)
            assert.ok(await page.getByRole("navigation").getByRole("button", { name: "新建", exact: true }).locator(".nav-label").isVisible())
            if ((width === 1422 || width === 1920) && !rows.some((row) => row.name === name && row.width === width)) {
                await page.locator("main").evaluate((element) => (element.scrollTop = 0))
                await page.screenshot({ path: evidence + name + "-" + width + "x" + height + ".png" })
            }
            rows.push({ name, width, height, status: "PASS", geometry: value })
        }
        // 新建页只保留结构, 示例完整表单与模拟结果在相同桌面尺寸下均可滚动到达.
        await page.getByRole("navigation").getByRole("button", { name: "新建", exact: true }).click()
        await page.getByRole("tab", { name: "示例", exact: true }).click()
        await page.locator(".sample-panel").waitFor()
        assert.equal(await page.locator(".content > .intro, .content > .eyebrow, .content > h1").count(), 0)
        const directory = await page.locator(".sample-directory-row").evaluate((element) => {
            const items = [...element.children].map((child) => {
                const rectangle = child.getBoundingClientRect()
                return { top: rectangle.top, bottom: rectangle.bottom, left: rectangle.left, right: rectangle.right, width: rectangle.width }
            })
            return { items, fits: element.scrollWidth <= element.clientWidth + 1 }
        })
        assert.equal(directory.items.length, 3)
        assert.ok(directory.fits, "目录框及两按钮不能换行或溢出")
        assert.ok(directory.items.every((item) => Math.abs(item.top - directory.items[0].top) <= 1))
        assert.ok(directory.items[0].width > 250, "目录路径保留可读宽度")
        assert.equal(await page.getByRole("button", { name: "刷新结果", exact: true }).count(), 0)
        assert.equal(await page.locator(".sample-config .hint, .sample-config .eyebrow").count(), 0)
        await page.locator(".sample-config button img.ui-icon").evaluateAll((icons) =>
            Promise.all(
                icons.map((icon) =>
                    icon.complete
                        ? Promise.resolve()
                        : new Promise((resolve) => {
                              icon.addEventListener("load", resolve, { once: true })
                              icon.addEventListener("error", resolve, { once: true })
                          }),
                ),
            ),
        )
        const missingIcons = await page
            .locator(".sample-config button")
            .evaluateAll((buttons) =>
                buttons
                    .filter(
                        (button) =>
                            !button.querySelector("svg") && ![...button.querySelectorAll("img.ui-icon")].some((icon) => icon.complete && icon.naturalWidth > 0),
                    )
                    .map((button) => button.textContent),
            )
        assert.deepEqual(missingIcons, [], "所有示例按钮都必须实际载入图标")
        if (width === 1422 && rows.filter((row) => row.name === "示例").length === 0) {
            await page.locator(".new-panel .el-tabs__active-bar").evaluate(async (element) => {
                await Promise.all(element.getAnimations().map((animation) => animation.finished.catch(() => undefined)))
            })
            await page.locator("main").evaluate((element) => (element.scrollTop = 0))
            await page.screenshot({ path: evidence + "示例配置-1422x800.png" })
        }
        await page.getByRole("button", { name: "生成示例数据", exact: true }).click()
        await page.locator(".sample-results .el-table").waitFor()
        const sampleGeometry = await geometry(page, width, height)
        await reachable(page.getByRole("button", { name: "清空数据", exact: true }), height)
        await reachable(page.locator(".sample-results .el-table").locator(".el-table__row").last(), height)
        const sampleSimulation = page.locator(".el-message--info .el-message__content").filter({ hasText: /^浏览器模拟结果 · 未创建 CSV 或 PBIP 文件。$/ })
        await sampleSimulation.waitFor()
        assert.equal(await sampleSimulation.innerText(), "浏览器模拟结果 · 未创建 CSV 或 PBIP 文件。")
        if (width === 1422 || width === 1920) await page.screenshot({ path: evidence + "示例-" + width + "x" + height + ".png" })
        rows.push({ name: "示例", width, height, status: "PASS", geometry: sampleGeometry })
        await page.getByRole("tab", { name: "新建", exact: true }).click()
        assert.equal(await page.locator(".new-placeholder form").count(), 0)
        await page.getByRole("button", { name: "折叠侧栏", exact: true }).click()
        const collapsed = await geometry(page, width, height)
        assert.ok(collapsed.sidebarWidth < rows.at(-1).geometry.sidebarWidth)
        await reachable(page.getByRole("button", { name: "展开侧栏", exact: true }), height)
        await page.getByRole("button", { name: "展开侧栏", exact: true }).click()

        await page.getByRole("navigation").getByRole("button", { name: "用户", exact: true }).click()
        await page.getByRole("button", { name: "新增类别", exact: true }).click()
        const dialog = page.getByRole("dialog")
        await dialog.waitFor()
        await reachable(dialog.getByRole("button", { name: "保存类别", exact: true }), height)
        assert.ok(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth + 1))
        await page.keyboard.press("Escape")
        await dialog.waitFor({ state: "hidden" })
    }
    // 保留外部模型超长标题的压力检查, 不冒充真实实例或模型读取.
    await page.getByRole("navigation").getByRole("button", { name: "连接页", exact: true }).click()
    const instanceTitle = page.locator(".instance-heading h2").first()
    await instanceTitle.waitFor()
    await instanceTitle.evaluate((element) => (element.textContent = "长报表标题".repeat(25)))
    const longTitle = await geometry(page, 1422, 800)
    await reachable(page.getByRole("button", { name: "刷新实例", exact: true }), 800)
    rows.push({ name: "外部元数据长标题", width: 1422, height: 800, status: "PASS", geometry: longTitle })
    await assertInlineConnections(page)
    await collectRuntimeErrors(page, issues)
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        evidence + "responsive.json",
        JSON.stringify({ status: "PASS", browser: browser.version(), mode: "explicit mock preview", rows, consoleIssues: [...issues] }, null, 2),
    )
    console.log("PASS " + rows.length + " page/viewport checks; resize/restore, sidebar and dialogs reachable; console clean; explicit mock only")
} catch (cause) {
    await writeFile(evidence + "responsive-failure.json", JSON.stringify({ status: "FAIL", message: String(cause), rows, issues: [...issues] }, null, 2))
    throw cause
} finally {
    await browser.close()
}
