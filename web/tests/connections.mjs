/**
 * FilePath    : pbi-utils\web\tests\connections.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 以可控模拟验证连接页文件加载、云登录阶段、可搜索目录和按来源隔离的最近连接.
 */
/* oxlint-disable no-await-in-loop -- 同一账号的登录、权限变化和窗口尺寸必须按顺序验收. */
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"
import { chromium } from "playwright"
import { assertInlineConnections, collectRuntimeErrors, monitorRuntime } from "./browser-acceptance.mjs"

const target = process.env.PBI_BROWSER_URL || "http://127.0.0.1:5173/"
const evidence = fileURLToPath(new URL("../../.docs/evidence/connections/", import.meta.url))
await mkdir(evidence, { recursive: true })
const browser = await chromium.launch({ headless: true })
const rows = []
const issues = new Map()
let activePage

/** 仅修改验收页面的模拟桥接, 用门闩暴露真实阶段边界, 不请求 Microsoft 或接触用户文件. */
function installControlledMock(MockTransport, locale) {
    const original = MockTransport.prototype.postMessage
    const models = [
        { id: "model-a", modelId: "dataset-a", workspaceId: "north", workspace: "North workspace", name: "Sales North", unavailableReason: null },
        { id: "model-b", modelId: "dataset-b", workspaceId: "north", workspace: "North workspace", name: "Inventory North", unavailableReason: null },
        { id: "model-c", modelId: "dataset-c", workspaceId: "south", workspace: "South workspace", name: "Revenue South", unavailableReason: null },
    ]
    const logins = [
        {
            id: "saved-login",
            email: "saved@example.com",
            environmentName: "ChinaCloud",
            environmentLabel: "世纪互联 (中国)",
            lastUsedAt: "2026-10-15T00:00:00Z",
        },
    ]
    const recents = new Map([
        [
            "saved@example.com/ChinaCloud",
            [{ id: "cloud-recent-a", modelId: "model-a", name: "Sales North", workspace: "North workspace", lastUsedAt: "2026-10-15T00:00:00Z" }],
        ],
    ])
    const control = (window.connectionsTest = {
        calls: [],
        pending: null,
        account: null,
        instances: [],
        desktopRevision: 0,
        revoked: [],
        failCatalogOnce: false,
        phase(phase) {
            const { transport, message } = this.pending
            transport.send({ protocolVersion: 1, requestPhase: { requestId: message.id, phase } })
        },
        discovery() {
            const { transport, message } = this.pending
            if (message.params.email.includes("multi")) {
                this.pending = null
                transport.send({
                    protocolVersion: 1,
                    id: message.id,
                    result: {
                        account: null,
                        recentLogins: logins,
                        isSimulation: true,
                        environments: [
                            { name: "GlobalCloud", displayName: "全球版" },
                            { name: "ChinaCloud", displayName: "世纪互联 (中国)" },
                        ],
                    },
                })
            } else this.phase("authenticating")
        },
        complete(outcome = "success") {
            const { transport, message } = this.pending
            this.pending = null
            if (outcome !== "success") {
                transport.send({ protocolVersion: 1, id: message.id, error: { code: outcome, message: "模拟操作已取消" } })
                return
            }
            if (message.method === "powerbi.serviceSignIn") {
                const environmentName =
                    message.params.environmentName || logins.find((item) => item.email === message.params.email)?.environmentName || "GlobalCloud"
                this.account = {
                    username: message.params.email,
                    environmentName,
                    environmentLabel: environmentName === "ChinaCloud" ? "世纪互联 (中国)" : "全球版",
                }
                if (message.params.remember && !logins.some((item) => item.email === message.params.email && item.environmentName === environmentName))
                    logins.push({
                        id: `${message.params.email}/${environmentName}`,
                        email: message.params.email,
                        environmentName,
                        environmentLabel: this.account.environmentLabel,
                        lastUsedAt: "2026-10-15T00:00:00Z",
                    })
                transport.send({
                    protocolVersion: 1,
                    id: message.id,
                    result: { account: this.account, recentLogins: logins, isSimulation: true, environments: [] },
                })
                return
            }
            this.desktopRevision++
            const instanceId = `opened-desktop-${this.desktopRevision}`
            const title = `新打开 PBIX ${this.desktopRevision} (模拟)`
            this.instances = [
                {
                    processId: 4000 + this.desktopRevision,
                    instanceId,
                    title,
                    port: 54000 + this.desktopRevision,
                    status: "connected",
                    message: null,
                    errorCode: null,
                    models: [{ name: title, compatibilityLevel: 1702, tableCount: 6, measureCount: 12 }],
                },
            ]
            transport.currentProject = {
                ...structuredClone(transport.currentProject),
                projectId: instanceId,
                projectPath: `model://${instanceId}`,
                modelFormat: "Power BI Desktop",
                connection: { kind: "desktop", label: "Power BI Desktop", instanceId },
            }
            transport.send({ protocolVersion: 1, id: message.id, result: structuredClone(transport.currentProject) })
        },
    })
    MockTransport.prototype.postMessage = function (message) {
        control.calls.push({ method: message.method, params: structuredClone(message.params) })
        const reply = (result) => this.send({ protocolVersion: 1, id: message.id, result: structuredClone(result) })
        if (message.method === "ui.settings") this.uiSettings.locale = locale
        if (message.method === "project.recent")
            return reply([{ id: "local-pbix", path: "C:/mock/local-report.pbix", name: "本地 PBIX 记录 (模拟)", openedAt: "2026-10-15T00:00:00Z" }])
        if (message.method === "powerbi.instances") return reply({ timestamp: "2026-10-15T00:00:00Z", instances: control.instances })
        if (["project.open", "project.openRecent", "powerbi.serviceSignIn"].includes(message.method)) {
            control.pending = { transport: this, message }
            if (message.method === "powerbi.serviceSignIn") control.phase(message.params.environmentName ? "authenticating" : "discoveringEnvironment")
            return
        }
        if (message.method === "request.cancel" && message.params.requestId === control.pending?.message.id) return reply({ accepted: true })
        if (message.method === "powerbi.serviceStatus" || message.method === "powerbi.serviceSignOut") {
            if (message.method === "powerbi.serviceSignOut") control.account = null
            return reply({ account: control.account, recentLogins: logins, isSimulation: true })
        }
        if (message.method === "powerbi.serviceModels") {
            if (control.failCatalogOnce) {
                control.failCatalogOnce = false
                return this.send({ protocolVersion: 1, id: message.id, error: { code: "CATALOG_UNAVAILABLE", message: "模拟权限目录读取失败" } })
            }
            return reply(models.filter((item) => !control.revoked.includes(item.id)))
        }
        if (message.method === "powerbi.serviceRecentModels")
            return reply({
                items: (recents.get(`${control.account.username}/${control.account.environmentName}`) || []).filter(
                    (item) => !control.revoked.includes(item.modelId),
                ),
                storageWarning: null,
            })
        if (message.method === "project.connectService") {
            const model = models.find((item) => item.id === message.params.modelId && !control.revoked.includes(item.id))
            if (!model) return this.send({ protocolVersion: 1, id: message.id, error: { code: "MODEL_UNAVAILABLE", message: "模型不可用" } })
            const key = `${control.account.username}/${control.account.environmentName}`
            recents.set(key, [
                { id: `recent-${model.id}`, modelId: model.id, name: model.name, workspace: model.workspace, lastUsedAt: "2026-10-15T00:00:00Z" },
                ...(recents.get(key) || []).filter((item) => item.modelId !== model.id),
            ])
            this.currentProject = {
                ...structuredClone(this.currentProject),
                projectId: `service-${model.id}`,
                projectPath: `model://service-${model.id}`,
                modelFormat: "Power BI Service",
                pages: [],
                model: { ...this.currentProject.model, name: model.name },
                connection: { kind: "powerbi-service", label: "Power BI Service", modelId: model.id },
            }
            return reply(this.currentProject)
        }
        original.call(this, message)
    }
}

