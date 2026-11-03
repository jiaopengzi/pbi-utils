/**
 * FilePath    : pbi-utils\web\tests\udf-lint-acceptance.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式浏览器模拟的双语 UDF lint 预览与取消, 从文档开始采集异常, 不写真实模型.
 */
/* oxlint-disable no-await-in-loop -- 同页的模态预览、取消和窗口缩放按顺序验收. */
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { chromium } from "playwright"
import { collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const evidence = fileURLToPath(new URL("../../.docs/261020-04/evidence/udf-lint/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const issues = new Map(),
    rows = []

/** 只在浏览器验收模块中注入受控只读候选, 生产 mock 仍保守返回 incomplete; 所有模型写入只计数并拒绝. */
function installLintMock(MockTransport, locale) {
    const original = MockTransport.prototype.postMessage
    const calls = (window.udfLintAcceptanceCalls = [])
    MockTransport.prototype.postMessage = function (message) {
        calls.push(message.method)
        const reply = (result) => this.send({ protocolVersion: 1, id: message.id, result })
        if (message.method === "ui.settings") return reply({ locale, showRecentFiles: true, isSimulation: true })
        if (message.method === "model.udfLint") {
            const project = this.workspace.currentProject
            return reply({
                projectId: project.projectId,
                baselineId: project.baselineId,
                unused: project.model.functions.map((item) => item.name),
                totalCount: project.model.functions.length,
                complete: true,
                isSimulation: true,
            })
        }
        if (message.method === "model.edit")
            return this.send({ protocolVersion: 1, id: message.id, error: { code: "ACCEPTANCE_WRITE_BLOCKED", message: "Acceptance does not write models" } })
        return original.call(this, message)
    }
}

try {
    for (const locale of ["zh-cn", "en-us"]) {
        const english = locale === "en-us"
        const page = await browser.newPage({ viewport: { width: 1422, height: 800 } })
        page.setDefaultTimeout(15000)
        await monitorRuntime(page, issues)
        await page.route(/\/src\/bridge\/mock\.ts/, async (route) => {
            const response = await route.fetch()
            await route.fulfill({ response, body: (await response.text()) + `\n(${installLintMock.toString()})(MockTransport, ${JSON.stringify(locale)});` })
        })
        await page.goto(process.env.PBI_BROWSER_URL || "http://127.0.0.1:5173/", { waitUntil: "networkidle" })
        await page.locator(".connection-center .desktop-file-button").click()
        await page.locator(".connection-center .instance-card.is-current").waitFor()
        await page
            .getByRole("navigation")
            .getByRole("button", { name: english ? "Model" : "模型", exact: true })
            .click()
        await page.getByRole("tab", { name: english ? "UDF functions" : "UDF 函数", exact: true }).click()
        for (const width of [1422, 1920]) {
            await page.setViewportSize({ width, height: 800 })
            const analyze = page.getByRole("button", { name: english ? "Lint · Clean unused UDFs" : "Lint · 清理未使用 UDF", exact: true })
            await analyze.click()
            const dialog = page.getByRole("dialog", { name: english ? "Review unused UDFs" : "预览未使用的 UDF", exact: true })
            await dialog.waitFor()
            assert.match(await dialog.innerText(), /Demo\.Double/)
            const confirm = dialog.getByRole("button", { name: english ? "Delete and save" : "确认删除并保存", exact: true })
            assert.equal(await confirm.isEnabled(), true)
            await page.waitForFunction(() => {
                const box = document.querySelector(".feature-dialog")?.getBoundingClientRect()
                return box && Math.abs(box.x + box.width / 2 - innerWidth / 2) <= 2 && Math.abs(box.y + box.height / 2 - innerHeight / 2) <= 2
            })
            await dialog.evaluate(async (element) => {
                await Promise.all(
                    element
                        .closest(".el-overlay")
                        .getAnimations({ subtree: true })
                        .map((animation) => animation.finished.catch(() => undefined)),
                )
            })
            const box = await dialog.locator(".el-dialog").boundingBox()
            assert.ok(box && Math.abs(box.x + box.width / 2 - width / 2) <= 2 && Math.abs(box.y + box.height / 2 - 400) <= 2, JSON.stringify(box))
            await page.screenshot({ path: `${evidence}${locale}-${width}-preview.png` })
            await dialog.getByRole("button", { name: english ? "Cancel" : "取消", exact: true }).click()
            await dialog.waitFor({ state: "hidden" })
            assert.equal(await page.evaluate(() => window.udfLintAcceptanceCalls.filter((method) => method === "model.edit").length), 0)
            rows.push({ locale, width, height: 800, status: "PASS", previewNames: ["Demo.Double"], cancelledWithoutWrite: true, box })
        }
        await collectRuntimeErrors(page, issues)
        await page.close()
    }
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        `${evidence}report.json`,
        JSON.stringify(
            {
                mode: "explicit browser mock with injected read-only lint result",
                status: "PASS",
                rows,
                issues: [...issues],
                realDesktop: "UNRUN",
                realWebView2: "UNRUN",
            },
            null,
            2,
        ),
    )
    console.log(`PASS ${rows.length} bilingual UDF lint preview/cancel scenarios; console clean; real Desktop/WebView2 UNRUN`)
} finally {
    await browser.close()
}
