/**
 * FilePath    : pbi-utils\web\tests\connection-loading.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 用可控模拟桥接验证远端连接动画、阶段、取消和连接页连接布局, 不请求真实服务.
 */
/* oxlint-disable no-await-in-loop -- 同一连接的阶段和桌面尺寸按顺序验收. */
import { chromium } from "playwright"
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const evidence = fileURLToPath(new URL("../../artifacts/acceptance/261015-inline-centered/connection-loading/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const rows = []
const issues = new Map()
/** 仅在验收页面拦截连接请求; 手动触发阶段和终态, 不修改产品的模拟响应时序. */
function installHold(MockTransport) {
    const original = MockTransport.prototype.postMessage
    window.loadingTest = {
        pending: null,
        phase() {
            const { transport, message } = this.pending
            transport.send({ protocolVersion: 1, requestPhase: { requestId: message.id, phase: "connecting" } })
        },
        complete(outcome) {
            const { transport, message } = this.pending
            this.pending = null
            if (outcome === "success") original.call(transport, message)
            else
                transport.send({
                    protocolVersion: 1,
                    id: message.id,
                    error: { code: outcome, message: outcome === "CANCELLED" ? "模拟操作已取消" : "模拟连接失败" },
                })
        },
    }
    MockTransport.prototype.postMessage = function (message) {
        if (["project.connectRemote", "project.connectService"].includes(message.method)) {
            window.loadingTest.pending = { transport: this, message }
            return
        }
        if (message.method === "request.cancel" && message.params.requestId === window.loadingTest.pending?.message.id) {
            this.send({ protocolVersion: 1, id: message.id, result: { accepted: true } })
            return
        }
        original.call(this, message)
    }
}
/** 验证提示、持续旋转动画及取消入口可见; 保存各桌面尺寸的截图与几何证据. */
async function verify(page, center, button, text, name, spinner = true) {
    await page.locator(".operation-feedback").filter({ hasText: text }).waitFor()
    assert.equal(await button.isDisabled(), true)
    let animation = null
    if (spinner) {
        assert.equal(await button.evaluate((element) => element.classList.contains("is-loading")), true)
        animation = await button.locator(".el-icon.is-loading").evaluate((element) => {
            const style = getComputedStyle(element)
            return { name: style.animationName, duration: style.animationDuration, iterations: style.animationIterationCount }
        })
        assert.notEqual(animation.name, "none")
        assert.equal(animation.iterations, "infinite")
    } else assert.equal(await button.innerText(), "连接所选模型")
    const cancel = page.getByRole("button", { name: "取消操作", exact: true })
    assert.equal(await cancel.isEnabled(), true)
    assert.equal(await center.getByRole("button", { name: "关闭", exact: true }).count(), 0)
    assert.equal(await page.getByRole("dialog").count(), 0)
    assert.deepEqual(await center.getByRole("tab").evaluateAll((tabs) => tabs.map((tab) => tab.classList.contains("is-disabled"))), [true, true, true])
    const activeSource = await center.getByRole("tab", { selected: true }).innerText()
    await center.getByRole("tab", { name: "Power BI Desktop", exact: true }).click({ force: true })
    assert.equal(await center.getByRole("tab", { selected: true }).innerText(), activeSource, "在途请求期间强制点击其他来源也不能切换")
    await cancel.scrollIntoViewIfNeeded()
    const box = await cancel.boundingBox()
    assert.ok(box && box.y >= 0 && box.y + box.height <= page.viewportSize().height)
    const layout = await center.evaluate((element) => ({
        width: element.clientWidth,
        scrollWidth: element.scrollWidth,
        bottom: element.getBoundingClientRect().bottom,
    }))
    assert.ok(layout.scrollWidth <= layout.width + 1)
    await page.screenshot({ path: evidence + name + ".png" })
    rows.push({ scenario: name, status: "PASS", animation, layout })
}
try {
    const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
    page.setDefaultTimeout(12000)
    await monitorRuntime(page, issues)
    await page.route(/\/src\/bridge\/mock\.ts/, async (route) => {
        const response = await route.fetch()
        await route.fulfill({ response, body: (await response.text()) + `\n(${installHold.toString()})(MockTransport);` })
    })
    await page.goto("http://127.0.0.1:5173/", { waitUntil: "networkidle" })
    const header = page.locator(".header-actions")
    await assertInlineConnections(page)
    const center = page.locator(".connection-center")
    await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
    await center.getByRole("textbox", { name: "工作或学校邮箱", exact: true }).fill("preview@example.com")
    await center.getByRole("button", { name: "登录", exact: true }).click()
    await center.getByRole("combobox", { name: "登录环境", exact: true }).press("Enter")
    await page.getByRole("option", { name: "全球版", exact: true }).click()
    await center.getByRole("button", { name: "登录", exact: true }).click()
    await center.getByText("preview@example.com (模拟)", { exact: false }).waitFor()
    await center.getByRole("combobox", { name: "语义模型", exact: true }).press("Enter")
    await page.getByRole("option", { name: "销售模型 (模拟)", exact: true }).click()
    const service = center.getByRole("button", { name: "连接所选模型", exact: true })
    await service.click()
    for (const [width, height] of [
        [1422, 800],
        [1920, 1080],
        [1422, 800],
    ]) {
        await page.setViewportSize({ width, height })
        await verify(page, center, service, "正在连接 Power BI Service 模型", `service-loading-${width}x${height}`, false)
    }
    assert.doesNotMatch(await page.locator(".operation-feedback").innerText(), /系统浏览器/)
    await page.getByRole("button", { name: "取消操作", exact: true }).click()
    assert.equal(await service.isDisabled(), true)
    await page.evaluate(() => window.loadingTest.complete("CANCELLED"))
    await page.getByText("模拟操作已取消", { exact: true }).waitFor()
    assert.equal(await service.isEnabled(), true)
    await service.click()
    await page.evaluate(() => window.loadingTest.complete("success"))
    await page.waitForFunction(() => document.querySelector(".content").getAttribute("aria-busy") === "false")
    assert.match(await header.innerText(), /Power BI Service · 只读/)
    rows.push({ scenario: "Service 取消清理及重试成功", status: "PASS" })
    await center.getByRole("tab", { name: "SSAS / XMLA", exact: true }).click()
    await center.locator(".remote-connection[aria-busy=false]").waitFor()
    assert.equal(await page.getByRole("dialog").count(), 0)
    await center.getByRole("textbox", { name: "服务器", exact: true }).fill("ssas.example")
    await center.getByRole("textbox", { name: "数据库 / 模型", exact: true }).fill("demo")
    const server = center.getByRole("button", { name: "连接", exact: true })
    assert.equal(await page.locator(".operation-feedback").count(), 0)
    await server.click()
    await verify(page, center, server, "正在连接 SSAS / XMLA 模型", "ssas-connecting-1422x800")
    await page.evaluate(() => window.loadingTest.complete("CONNECTION_FAILED"))
    await center.getByText("模拟连接失败", { exact: true }).waitFor()
    assert.equal(await server.isEnabled(), true)
    assert.match(await header.innerText(), /Power BI Service · 只读/)
    await server.click()
    await verify(page, center, server, "正在连接 SSAS / XMLA 模型", "ssas-retry-connecting-1422x800")
    await page.getByRole("button", { name: "取消操作", exact: true }).click()
    await page.evaluate(() => window.loadingTest.complete("CANCELLED"))
    await page.getByText("模拟操作已取消", { exact: true }).waitFor()
    assert.equal(await server.isEnabled(), true)
    rows.push({ scenario: "SSAS 平铺填写与失败保留当前模型及连接取消", status: "PASS" })
    await assertInlineConnections(page)
    await collectRuntimeErrors(page, issues)
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        evidence + "browser.json",
        JSON.stringify(
            {
                status: "PASS",
                platform: "Windows",
                browser: browser.version(),
                mode: "explicit mock with controlled responses",
                realService: "UNRUN",
                realSsasXmla: "UNRUN",
                rows,
                consoleIssues: [...issues],
            },
            null,
            2,
        ),
    )
    console.log(`PASS ${rows.length} loading/stage/viewport scenarios; console clean; real connections UNRUN`)
} catch (error) {
    await writeFile(evidence + "browser-failure.json", JSON.stringify({ status: "FAIL", message: String(error), rows, consoleIssues: [...issues] }, null, 2))
    throw error
} finally {
    await browser.close()
}