/** 等待所有父子面板请求结束, 后续断言不依赖固定毫秒延迟. */
async function waitIdle(page) {
    await page.waitForFunction(() => document.querySelector(".content")?.getAttribute("aria-busy") === "false")
}

/** 等待下拉与提示实际离场及有限动画结束, 保留加载请求本身, 避免截入 teleport 的半透明残影. */
async function settleVisualState(page) {
    await page.mouse.move(0, 0)
    await page.waitForFunction(() =>
        [...document.querySelectorAll(".el-popper")].every((element) => {
            const style = getComputedStyle(element)
            const rect = element.getBoundingClientRect()
            return style.display === "none" || style.visibility === "hidden" || !rect.width || !rect.height
        }),
    )
    await page.evaluate(async () => {
        const animations = document.getAnimations().filter((animation) => animation.effect?.getTiming().iterations !== Infinity)
        await Promise.allSettled(animations.map((animation) => animation.finished))
    })
    const state = await page.evaluate(() => ({
        visiblePoppers: [...document.querySelectorAll(".el-popper")].filter((element) => {
            const style = getComputedStyle(element)
            const rect = element.getBoundingClientRect()
            return style.display !== "none" && style.visibility !== "hidden" && rect.width && rect.height
        }).length,
        finiteAnimations: document
            .getAnimations()
            .filter(
                (animation) => animation.effect?.getTiming().iterations !== Infinity && animation.playState !== "finished" && animation.playState !== "idle",
            ).length,
    }))
    assert.deepEqual(state, { visiblePoppers: 0, finiteAnimations: 0 })
    return state
}

