/**
 * FilePath    : pbi-utils\web\tests\service-clouds.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 用显式浏览器模拟验证单按钮、多云选择、单云直登及最近邮箱恢复, 不冒充真实认证.
 */
/* oxlint-disable no-await-in-loop -- 同一连接中心的状态与尺寸依次验证. */
import { chromium } from "playwright"
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const evidence = fileURLToPath(new URL("../../.docs/evidence/connections/service-login/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const issues = new Map()
const rows = []
/** 模拟固定测试邮箱只返回单云; 生产服务始终通过微软发现, 不按邮箱猜测. */
function installSingleCloud(Transport) {
    const original = Transport.prototype.postMessage
    Transport.prototype.postMessage = function (value) {
        if (value.method === "powerbi.serviceSignIn" && !value.params.environmentName) {
            if (value.params.email === "single-global@example.com") value = { ...value, params: { ...value.params, environmentName: "GlobalCloud" } }
            if (value.params.email === "single-china@example.com") value = { ...value, params: { ...value.params, environmentName: "ChinaCloud" } }
        }
        return original.call(this, value)
    }
}
/** 操作可通过连接页滚动区域访问, 外壳与侧栏不出现横向溢出. */
async function geometry(page, center) {
    const loginButton = center.getByRole("button", { name: "登录", exact: true })
    await loginButton.scrollIntoViewIfNeeded()
    await loginButton.click({ trial: true })
    const result = await page.evaluate(() => {
        const sidebar = document.querySelector(".sidebar")
        const surface = document.querySelector(".connection-center")
        const login = [...surface.querySelectorAll("button")].find((element) => element.textContent.trim() === "登录")
        return {
            width: innerWidth,
            height: innerHeight,
            pageWidth: document.documentElement.scrollWidth,
            sidebarHeight: sidebar.clientHeight,
            sidebarScrollHeight: sidebar.scrollHeight,
            centerWidth: surface.clientWidth,
            centerScrollWidth: surface.scrollWidth,
            loginTop: login.getBoundingClientRect().top,
            loginBottom: login.getBoundingClientRect().bottom,
        }
    })
    assert.ok(result.pageWidth <= result.width, JSON.stringify(result))
    assert.ok(result.sidebarScrollHeight <= result.sidebarHeight + 1, JSON.stringify(result))
    assert.ok(result.centerScrollWidth <= result.centerWidth + 1, JSON.stringify(result))
    assert.ok(result.loginTop >= -1 && result.loginBottom <= result.height + 1, JSON.stringify(result))
    return result
}
try {
    const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
    page.setDefaultTimeout(15000)
    await monitorRuntime(page, issues)
    await page.route(/\/src\/bridge\/mock\.ts/, async (route) => {
        const response = await route.fetch()
        await route.fulfill({ response, body: (await response.text()) + `\n(${installSingleCloud.toString()})(MockTransport);` })
    })
    await page.goto("http://127.0.0.1:5173", { waitUntil: "networkidle" })
    await assertInlineConnections(page)
    const center = page.locator(".connection-center")
    await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
    const email = center.getByRole("textbox", { name: "工作或学校邮箱", exact: true })
    const login = center.getByRole("button", { name: "登录", exact: true })
    assert.equal(await login.count(), 1)
    assert.equal(await center.getByRole("button", { name: "获取登录环境", exact: true }).count(), 0)
    assert.equal(await center.getByRole("combobox", { name: "登录环境", exact: true }).count(), 0)
    await email.fill("invalid")
    await login.click()
    await center.getByText("请输入有效的邮箱地址", { exact: true }).waitFor()
    rows.push({ scenario: "一个登录按钮及无效邮箱校验", status: "PASS" })
    await email.fill("preview@example.com")
    await login.click()
    await center.getByText("此邮箱有多个登录环境，请确认选择后再次点击登录。", { exact: true }).waitFor()
    await center.getByRole("combobox", { name: "登录环境", exact: true }).press("Enter")
    await page.getByRole("option", { name: "世纪互联 (中国)", exact: true }).click()
    for (const [width, height] of [
        [1422, 800],
        [1920, 1080],
    ]) {
        await page.setViewportSize({ width, height })
        rows.push({ scenario: `多云选择 ${width}×${height}`, status: "PASS", geometry: await geometry(page, center) })
        await page.screenshot({ path: evidence + `multi-${width}x${height}.png` })
    }
    await login.click()
    await center.getByText("preview@example.com (模拟) · 世纪互联 (中国)", { exact: true }).waitFor()
    await center.getByRole("button", { name: "退出登录", exact: true }).click()
    await center.getByRole("combobox", { name: "最近登录", exact: true }).waitFor()
    await page.getByRole("navigation").getByRole("button", { name: "关于", exact: true }).click()
    await page.getByRole("navigation").getByRole("button", { name: "连接页", exact: true }).click()
    await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
    assert.equal(await email.inputValue(), "preview@example.com")
    await login.click()
    await center.getByText("此邮箱有多个登录环境，请确认选择后再次点击登录。", { exact: true }).waitFor()
    await center.getByRole("combobox", { name: "登录环境", exact: true }).press("Enter")
    assert.equal(await page.getByRole("option", { name: "世纪互联 (中国)", exact: true }).getAttribute("aria-selected"), "true")
    await center.getByRole("combobox", { name: "登录环境", exact: true }).press("Escape")
    await page.setViewportSize({ width: 1422, height: 800 })
    rows.push({ scenario: "模拟最近记录恢复及多云首选恢复", status: "PASS", geometry: await geometry(page, center) })
    await page.screenshot({ path: evidence + "recent-multi-1422x800.png" })
    await center.getByRole("button", { name: "删除记录", exact: true }).click()
    await center.getByRole("combobox", { name: "最近登录", exact: true }).waitFor({ state: "hidden" })
    assert.equal(await email.inputValue(), "preview@example.com")
    rows.push({ scenario: "删除记录保留当前输入", status: "PASS" })
    for (const [address, label] of [
        ["single-global@example.com", "全球版"],
        ["single-china@example.com", "世纪互联 (中国)"],
    ]) {
        await email.fill(address)
        assert.equal(await center.getByRole("combobox", { name: "登录环境", exact: true }).count(), 0)
        await login.click()
        await center.getByText(`preview@example.com (模拟) · ${label}`, { exact: true }).waitFor()
        rows.push({ scenario: `单个${label}一次登录且无环境下拉`, status: "PASS" })
        await center.getByRole("button", { name: "退出登录", exact: true }).click()
    }
    await assertInlineConnections(page)
    await collectRuntimeErrors(page, issues)
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        evidence + "browser.json",
        JSON.stringify(
            { status: "PASS", mode: "explicit mock", rows, consoleIssues: [...issues], realAuthentication: "UNRUN", realDiskPersistence: "UNRUN" },
            null,
            2,
        ),
    )
    console.log(`PASS ${rows.length} single-login/cloud/recent scenarios; real account/disk persistence UNRUN`)
} catch (error) {
    await writeFile(evidence + "browser-failure.json", JSON.stringify({ status: "FAIL", message: String(error), rows, consoleIssues: [...issues] }, null, 2))
    throw error
} finally {
    await browser.close()
}
