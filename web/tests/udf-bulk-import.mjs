/**
 * FilePath    : pbi-utils\web\tests\udf-bulk-import.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 双语桌面尺寸下核验 UDF 来源对齐及全量预览确认, 仅使用显式浏览器模拟.
 */
import assert from "node:assert/strict"
import { chromium } from "playwright"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const target = process.env.PBI_UDF_TEST_URL || "http://127.0.0.1:5173/"
const evidence = fileURLToPath(new URL("../../.docs/261020-04/evidence/udf-bulk/", import.meta.url))
const issues = new Map(),
    rows = []

/** 受控协议响应只替代来源数据, 页面、目录动作和真实组件仍由产品代码执行. */
function installFixtures(MockTransport) {
    const post = MockTransport.prototype.postMessage,
        send = MockTransport.prototype.send,
        requests = new Map()
    window.udfBulkCalls = []
    MockTransport.prototype.send = function (payload) {
        if (requests.get(payload.id)?.method === "ui.settings" && payload.result) payload.result.locale = window.udfBulkLocale
        send.call(this, payload)
    }
    MockTransport.prototype.postMessage = function (message) {
        requests.set(message.id, message)
        window.udfBulkCalls.push(message)
        if (message.method !== "udf.catalog") return post.call(this, message)
        const { action, packageId, version } = message.params
        const packages = ["Example.One", "Example.Two"]
        const result =
            action === "search"
                ? { packages: packages.map((id) => ({ id, version: "1.0.0", description: "Browser simulation" })) }
                : action === "versions"
                  ? { versions: ["1.9.0", "2.0.0-beta", "1.10.0"] }
                  : {
                        package: {
                            id: packageId,
                            version,
                            hash: "c".repeat(64),
                            description: "Browser simulation",
                            readme: "",
                            licenseFiles: { LICENSE: "Original package permission (simulation)" },
                            dependencies: [{ id: "Example.Dependency", version: "1.0.0" }],
                            functions: ["First", "Second"].map((name) => ({
                                name: `${packageId}.${name}`,
                                expression: "() => 1",
                                description: "",
                                annotations: {},
                            })),
                            retrievedAt: "2026-10-20T00:00:00Z",
                            fromCache: false,
                        },
                    }
        return this.send({ protocolVersion: 1, id: message.id, result })
    }
}

/** 每个场景从文档开始采集运行时异常, 不创建宿主、真实网络下载或用户数据. */
async function scenario(browser, locale, width, collapsed) {
    const page = await browser.newPage({ viewport: { width, height: 800 } })
    const english = locale === "en-us",
        key = `${locale}-${width}-${collapsed ? "collapsed" : "expanded"}`
    page.setDefaultTimeout(15000)
    await monitorRuntime(page, issues)
    await page.addInitScript((value) => {
        window.udfBulkLocale = value
    }, locale)
    await page.route(/\/src\/bridge\/mock\.ts/, async (route) => {
        const response = await route.fetch()
        await route.fulfill({ response, body: (await response.text()) + `\n(${installFixtures.toString()})(MockTransport);` })
    })
    try {
        await page.goto(target, { waitUntil: "networkidle" })
        await page
            .getByRole("navigation")
            .getByRole("button", { name: english ? "New" : "新建", exact: true })
            .click()
        await page.locator('[data-step="scaffold-udfs"]').click()
        const section = page.locator("#section-scaffold-udfs")
        await section.locator("[data-optional]").click()
        const root = section.locator(".udf-step")
        await root.locator('[data-test="udf-source"] .el-select__wrapper').waitFor()
        if (collapsed) await page.getByRole("button", { name: english ? "Collapse sidebar" : "折叠侧栏", exact: true }).click()
        const geometry = await root.evaluate((element) => {
            const picker = element.querySelector(".udf-source-picker"),
                dropdown = picker.querySelector(".el-select__wrapper")
            const text = [...picker.childNodes].find((node) => node.nodeType === Node.TEXT_NODE && node.textContent.trim())
            const range = document.createRange()
            range.selectNode(text)
            const label = range.getBoundingClientRect(),
                select = dropdown.getBoundingClientRect()
            return {
                labelCenter: label.top + label.height / 2,
                selectCenter: select.top + select.height / 2,
                controlCenters: [...element.querySelectorAll(".udf-source-toolbar > .el-button")].map((button) => {
                    const box = button.getBoundingClientRect()
                    return box.top + box.height / 2
                }),
                documentWidth: document.documentElement.scrollWidth,
                viewportWidth: innerWidth,
            }
        })
        assert.ok(Math.abs(geometry.labelCenter - geometry.selectCenter) <= 3, JSON.stringify(geometry))
        assert.ok(
            geometry.controlCenters.every((center) => Math.abs(center - geometry.selectCenter) <= 1),
            JSON.stringify(geometry),
        )
        assert.ok(geometry.documentWidth <= width, JSON.stringify(geometry))
        await page.screenshot({ path: `${evidence}/${key}-layout.png` })
        await root.locator(".udf-search .el-input__inner").fill("filtered text")
        await root.getByRole("button", { name: english ? "Import all" : "全量导入", exact: true }).click()
        const dialog = page.getByRole("dialog", { name: english ? "Import all" : "全量导入", exact: true })
        const confirm = dialog.getByRole("button", { name: english ? "Confirm import all" : "确认全量导入", exact: true })
        await confirm.waitFor()
        await page.waitForFunction(() => document.querySelectorAll(".udf-bulk-package").length === 2)
        assert.equal(await root.locator(".udf-selected .scaffold-selection").count(), 0)
        await dialog.locator("summary").first().click()
        assert.ok((await dialog.textContent()).includes("Original package permission (simulation)"))
        const bounds = await dialog.locator(".el-dialog").boundingBox()
        assert.ok(bounds && Math.abs(bounds.x + bounds.width / 2 - width / 2) <= 2 && Math.abs(bounds.y + bounds.height / 2 - 400) <= 2, JSON.stringify(bounds))
        await page.screenshot({ path: `${evidence}/${key}-preview.png` })
        await confirm.click()
        await dialog.waitFor({ state: "hidden" })
        assert.equal(await root.locator(".udf-selected .scaffold-selection").count(), 2)
        const calls = await page.evaluate(() => window.udfBulkCalls.filter((item) => item.method === "udf.catalog"))
        assert.deepEqual(calls[0].params, { action: "search", source: "daxlib", query: "", includePrerelease: false })
        assert.deepEqual(
            calls.filter((item) => item.params.action === "package").map((item) => item.params.version),
            ["1.10.0", "1.10.0"],
        )
        await collectRuntimeErrors(page, issues)
        rows.push({ locale, width, height: 800, collapsed, geometry, dialog: bounds, state: "PASS", isSimulation: true })
    } finally {
        await page.close()
    }
}

await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
try {
    const scenarios = ["zh-cn", "en-us"].flatMap((locale) => [1422, 1920].flatMap((width) => [false, true].map((collapsed) => ({ locale, width, collapsed }))))
    await scenarios.reduce(async (pending, item) => {
        await pending
        await scenario(browser, item.locale, item.width, item.collapsed)
    }, Promise.resolve())
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(`${evidence}/report.json`, JSON.stringify({ rows, issues: [...issues], realWebView2: "UNRUN", realModelWrite: "UNRUN" }, null, 2))
    console.log(`PASS ${rows.length} UDF layout/import scenarios; explicit browser simulation; real WebView2/model writes UNRUN`)
} finally {
    await browser.close()
}