/** 验证当前桌面几何和侧栏折叠, 保存可人工复核的完整页面截图. */
async function captureLayout(page, name) {
    for (const [width, height, collapsed] of [
        [1422, 800, false],
        [1422, 800, true],
        [1920, 1080, false],
    ]) {
        await page.setViewportSize({ width, height })
        const toggle = page.locator(".collapse-button")
        if (((await toggle.getAttribute("aria-expanded")) === "false") !== collapsed) await toggle.click()
        const visualState = await settleVisualState(page)
        const geometry = await page.evaluate(() => {
            const sidebar = document.querySelector(".sidebar")
            const center = document.querySelector(".connection-center")
            return {
                width: innerWidth,
                height: innerHeight,
                pageWidth: document.documentElement.scrollWidth,
                sidebarHeight: sidebar.clientHeight,
                sidebarScrollHeight: sidebar.scrollHeight,
                centerWidth: center.clientWidth,
                centerScrollWidth: center.scrollWidth,
            }
        })
        assert.ok(geometry.pageWidth <= width, JSON.stringify(geometry))
        assert.ok(geometry.sidebarScrollHeight <= geometry.sidebarHeight, JSON.stringify(geometry))
        assert.ok(geometry.centerScrollWidth <= geometry.centerWidth + 1, JSON.stringify(geometry))
        await page.screenshot({
            path: `${evidence}${name}-${width}x${height}-${collapsed ? "collapsed" : "expanded"}.png`,
            fullPage: true,
            animations: "disabled",
        })
        rows.push({ scenario: name, status: "PASS", collapsed, geometry, visualState })
    }
    await page.setViewportSize({ width: 1422, height: 800 })
}

