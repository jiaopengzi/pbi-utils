/**
 * FilePath    : pbi-utils\web\tests\desktop-instance-controls.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证双语公共 Desktop 操作栏、实例身份状态、取消和桌面尺寸, 显式模拟不替代真实 Desktop/WebView2.
 */
/* oxlint-disable no-await-in-loop -- 同一窗口的连接、断开、取消、折叠和缩放必须顺序验收. */
import { chromium } from "playwright"
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const target = process.env.PBI_INSTANCE_TEST_URL || "http://127.0.0.1:5173/"
const evidence =
    process.env.PBI_INSTANCE_TEST_EVIDENCE || fileURLToPath(new URL("../../.docs/evidence/connections/desktop-instance-controls/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const rows = []
const issues = new Map()

/** 仅拦截显式 mock 模块响应, 用同名不同身份的实例和可控在途请求验收真实 Vue 组件. */
function installFixtures(MockTransport) {
    const originalPost = MockTransport.prototype.postMessage
    const originalSend = MockTransport.prototype.send
    const requests = new Map()
    const state = {
        calls: [],
        holdNext: "",
        pending: null,
        finish(outcome = "success") {
            const pending = this.pending
            this.pending = null
            if (!pending) throw new Error("No controlled mock request")
            if (outcome === "success") dispatch(pending.transport, pending.message)
            else pending.transport.send({ protocolVersion: 1, id: pending.message.id, error: { code: outcome, message: "模拟操作已取消" } })
        },
    }
    window.desktopInstanceTest = state
    MockTransport.prototype.send = function (payload) {
        const request = requests.get(payload.id)
        if (payload.result && request?.method === "ui.settings") payload.result.locale = window.desktopInstanceTestLocale
        if (payload.result && request?.method === "project.connectLocal") {
            payload.result.connection.instanceId = `mock-desktop-instance-${request.params.processId}`
            this.workspace.currentProject.connection.instanceId = payload.result.connection.instanceId
        }
        originalSend.call(this, payload)
    }
    function dispatch(transport, message) {
        if (message.method === "powerbi.instances") {
            transport.send({
                protocolVersion: 1,
                id: message.id,
                result: {
                    timestamp: new Date().toISOString(),
                    instances: [1000, 2000].map((processId) => ({
                        processId,
                        instanceId: `mock-desktop-instance-${processId}`,
                        title: "示例报表 (模拟数据)",
                        port: 50000 + processId,
                        status: "connected",
                        errorCode: null,
                        message: null,
                        models: [{ name: "示例模型", compatibilityLevel: 1600, tableCount: 6, measureCount: 12 }],
                    })),
                },
            })
        } else originalPost.call(transport, message)
    }
    MockTransport.prototype.postMessage = function (message) {
        requests.set(message.id, message)
        state.calls.push({ method: message.method, params: message.params })
        if (state.holdNext === message.method) {
            state.holdNext = ""
            state.pending = { transport: this, message }
        } else if (message.method === "request.cancel" && message.params.requestId === state.pending?.message.id) {
            this.send({ protocolVersion: 1, id: message.id, result: { accepted: true } })
        } else dispatch(this, message)
    }
}

/** 从文档加载前开始监听全局异常, 不以“加载后没有报错”代替整个页面生命周期证据. */
async function createPage(locale) {
    const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
    page.setDefaultTimeout(15000)
    await monitorRuntime(page, issues)
    await page.addInitScript((initialLocale) => {
        window.desktopInstanceTestLocale = initialLocale
    }, locale)
    await page.route(/\/src\/bridge\/mock\.ts/, async (route) => {
        const response = await route.fetch()
        await route.fulfill({ response, body: (await response.text()) + `\n(${installFixtures.toString()})(MockTransport);` })
    })
    await page.goto(target, { waitUntil: "networkidle" })
    await page.locator(".content .instance-card").nth(1).waitFor()
    return page
}

/** 公共操作和实例上层行在实际几何保持同行, 连接页不能横向溢出. */
async function verifyLayout(page, root, locale, name) {
    await root.evaluate(async (element) => {
        await Promise.all(element.getAnimations({ subtree: true }).map((animation) => animation.finished.catch(() => undefined)))
    })
    const value = await root.evaluate((element) => {
        // oxlint-disable-next-line unicorn/consistent-function-scoping -- evaluate 在独立浏览器上下文执行, helper 必须随回调序列化, 不能提到 Node 外层.
        const rect = (item) => {
            const box = item.getBoundingClientRect()
            return { top: box.top, bottom: box.bottom, left: box.left, right: box.right }
        }
        const toolbar = element.querySelector(".desktop-instance-actions")
        const sidebar = document.querySelector(".sidebar")
        return {
            width: innerWidth,
            height: innerHeight,
            documentWidth: document.documentElement.scrollWidth,
            mainWidth: document.querySelector("main").clientWidth,
            mainScrollWidth: document.querySelector("main").scrollWidth,
            sidebarHeight: sidebar.clientHeight,
            sidebarScrollHeight: sidebar.scrollHeight,
            contentWidth: element.clientWidth,
            contentScrollWidth: element.scrollWidth,
            toolbar: [...toolbar.querySelectorAll("button")].map(rect),
            headings: [...element.querySelectorAll(".instance-heading")].map((heading) => [...heading.children].map(rect)),
            instanceRows: [...element.querySelectorAll(".instance-heading")].map((heading) => ({
                row: rect(heading),
                action: rect(heading.querySelector(".instance-connect-button")),
                title: rect(heading.querySelector("h2")),
                compatibility: rect(heading.querySelector('[data-stat="compatibilityLevel"]')),
            })),
        }
    })
    assert.ok(value.documentWidth <= value.width + 1, JSON.stringify(value))
    assert.ok(value.mainScrollWidth <= value.mainWidth + 1, JSON.stringify(value))
    assert.ok(value.sidebarScrollHeight <= value.sidebarHeight + 1, JSON.stringify(value))
    assert.ok(value.contentScrollWidth <= value.contentWidth + 1, JSON.stringify(value))
    assert.ok(value.toolbar.length >= 2)
    assert.ok(
        value.toolbar.every((box) => Math.abs(box.top - value.toolbar[0].top) <= 1),
        JSON.stringify(value.toolbar),
    )
    assert.ok(value.headings.every((heading) => heading.every((box) => Math.min(box.bottom, heading[0].bottom) > Math.max(box.top, heading[0].top))))
    assert.ok(
        value.instanceRows.every((item) => Math.abs(item.action.left - item.row.left) <= 1),
        "实例连接动作应位于整行开头",
    )
    assert.ok(value.instanceRows.every((item) => item.compatibility.top >= item.title.top && item.compatibility.bottom <= item.row.bottom + 1))
    assert.equal(await root.locator(".scan-toolbar").count(), 0)
    assert.equal(await root.locator(".desktop-file-button").count(), 1)
    assert.equal(await root.locator(".instance-refresh-button").innerText(), locale === "zh-cn" ? "刷新实例" : "Refresh instances")
    assert.equal(await root.locator(".instance-connection").count(), 2)
    assert.equal(await root.locator(".instance-status").count(), 0)
    assert.equal(await root.locator(".instance-card .el-descriptions").count(), 0)
    assert.equal(await root.locator(".instance-card .model-summary").count(), 0)
    assert.deepEqual(await root.locator('[data-stat="compatibilityLevel"] dd').allTextContents(), ["1600", "1600"])
    assert.equal(await root.getByRole("button", { name: locale === "zh-cn" ? "检查 UDF 能力" : "Check UDF capabilities", exact: true }).count(), 0)
    assert.doesNotMatch(await root.locator(".instance-card").first().innerText(), /可连接 · 读写|Available · Read\/write/)
    assert.equal(await root.locator(".instance-stat").count(), 8)
    await assertInlineConnections(page)
    await page.screenshot({ path: evidence + `${locale}-${name}.png` })
    rows.push({ locale, scenario: name, status: "PASS", geometry: value })
}

/** 验证两张同名卡片由精确身份区分, 当前按钮允许查看并保持其他实例可切换. */
async function expectCurrent(root, current, locale) {
    const cards = root.locator(".instance-card")
    assert.equal(await cards.count(), 2)
    for (const index of [0, 1]) {
        const isCurrent = index === current
        assert.equal(
            await cards.nth(index).locator(".instance-connection").innerText(),
            locale === "zh-cn" ? (isCurrent ? "已连接" : "未连接") : isCurrent ? "Connected" : "Not connected",
        )
        assert.equal(await cards.nth(index).locator(".instance-connect-button").isDisabled(), false)
        if (isCurrent) assert.equal(await cards.nth(index).locator(".instance-connect-button").innerText(), locale === "zh-cn" ? "查看模型" : "View model")
    }
}

try {
    for (const locale of ["zh-cn", "en-us"]) {
        const page = await createPage(locale)
        const center = page.locator(".connection-center")
        const desktopPanel = center.locator(".powerbi-panel")
        const header = page.locator(".header-actions")
        const disconnect = locale === "zh-cn" ? "断开连接" : "Disconnect"
        await expectCurrent(desktopPanel, -1, locale)
        for (const [width, height, collapsed] of [
            [1422, 800, false],
            [1422, 800, true],
            [1920, 1080, false],
        ]) {
            await page.setViewportSize({ width, height })
            if ((await page.locator(".shell.collapsed").count()) !== Number(collapsed)) await page.locator(".collapse-button").click()
            await desktopPanel.locator(".desktop-instance-actions").scrollIntoViewIfNeeded()
            await verifyLayout(page, desktopPanel, locale, `connections-${width}x${height}-${collapsed ? "collapsed" : "expanded"}`)
            for (const name of ["Power BI Service", "SSAS / XMLA", "Power BI Desktop"]) {
                await center.getByRole("tab", { name, exact: true }).click()
                await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
                await assertInlineConnections(page)
            }
            await desktopPanel.locator(".instance-card").nth(1).waitFor()
            await expectCurrent(desktopPanel, -1, locale)
        }
        await desktopPanel.locator(".desktop-file-button").click()
        await desktopPanel.locator(".instance-card.is-current").waitFor()
        await expectCurrent(desktopPanel, 0, locale)
        const beforeCurrentClick = await page.evaluate(() => window.desktopInstanceTest.calls.filter((call) => call.method === "project.connectLocal").length)
        await desktopPanel.locator(".instance-connect-button").first().dispatchEvent("click")
        assert.equal(
            await page.evaluate(() => window.desktopInstanceTest.calls.filter((call) => call.method === "project.connectLocal").length),
            beforeCurrentClick,
        )
        await page
            .getByRole("navigation")
            .getByRole("button", { name: locale === "zh-cn" ? "连接" : "Connections", exact: true })
            .click()
        await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
        await desktopPanel.locator(".instance-card").nth(1).waitFor()
        await desktopPanel.locator(".instance-connect-button").nth(1).click()
        // 当前旧卡片在请求返回前仍存在, 必须等待目标卡片实际接管身份后再断言.
        await page.waitForFunction(() => document.querySelectorAll(".powerbi-panel .instance-card")[1]?.classList.contains("is-current"))
        await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
        await expectCurrent(desktopPanel, 1, locale)
        await desktopPanel.locator(".instance-refresh-button").click()
        await desktopPanel.locator(".instance-card").nth(1).waitFor()
        await expectCurrent(desktopPanel, 1, locale)
        await header.getByRole("button", { name: disconnect, exact: true }).click()
        await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
        await expectCurrent(desktopPanel, -1, locale)
        rows.push({ locale, scenario: "文件连接、同名实例身份切换、刷新保留当前状态、断开清理", status: "PASS" })
        await desktopPanel.locator(".instance-card").nth(1).waitFor()
        await page.evaluate(() => (window.desktopInstanceTest.holdNext = "project.open"))
        await desktopPanel.locator(".desktop-file-button").click()
        await page.locator(".operation-message .operation-feedback").waitFor()
        assert.equal(await desktopPanel.locator(".desktop-file-button").isDisabled(), true)
        assert.equal(await desktopPanel.locator(".instance-refresh-button").isDisabled(), true)
        assert.equal(await page.getByRole("navigation").getByRole("button").first().isDisabled(), true)
        const cancel = page.locator(".operation-message").getByRole("button", { name: locale === "zh-cn" ? "取消操作" : "Cancel operation", exact: true })
        await cancel.click()
        assert.equal(await desktopPanel.locator(".desktop-file-button").isDisabled(), true)
        await page.evaluate(() => window.desktopInstanceTest.finish("CANCELLED"))
        await page.locator(".app-feedback-message").filter({ hasText: "模拟操作已取消" }).waitFor()
        assert.equal(await desktopPanel.locator(".desktop-file-button").isEnabled(), true)
        await assertInlineConnections(page)
        await expectCurrent(desktopPanel, -1, locale)
        rows.push({ locale, scenario: "连接页文件连接在途锁定、独立取消、终态恢复操作", status: "PASS" })
        assert.equal(await desktopPanel.locator(".udf-probe-button").count(), 0)
        await collectRuntimeErrors(page, issues)
        await page.close()
    }
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        evidence + "browser.json",
        JSON.stringify(
            {
                status: "PASS",
                platform: "Windows",
                browser: browser.version(),
                mode: "explicit mock with controlled same-name instance identities",
                realDesktop: "UNRUN",
                realWebView2: "UNRUN",
                rows,
                runtimeIssues: [...issues],
            },
            null,
            2,
        ),
    )
    console.log(`PASS ${rows.length} dual-language desktop-control scenarios; console/runtime clean; real Desktop/WebView2 UNRUN`)
} catch (error) {
    await writeFile(evidence + "browser-failure.json", JSON.stringify({ status: "FAIL", message: String(error), rows, runtimeIssues: [...issues] }, null, 2))
    throw error
} finally {
    await browser.close()
}
