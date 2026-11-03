/**
 * FilePath    : pbi-utils\web\tests\model-instance-cards.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 双语验收三种来源共享单行卡片、权威统计、模型查看、保存锁与桌面布局, 显式模拟不替代 Desktop/WebView2.
 */
/* oxlint-disable no-await-in-loop -- 同一模型的连接、来源切换、导航和窗口缩放必须顺序验证. */
import { chromium } from "playwright"
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const target = process.env.PBI_INSTANCE_TEST_URL || "http://127.0.0.1:5173/"
const evidence = fileURLToPath(new URL("../../artifacts/acceptance/261015-inline-centered/model-instance-cards/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const rows = []
const issues = new Map()
const sizes = [
    { width: 1422, height: 800, collapsed: false },
    { width: 1422, height: 800, collapsed: true },
    { width: 1920, height: 1080, collapsed: false },
    { width: 1920, height: 1080, collapsed: true },
]

/** 注入显式 mock 的同名不同身份实例和可控终态, 每次连接仍由实际 Vue/BridgeClient 调用. */
function installFixtures(MockTransport) {
    const originalPost = MockTransport.prototype.postMessage
    const originalSend = MockTransport.prototype.send
    const requests = new Map()
    const longTitle = "同名模型 / Original model name with a deliberately long unbroken suffix ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
    const state = {
        calls: [],
        holdNext: "",
        pending: null,
        pendingSave: false,
        finish(outcome = "success") {
            const pending = this.pending
            this.pending = null
            if (!pending) throw new Error("No controlled mock request")
            if (outcome === "success") dispatch(pending.transport, pending.message)
            else pending.transport.send({ protocolVersion: 1, id: pending.message.id, error: { code: outcome, message: "模拟操作已取消" } })
        },
    }
    window.modelInstanceTest = state
    MockTransport.prototype.send = function (payload) {
        const request = requests.get(payload.id)
        if (payload.result && request?.method === "ui.settings") payload.result.locale = window.modelInstanceLocale
        if (payload.result && request?.method === "powerbi.serviceModels") {
            payload.result = [
                { id: "preview-service-model", modelId: "db1", name: longTitle, workspace: "同名工作区", workspaceId: "one", unavailableReason: null },
                { id: "service-second", modelId: "db2", name: longTitle, workspace: "同名工作区", workspaceId: "one", unavailableReason: null },
                { id: "service-unavailable", name: "不可用目录模型", workspace: "同名工作区", workspaceId: "one", unavailableReason: "模拟目录不可用" },
            ]
        }
        if (payload.result?.model && /^project\.(open|inspect|connectLocal|connectRemote|connectService)$/.test(request?.method || "")) {
            const snapshot = payload.result
            snapshot.model.name = longTitle
            snapshot.summary = {
                compatibilityLevel: snapshot.connection.kind === "powerbi-service" ? null : 1702,
                tableCount: 17,
                measureCount: 23,
                reportPageCount: snapshot.connection.kind === "desktop" ? 9 : null,
            }
            if (request.method === "project.connectLocal") snapshot.connection.instanceId = `mock-desktop-instance-${request.params.processId}`
            if (snapshot.connection.kind === "powerbi-service") snapshot.connection.modelId = request.params.modelId || this.currentProject.connection.modelId
            if (snapshot.connection.kind === "ssas-xmla") snapshot.connection.instanceId = "mock-desktop-instance-1000"
            snapshot.hasPendingChanges = state.pendingSave
            snapshot.saveResult = state.pendingSave
                ? { modelSaved: true, fileSaved: false, desktopSynchronized: false, errorCode: "DESKTOP_FILE_SAVE_FAILED", message: "模拟文件保存未完成" }
                : null
            this.currentProject = structuredClone(snapshot)
        }
        originalSend.call(this, payload)
    }
    /** 对扫描只返回有界目录字段, 其他请求仍委托原 mock 并保持当前模型. */
    function dispatch(transport, message) {
        if (message.method === "powerbi.instances") {
            transport.send({
                protocolVersion: 1,
                id: message.id,
                result: {
                    timestamp: "2026-10-14T00:00:00Z",
                    instances: [1000, 2000].map((processId) => ({
                        processId,
                        instanceId: `mock-desktop-instance-${processId}`,
                        title: longTitle,
                        port: 50000 + processId,
                        status: "connected",
                        errorCode: null,
                        message: null,
                        models: [
                            {
                                name: longTitle,
                                compatibilityLevel: 1600,
                                tableCount: processId === 1000 ? 6 : 0,
                                measureCount: processId === 1000 ? 12 : 0,
                                summary: {
                                    compatibilityLevel: 1600,
                                    tableCount: processId === 1000 ? 6 : 0,
                                    measureCount: processId === 1000 ? 12 : null,
                                    reportPageCount: null,
                                },
                            },
                        ],
                    })),
                },
            })
        } else if (message.method === "project.connectService" && message.params.modelId === "service-second") {
            originalPost.call(transport, { ...message, params: { modelId: "preview-service-model" } })
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

/** 在文档加载前接入所有异常通道, 注入局部模拟数据后返回可操作页面. */
async function createPage(locale) {
    const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
    page.setDefaultTimeout(15000)
    await monitorRuntime(page, issues)
    await page.addInitScript((value) => {
        window.modelInstanceLocale = value
    }, locale)
    await page.route(/\/src\/bridge\/mock\.ts/, async (route) => {
        const response = await route.fetch()
        await route.fulfill({ response, body: (await response.text()) + `\n(${installFixtures.toString()})(MockTransport);` })
    })
    await page.goto(target, { waitUntil: "networkidle" })
    await page.locator(".instance-card").nth(1).waitFor()
    return page
}

/** 等待业务权威终态, 避免只等待取消确认或按钮出现就判断已恢复. */
async function settled(page) {
    await page.waitForFunction(() => document.querySelector(".content")?.getAttribute("aria-busy") === "false")
}

/** 固定窗口尺寸并合并完成中的 CSS 动画, 再检查单行卡片、统计区隔与模块间距. */
async function checkLayout(page, locale, source, size) {
    await page.setViewportSize({ width: size.width, height: size.height })
    if ((await page.locator(".shell.collapsed").count()) !== Number(size.collapsed)) await page.locator(".collapse-button").click()
    await page.locator(".connection-center").evaluate(async (element) => {
        await Promise.all(element.getAnimations({ subtree: true }).map((animation) => animation.finished.catch(() => undefined)))
    })
    await page.locator(".connection-center .instance-card").last().scrollIntoViewIfNeeded()
    const geometry = await page.evaluate(() => {
        // oxlint-disable-next-line unicorn/consistent-function-scoping -- 浏览器 evaluate 回调独立序列化, helper 需要处于相同作用域.
        const rect = (element) => {
            const box = element.getBoundingClientRect()
            return { top: box.top, bottom: box.bottom, left: box.left, right: box.right }
        }
        const center = document.querySelector(".connection-center")
        const recent = document.querySelector(".recent-files-panel")
        const sidebar = document.querySelector(".sidebar")
        return {
            width: innerWidth,
            documentWidth: document.documentElement.scrollWidth,
            centerWidth: center.clientWidth,
            centerScrollWidth: center.scrollWidth,
            sidebarHeight: sidebar.clientHeight,
            sidebarScrollHeight: sidebar.scrollHeight,
            moduleGap: recent ? rect(recent).top - rect(center).bottom : null,
            cards: [...center.querySelectorAll(".instance-card")].map((card) => ({
                width: card.clientWidth,
                scrollWidth: card.scrollWidth,
                row: rect(card.querySelector(".instance-heading")),
                action: rect(card.querySelector(".instance-connect-button")),
                title: rect(card.querySelector("h2")),
                summary: rect(card.querySelector(".instance-summary")),
                stats: [...card.querySelectorAll(".instance-stat")].map(rect),
                indicators: rect(card.querySelector(".instance-indicators")),
                separators: [...card.querySelectorAll(".instance-stat")].map((element) => getComputedStyle(element).borderLeftWidth),
            })),
        }
    })
    assert.ok(geometry.documentWidth <= geometry.width + 1, JSON.stringify(geometry))
    assert.ok(geometry.centerScrollWidth <= geometry.centerWidth + 1, JSON.stringify(geometry))
    assert.ok(geometry.sidebarScrollHeight <= geometry.sidebarHeight + 1, JSON.stringify(geometry))
    assert.ok(geometry.moduleGap === null || geometry.moduleGap >= 12, JSON.stringify(geometry))
    assert.ok(geometry.cards.length > 0)
    for (const card of geometry.cards) {
        assert.ok(card.scrollWidth <= card.width + 1, JSON.stringify(card))
        assert.ok(Math.abs(card.action.left - card.row.left) <= 1, JSON.stringify(card))
        assert.equal(card.stats.length, 4)
        assert.ok(
            card.stats.every((stat) => Math.abs(stat.top - card.stats[0].top) <= 1),
            JSON.stringify(card),
        )
        assert.ok(
            card.stats.slice(1).every((stat, index) => stat.left >= card.stats[index].right),
            JSON.stringify(card),
        )
        assert.ok(
            [card.action, card.title, card.summary, card.indicators].every((box) => box.top >= card.row.top - 1 && box.bottom <= card.row.bottom + 1),
            JSON.stringify(card),
        )
        assert.ok(card.summary.left >= card.title.right - 1, JSON.stringify(card))
        assert.ok(card.summary.right <= card.indicators.left + 1, JSON.stringify(card))
    }
    assert.equal(await page.locator(".hero, .stats-grid").count(), 0)
    await assertInlineConnections(page)
    const scenario = `${source}-${size.width}x${size.height}-${size.collapsed ? "collapsed" : "expanded"}`
    await page.screenshot({ path: evidence + `${locale}-${scenario}.png` })
    rows.push({ locale, scenario, status: "PASS", geometry })
}

/** 当前卡片只导航到模型页, 重回连接页后保持宿主连接, 不增加任何连接请求. */
async function checkView(page, locale, source) {
    const before = await page.evaluate(
        () => window.modelInstanceTest.calls.filter((call) => /^project\.(open|connectLocal|connectRemote|connectService)$/.test(call.method)).length,
    )
    await page.locator(".instance-card.is-current .instance-connect-button").click()
    await page
        .getByRole("navigation")
        .getByRole("button", { name: locale === "zh-cn" ? "模型" : "Model", exact: true })
        .waitFor()
    assert.equal(
        await page
            .getByRole("navigation")
            .getByRole("button", { name: locale === "zh-cn" ? "模型" : "Model", exact: true })
            .getAttribute("aria-current"),
        "page",
    )
    assert.equal(
        await page.evaluate(
            () => window.modelInstanceTest.calls.filter((call) => /^project\.(open|connectLocal|connectRemote|connectService)$/.test(call.method)).length,
        ),
        before,
    )
    await page
        .getByRole("navigation")
        .getByRole("button", { name: locale === "zh-cn" ? "连接" : "Connections", exact: true })
        .click()
    await settled(page)
    rows.push({ locale, scenario: `${source}-view-without-reconnect`, status: "PASS" })
}

try {
    for (const locale of ["zh-cn", "en-us"]) {
        const page = await createPage(locale)
        const center = page.locator(".connection-center")
        const header = page.locator(".header-actions")
        const unknown = "-"
        const view = locale === "zh-cn" ? "查看模型" : "View model"
        const desktopCards = center.locator(".powerbi-panel .instance-card")
        assert.equal(await desktopCards.nth(1).locator('[data-stat="tableCount"] dd').innerText(), "0")
        assert.equal(await desktopCards.nth(1).locator('[data-stat="measureCount"] dd').innerText(), unknown)
        await center.locator(".desktop-file-button").click()
        await settled(page)
        const current = center.locator(".instance-card.is-current")
        assert.equal(await current.locator(".instance-connect-button").innerText(), view)
        assert.deepEqual(await current.locator(".instance-stat dd").allTextContents(), ["1702", "17", "23", "9"])
        assert.deepEqual(await desktopCards.nth(1).locator(".instance-stat dd").allTextContents(), ["1600", "0", unknown, "0"])
        for (const size of sizes) await checkLayout(page, locale, "desktop", size)
        await checkView(page, locale, "desktop")
        await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
        await center.getByRole("textbox", { name: locale === "zh-cn" ? "工作或学校邮箱" : "Work or school email", exact: true }).fill("preview@example.com")
        const signIn = center.getByRole("button", { name: locale === "zh-cn" ? "登录" : "Sign in", exact: true })
        await signIn.click()
        await center.getByRole("combobox", { name: locale === "zh-cn" ? "登录环境" : "Sign-in environment", exact: true }).press("Enter")
        await page.getByRole("option", { name: locale === "zh-cn" ? "全球版" : "Global cloud", exact: true }).click()
        await signIn.click()
        await settled(page)
        await center.locator(".el-select").last().click()
        await page.getByRole("option", { name: / · db1$/, exact: false }).click()
        const serviceCard = center.locator(".instance-card").first()
        assert.deepEqual(await serviceCard.locator(".instance-stat dd").allTextContents(), [unknown, unknown, unknown, "0"])
        await serviceCard.locator(".instance-connect-button").click()
        await settled(page)
        assert.equal(await center.locator(".instance-card.is-current .instance-connect-button").innerText(), view)
        await center.locator(".el-select").last().click()
        await page.getByRole("option", { name: / · db2$/, exact: false }).click()
        assert.equal(await center.locator(".instance-card").count(), 2)
        assert.equal(await center.locator(".current-model-card .instance-connect-button").innerText(), view)
        assert.equal(await center.locator(".instance-card:not(.is-current) [data-stat=tableCount] dd").innerText(), unknown)
        for (const size of sizes) await checkLayout(page, locale, "service", size)
        await checkView(page, locale, "service")
        await center.getByRole("tab", { name: "Power BI Desktop", exact: true }).click()
        await settled(page)
        assert.equal(await center.locator(".instance-card.is-current").count(), 0)
        await center.getByRole("tab", { name: "SSAS / XMLA", exact: true }).click()
        await center.locator(".remote-connection[aria-busy=false]").waitFor()
        await center.getByRole("textbox", { name: locale === "zh-cn" ? "服务器" : "Server", exact: true }).fill("ssas.example")
        await center.getByRole("textbox", { name: locale === "zh-cn" ? "数据库 / 模型" : "Database / model", exact: true }).fill("demo")
        await center.getByRole("button", { name: locale === "zh-cn" ? "连接" : "Connect", exact: true }).click()
        await settled(page)
        assert.deepEqual(await center.locator(".current-model-card .instance-stat dd").allTextContents(), ["1702", "17", "23", "0"])
        for (const size of sizes) await checkLayout(page, locale, "ssas", size)
        await checkView(page, locale, "ssas")
        await center.getByRole("tab", { name: "Power BI Desktop", exact: true }).click()
        await settled(page)
        assert.equal(await center.locator(".instance-card.is-current").count(), 0)
        await center.getByRole("tab", { name: "SSAS / XMLA", exact: true }).click()
        await settled(page)
        assert.equal(await page.locator(".recent-projects").count(), 0)
        await center.getByRole("tab", { name: "Power BI Desktop", exact: true }).click()
        await settled(page)
        await page.locator(".recent-projects button").first().click()
        await settled(page)
        assert.equal(await center.getByRole("tab", { name: "Power BI Desktop", exact: true }).getAttribute("aria-selected"), "true")
        await center.locator(".instance-card.is-current .instance-connect-button").waitFor()
        assert.equal(await center.locator(".instance-card.is-current .instance-connect-button").innerText(), view)
        rows.push({ locale, scenario: "SSAS不展示最近记录，切回Desktop重开文件后补扫当前卡片", status: "PASS" })
        await page.evaluate(() => (window.modelInstanceTest.holdNext = "project.open"))
        await center.locator(".desktop-file-button").click()
        const cancel = page.getByRole("button", { name: locale === "zh-cn" ? "取消操作" : "Cancel operation", exact: true })
        await cancel.click()
        assert.equal(await center.locator(".instance-card.is-current .instance-connect-button").isDisabled(), true)
        assert.equal(await cancel.isDisabled(), true)
        assert.ok((await center.getByRole("tab").all()).length === 3)
        assert.equal(await page.getByRole("navigation").getByRole("button").first().isDisabled(), true)
        await page.evaluate(() => window.modelInstanceTest.finish("CANCELLED"))
        await settled(page)
        assert.equal(await center.locator(".instance-card.is-current .instance-connect-button").isEnabled(), true)
        rows.push({ locale, scenario: "取消确认后锁定当前查看和来源直到权威终态", status: "PASS" })
        await page.evaluate(() => (window.modelInstanceTest.pendingSave = true))
        await header.getByRole("button", { name: locale === "zh-cn" ? "刷新模型" : "Refresh model", exact: true }).click()
        await settled(page)
        assert.equal(await center.locator(".instance-card.is-current .instance-connect-button").isDisabled(), true)
        assert.equal(await center.locator(".desktop-file-button").isDisabled(), true)
        assert.equal(await center.locator(".instance-refresh-button").isDisabled(), true)
        const before = await page.evaluate(() => window.modelInstanceTest.calls.length)
        await center.locator(".instance-card.is-current .instance-connect-button").dispatchEvent("click")
        assert.equal(await page.evaluate(() => window.modelInstanceTest.calls.length), before)
        assert.equal(
            await page
                .getByRole("navigation")
                .getByRole("button", { name: locale === "zh-cn" ? "连接" : "Connections", exact: true })
                .getAttribute("aria-current"),
            "page",
        )
        rows.push({ locale, scenario: "保存未完锁定模型查看和新连接且保留权威统计", status: "PASS" })
        await collectRuntimeErrors(page, issues)
        await page.close()
    }
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        evidence + "browser.json",
        JSON.stringify(
            {
                status: "PASS",
                browser: browser.version(),
                mode: "explicit controlled mock",
                realDesktop: "UNRUN",
                realWebView2: "UNRUN",
                realService: "UNRUN",
                realSsas: "UNRUN",
                rows,
                runtimeIssues: [...issues],
            },
            null,
            2,
        ),
    )
    console.log(`PASS ${rows.length} model-card scenarios; console/runtime clean; real Desktop/WebView2/Service/SSAS UNRUN`)
} catch (error) {
    await writeFile(evidence + "browser-failure.json", JSON.stringify({ status: "FAIL", message: String(error), rows, runtimeIssues: [...issues] }, null, 2))
    throw error
} finally {
    await browser.close()
}