/** 在单一语言上下文执行文件门闩、登录分支、权限撤销与全局偏好, 确保来源之间不共享记录. */
async function runLocale(locale) {
    const english = locale === "en-us"
    const label = {
        signIn: english ? "Sign in" : "登录",
        signOut: english ? "Sign out" : "退出登录",
        email: english ? "Work or school email" : "工作或学校邮箱",
        cancel: english ? "Cancel operation" : "取消操作",
        workspace: english ? "Workspace" : "工作区",
        model: english ? "Semantic model" : "语义模型",
        environment: english ? "Sign-in environment" : "登录环境",
        refreshModels: english ? "Refresh models" : "刷新模型列表",
        settings: english ? "Settings" : "设置",
        connections: english ? "Connections" : "连接",
        recent: english ? "Show recently opened files" : "显示最近打开的文件",
    }
    const page = (activePage = await browser.newPage({ viewport: { width: 1422, height: 800 } }))
    page.setDefaultTimeout(15000)
    await monitorRuntime(page, issues)
    await page.route(/\/src\/bridge\/mock\.ts/, async (route) => {
        const response = await route.fetch()
        await route.fulfill({ response, body: (await response.text()) + `\n(${installControlledMock.toString()})(MockTransport, ${JSON.stringify(locale)});` })
    })
    await page.goto(target, { waitUntil: "networkidle" })
    await waitIdle(page)
    await assertInlineConnections(page)
    const center = page.locator(".connection-center")
    const recents = page.locator(".recent-files-panel")
    const file = center.locator(".desktop-file-button")
    assert.equal(await file.locator("img").getAttribute("src"), "/icons/power-bi.svg")
    assert.match(await recents.innerText(), /本地 PBIX 记录/)
    const initialScans = await page.evaluate(() => window.connectionsTest.calls.filter((call) => call.method === "powerbi.instances").length)
    await file.click()
    await center.locator(".desktop-file-loading").waitFor()
    assert.equal(await file.evaluate((element) => element.classList.contains("is-loading")), true)
    await page.evaluate(() => window.connectionsTest.phase("connecting"))
    assert.match(await center.locator(".desktop-file-loading").innerText(), english ? /Opening the file in Power BI Desktop/ : /正在通过 Power BI Desktop/)
    await settleVisualState(page)
    await page.screenshot({ path: `${evidence}${locale}-desktop-file-loading.png`, animations: "disabled" })
    assert.equal(await page.evaluate(() => window.connectionsTest.calls.filter((call) => call.method === "powerbi.instances").length), initialScans)
    await center.getByRole("button", { name: label.cancel, exact: true }).click()
    assert.equal(await file.evaluate((element) => element.classList.contains("is-loading")), true)
    assert.equal(await center.locator(".desktop-file-loading").count(), 1)
    await page.evaluate(() => window.connectionsTest.complete("CANCELLED"))
    await waitIdle(page)
    assert.equal(await center.locator(".desktop-file-loading").count(), 0)
    assert.equal(await page.evaluate(() => window.connectionsTest.calls.filter((call) => call.method === "powerbi.instances").length), initialScans)
    await file.click()
    await center.locator(".desktop-file-loading").waitFor()
    await page.evaluate(() => window.connectionsTest.complete())
    await center.locator(".instance-card.is-current").waitFor()
    await waitIdle(page)
    assert.match(await center.locator(".instance-card.is-current").innerText(), /新打开 PBIX 1/)
    assert.equal(await page.evaluate(() => window.connectionsTest.calls.filter((call) => call.method === "powerbi.instances").length), initialScans + 1)
    rows.push({ scenario: `${locale} Desktop 阶段/取消排空/成功后单次刷新`, status: "PASS" })
    await captureLayout(page, `${locale}-desktop`)
    await recents.getByRole("button").click()
    await page.locator(".connection-progress").waitFor()
    await page.evaluate(() => window.connectionsTest.phase("connecting"))
    assert.equal(await page.locator(".connection-progress").count(), 1)
    await page.evaluate(() => window.connectionsTest.complete())
    await center.locator(".instance-card.is-current").filter({ hasText: "新打开 PBIX 2" }).waitFor()
    await waitIdle(page)
    assert.equal(await page.locator(".connection-progress").count(), 0)
    assert.equal(await page.evaluate(() => window.connectionsTest.calls.filter((call) => call.method === "powerbi.instances").length), initialScans + 2)
    rows.push({ scenario: `${locale} 本地最近文件重开后刷新`, status: "PASS" })
    await center.getByRole("tab", { name: "SSAS / XMLA", exact: true }).click()
    assert.equal(await recents.count(), 0)
    await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
    await waitIdle(page)
    assert.equal(await recents.count(), 0)
    await center.getByRole("button", { name: label.signIn, exact: true }).click()
    await center.locator(".service-login-loading").waitFor()
    assert.doesNotMatch(await center.locator(".service-login-loading").innerText(), english ? /Detecting/ : /检测/)
    assert.equal(await page.evaluate(() => window.connectionsTest.pending.message.params.environmentName), "ChinaCloud")
    await page.evaluate(() => window.connectionsTest.complete())
    await center.locator(".account-name").filter({ hasText: "saved@example.com" }).waitFor()
    await waitIdle(page)
    assert.match(await recents.innerText(), /Sales North/)
    assert.doesNotMatch(await recents.innerText(), /本地 PBIX/)
    rows.push({ scenario: `${locale} 已记住环境直接认证/云端记录隔离`, status: "PASS" })
    const workspace = center.getByRole("combobox", { name: label.workspace, exact: true })
    await workspace.fill("South")
    await page.getByRole("option", { name: "South workspace", exact: true }).click()
    const model = center.getByRole("combobox", { name: label.model, exact: true })
    await model.fill("Revenue")
    await page.getByRole("option", { name: "Revenue South", exact: true }).click()
    await center.locator(".instance-card:not(.is-current) button").click()
    await waitIdle(page)
    assert.match(await recents.innerText(), /Revenue South/)
    await captureLayout(page, `${locale}-service`)
    await page.evaluate(() => window.connectionsTest.revoked.push("model-c"))
    await center.getByRole("button", { name: label.refreshModels, exact: true }).click()
    await waitIdle(page)
    assert.doesNotMatch(await recents.innerText(), /Revenue South/)
    rows.push({ scenario: `${locale} 工作区/模型输入搜索及权限刷新撤销最近记录`, status: "PASS" })
    await page.evaluate(() => {
        window.connectionsTest.failCatalogOnce = true
    })
    await center.getByRole("button", { name: label.refreshModels, exact: true }).click()
    await waitIdle(page)
    assert.equal(await recents.getByRole("button").count(), 0)
    assert.doesNotMatch(await recents.innerText(), /Sales North|Revenue South/)
    const catalogError = page.locator(".el-message--error .el-message__content").filter({ hasText: /^模拟权限目录读取失败$/ })
    await catalogError.waitFor()
    assert.equal(await catalogError.innerText(), "模拟权限目录读取失败")
    await center.getByRole("button", { name: label.refreshModels, exact: true }).click()
    await waitIdle(page)
    assert.match(await recents.innerText(), /Sales North/)
    rows.push({ scenario: `${locale} 最后目录读取失败清空历史且重试恢复权威记录`, status: "PASS" })
    await center.getByRole("button", { name: label.signOut, exact: true }).click()
    await waitIdle(page)
    assert.equal(await recents.count(), 0)
    await center.getByRole("textbox", { name: label.email, exact: true }).fill("single@example.com")
    await center.getByRole("button", { name: label.signIn, exact: true }).click()
    await center
        .locator(".service-login-loading")
        .filter({ hasText: english ? /Detecting/ : /检测/ })
        .waitFor()
    await settleVisualState(page)
    await page.screenshot({ path: `${evidence}${locale}-discovering-environment.png`, animations: "disabled" })
    await page.evaluate(() => window.connectionsTest.discovery())
    await center
        .locator(".service-login-loading")
        .filter({ hasText: english ? /Signing in/ : /正在登录/ })
        .waitFor()
    await page.evaluate(() => window.connectionsTest.complete())
    await center.locator(".account-name").filter({ hasText: "single@example.com" }).waitFor()
    await waitIdle(page)
    assert.equal(await center.getByRole("combobox", { name: label.environment, exact: true }).count(), 0)
    assert.doesNotMatch(await recents.innerText(), /Sales North|Revenue South|本地 PBIX/)
    rows.push({ scenario: `${locale} 首次单云发现后自动认证/账号最近记录隔离`, status: "PASS" })
    await center.getByRole("button", { name: label.signOut, exact: true }).click()
    await waitIdle(page)
    await center.getByRole("textbox", { name: label.email, exact: true }).fill("multi@example.com")
    await center.getByRole("button", { name: label.signIn, exact: true }).click()
    await center.locator(".service-login-loading").waitFor()
    await page.evaluate(() => window.connectionsTest.discovery())
    await waitIdle(page)
    assert.equal(await center.getByRole("button", { name: label.signIn, exact: true }).isEnabled(), false)
    await center.getByRole("combobox", { name: label.environment, exact: true }).press("Enter")
    await page.getByRole("option", { name: english ? "21Vianet (China)" : "世纪互联 (中国)", exact: true }).click()
    await center.getByRole("button", { name: label.signIn, exact: true }).click()
    await center.locator(".service-login-loading").waitFor()
    await page.evaluate(() => window.connectionsTest.complete())
    await center.locator(".account-name").filter({ hasText: "multi@example.com" }).waitFor()
    await waitIdle(page)
    assert.doesNotMatch(await recents.innerText(), /Sales North|Revenue South|本地 PBIX/)
    rows.push({ scenario: `${locale} 首次多云等待明确选择及再次点击登录`, status: "PASS" })
    await page.locator(".sidebar nav").getByRole("button", { name: label.settings, exact: true }).click()
    await page.locator(".ui-settings-panel .el-switch").click()
    await waitIdle(page)
    await page.locator(".sidebar nav").getByRole("button", { name: label.connections, exact: true }).click()
    await waitIdle(page)
    assert.equal(await recents.count(), 0)
    await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
    await waitIdle(page)
    assert.equal(await recents.count(), 0)
    await page.locator(".sidebar nav").getByRole("button", { name: label.settings, exact: true }).click()
    await page.locator(".ui-settings-panel .el-switch").click()
    await waitIdle(page)
    await page.locator(".sidebar nav").getByRole("button", { name: label.connections, exact: true }).click()
    await waitIdle(page)
    await center.getByRole("tab", { name: "Power BI Desktop", exact: true }).click()
    await waitIdle(page)
    assert.match(await recents.innerText(), /本地 PBIX 记录/)
    await center.getByRole("tab", { name: "Power BI Service", exact: true }).click()
    await waitIdle(page)
    assert.equal(await recents.count(), 1)
    assert.doesNotMatch(await recents.innerText(), /本地 PBIX/)
    rows.push({ scenario: `${locale} 设置统一关闭/开启两类最近记录且保留历史`, status: "PASS" })
    await collectRuntimeErrors(page, issues)
    await page.close()
    activePage = undefined
}

try {
    await runLocale("zh-cn")
    await runLocale("en-us")
    assert.equal(issues.size, 0, JSON.stringify([...issues]))
    await writeFile(
        `${evidence}browser.json`,
        JSON.stringify(
            {
                status: "PASS",
                platform: "Windows",
                browser: browser.version(),
                mode: "explicit browser mock with controlled stages",
                realDesktopReady: "UNRUN",
                realMicrosoftSignIn: "UNRUN",
                realWebView2: "UNRUN",
                rows,
                consoleIssues: [...issues],
            },
            null,
            2,
        ),
    )
    console.log(`PASS ${rows.length} connection/stage/layout scenarios; console clean; real Desktop/Service/WebView2 UNRUN`)
} catch (error) {
    if (activePage && !activePage.isClosed()) {
        await collectRuntimeErrors(activePage, issues)
        await activePage.screenshot({ path: `${evidence}browser-failure.png`, fullPage: true })
    }
    await writeFile(`${evidence}browser-failure.json`, JSON.stringify({ status: "FAIL", message: String(error), rows, consoleIssues: [...issues] }, null, 2))
    throw error
} finally {
    await browser.close()
}
