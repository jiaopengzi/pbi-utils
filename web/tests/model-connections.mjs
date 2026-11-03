/**
 * FilePath    : pbi-utils\web\tests\model-connections.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 用显式浏览器模拟验证连接页三种连接来源、登录选择流程和桌面尺寸, 不冒充真实认证.
 */
/* oxlint-disable no-await-in-loop -- 同一工作区的连接与缩放必须按顺序验收. */
import { chromium } from "playwright"
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const evidence = fileURLToPath(new URL("../../artifacts/acceptance/261015-inline-centered/model-connections/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const rows = []
const issues = new Map()
/** 连接中心在最小桌面内容区可滚动访问全部操作, 外壳与侧栏不溢出. */
async function geometry(page) {
    const value = await page.evaluate(() => {
        const center = document.querySelector(".connection-center")
        const sidebar = document.querySelector(".sidebar")
        return {
            width: innerWidth,
            height: innerHeight,
            pageWidth: document.documentElement.scrollWidth,
            sidebarHeight: sidebar.clientHeight,
            sidebarScrollHeight: sidebar.scrollHeight,
            centerWidth: center?.clientWidth,
            centerScrollWidth: center?.scrollWidth,
        }
    })
    assert.ok(value.pageWidth <= value.width, JSON.stringify(value))
    assert.ok(value.sidebarScrollHeight <= value.sidebarHeight, JSON.stringify(value))
    assert.ok(value.centerWidth > 0, "连接中心应可见: " + JSON.stringify(value))
    assert.ok(value.centerScrollWidth <= value.centerWidth + 1, JSON.stringify(value))
    return value
}
try {
    const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
    page.setDefaultTimeout(15000)
    await monitorRuntime(page, issues)
    await page.goto("http://127.0.0.1:5173", { waitUntil: "networkidle" })
    await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
    const header = page.locator(".header-actions")
    assert.match(await header.innerText(), /未连接/)
    assert.doesNotMatch(await header.innerText(), /本地文件|连接远端模型/)
    await assertInlineConnections(page)
    const center = page.locator(".connection-center")
    await center.locator(".desktop-file-button").click()
    await center.locator(".instance-card.is-current").waitFor()
    assert.equal(await page.locator(".hero, .stats-grid").count(), 0)
    assert.match(await header.innerText(), /Power BI Desktop/)
    rows.push({ scenario: "连接页文件入口 → Desktop 模拟模型", status: "PASS" })
    await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
    await center.getByRole("textbox", { name: "工作或学校邮箱", exact: true }).fill("preview@example.com")
    await center.getByRole("button", { name: "登录", exact: true }).click()
    await center.getByRole("combobox", { name: "登录环境", exact: true }).press("Enter")
    await page.getByRole("option", { name: "全球版", exact: true }).click()
    await center.getByRole("button", { name: "登录", exact: true }).click()
    await center.getByText("preview@example.com (模拟)", { exact: false }).waitFor()
    assert.match(await center.innerText(), /浏览器模拟登录与目录/)
    await center.locator(".el-select").last().click()
    await page.getByRole("option", { name: "销售模型 (模拟)", exact: true }).click()
    for (const [width, height] of [
        [1422, 800],
        [1920, 1080],
        [1422, 800],
    ]) {
        await page.setViewportSize({ width, height })
        const action = center.locator(".instance-card:not(.is-current) .instance-connect-button")
        await action.scrollIntoViewIfNeeded()
        const box = await action.boundingBox()
        assert.ok(box && box.y + box.height <= height, JSON.stringify(box))
        rows.push({ scenario: "Service 登录与选择", mode: "explicit mock", status: "PASS", geometry: await geometry(page) })
        await page.screenshot({ path: evidence + `service-picker-${width}x${height}.png` })
    }
    await center.locator(".instance-card:not(.is-current) .instance-connect-button").click()
    await center.getByRole("button", { name: "查看模型", exact: true }).waitFor({ state: "visible" })
    await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
    assert.match(await header.innerText(), /Power BI Service · 只读/)
    await header.getByRole("button", { name: "刷新模型", exact: true }).click()
    await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
    assert.match(await header.innerText(), /Power BI Service · 只读/)
    rows.push({ scenario: "Service 模拟连接与刷新保持来源", status: "PASS" })
    await center.getByRole("button", { name: "退出登录", exact: true }).click()
    await center.getByRole("button", { name: "登录", exact: true }).waitFor()
    assert.match(await header.innerText(), /未连接/)
    await center.getByRole("tab", { name: "SSAS / XMLA", exact: true }).click()
    await center.locator(".remote-connection[aria-busy=false]").waitFor()
    await center.getByRole("textbox", { name: "服务器", exact: true }).fill("ssas.example")
    await center.getByRole("textbox", { name: "数据库 / 模型", exact: true }).fill("demo")
    await center.getByRole("button", { name: "连接", exact: true }).click()
    await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
    assert.match(await header.innerText(), /SSAS \/ XMLA · 只读/)
    rows.push({ scenario: "退出 Service → SSAS/XMLA 模拟连接", status: "PASS" })
    await center.getByRole("tab", { name: "Power BI Desktop", exact: true }).click()
    await center.getByRole("button", { name: "切换到此实例", exact: true }).click()
    await center.getByRole("button", { name: "查看模型", exact: true }).waitFor()
    assert.match(await header.innerText(), /Power BI Desktop · 读写/)
    rows.push({ scenario: "切换到 Desktop 模拟实例", status: "PASS" })
    await page.screenshot({ path: evidence + "desktop-source-1422x800.png" })
    await header.getByRole("button", { name: "断开连接", exact: true }).click()
    await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
    assert.match(await header.innerText(), /未连接/)
    await assertInlineConnections(page)
    await collectRuntimeErrors(page, issues)
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        evidence + "browser.json",
        JSON.stringify(
            { status: "PASS", browser: browser.version(), mode: "explicit mock", realMicrosoftSignIn: "UNRUN", rows, consoleIssues: [...issues] },
            null,
            2,
        ),
    )
    console.log(`PASS ${rows.length} connection/viewport scenarios; console clean; real Microsoft login UNRUN`)
} catch (error) {
    await writeFile(evidence + "browser-failure.json", JSON.stringify({ status: "FAIL", message: String(error), rows, consoleIssues: [...issues] }, null, 2))
    throw error
} finally {
    await browser.close()
}
