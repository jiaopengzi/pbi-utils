/**
 * FilePath    : pbi-utils\web\tests\browser-acceptance.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享浏览器生命周期异常采集与连接页内联连接入口检查.
 */
import assert from "node:assert/strict"

/** 在导航前监听所有异常通道, 包含不会作为 pageerror 上报的 ResizeObserver ErrorEvent. */
export async function monitorRuntime(page, issues) {
    const record = (message) => issues.set(message, (issues.get(message) ?? 0) + 1)
    page.on("pageerror", (error) => record(`pageerror: ${error.message}`))
    page.on("console", (message) => {
        if (["error", "warning"].includes(message.type())) record(`${message.type()}: ${message.text()}`)
    })
    await page.addInitScript(() => {
        window.browserAcceptanceErrors = []
        window.addEventListener("error", (event) => window.browserAcceptanceErrors.push(`error: ${event.message}`))
        window.addEventListener("unhandledrejection", (event) => window.browserAcceptanceErrors.push(`unhandledrejection: ${String(event.reason)}`))
    })
}

/** 在结束前读取从文档开始积累的错误, 每页调用一次避免重复计数. */
export async function collectRuntimeErrors(page, issues) {
    for (const message of await page.evaluate(() => window.browserAcceptanceErrors)) issues.set(message, (issues.get(message) ?? 0) + 1)
}

/** 三种连接方式直接显示在连接页, 连接流程不创建对话框和重复的顶栏选择入口. */
export async function assertInlineConnections(page) {
    const center = page.locator(".connection-center")
    await center.waitFor()
    assert.deepEqual(
        await Promise.all(["Power BI Desktop", "Power BI Service", "SSAS / XMLA"].map((name) => center.getByRole("tab", { name, exact: true }).count())),
        [1, 1, 1],
    )
    assert.equal(await page.getByRole("dialog").count(), 0)
    assert.equal(
        await page
            .locator(".header-actions")
            .getByRole("button", { name: /^(连接模型|切换模型|Connect model|Switch model)$/ })
            .count(),
        0,
    )
    assert.equal(await page.getByRole("button", { name: /^(选择连接方式|Choose connection)/ }).count(), 0)
}
