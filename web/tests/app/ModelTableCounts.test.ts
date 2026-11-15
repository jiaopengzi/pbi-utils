/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableCounts.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 Service 自动读取表行数和统计取消的请求身份边界.
 */
import { expect, it, vi } from "vitest"
import { flushPromises } from "@vue/test-utils"
import { setLocale } from "@/i18n"
import { setupTables, tableItem, tablesProject } from "./ModelTablesPanel.support"

it.each([
    { locale: "zh-cn", countScope: "当前账号可见", removedButton: "精确统计所选表" },
    { locale: "en-us", countScope: "Visible to current account", removedButton: "Count selected tables exactly" },
] as const)("$locale Service 首次进入无需选择, 自动显示所有返回的实际行数", async ({ locale, countScope, removedButton }) => {
    setLocale(locale)
    const project = tablesProject()
    project.connection = { kind: "powerbi-service", label: "Power BI Service", modelId: "service-model" }
    const { wrapper, statisticsRequests, cleanup } = await setupTables({
        project,
        statistics: [
            { table: "销售", rowCount: 0, sizeBytes: null, isQueryCount: true },
            { table: "另一张表", rowCount: 2622, sizeBytes: 4096, isQueryCount: true },
        ],
    })
    try {
        expect(statisticsRequests).toHaveLength(1)
        expect(statisticsRequests[0]!.params).toEqual({ projectId: project.projectId, baselineId: project.baselineId })
        expect(wrapper.find('.table-selection input[type="checkbox"]:checked').exists()).toBe(false)
        expect(wrapper.text()).not.toContain(removedButton)
        const sales = tableItem(wrapper, "销售")
        const other = tableItem(wrapper, "另一张表")
        expect(sales.get(".table-title > span:nth-child(2)").text()).toBe("0")
        expect(other.get(".table-title > span:nth-child(2)").text()).toBe("2,622")
        expect(other.get(".table-title > span:nth-child(2)").attributes("title")).toBe(countScope)
        await other.get(".el-collapse-item__header").trigger("click")
        expect(other.text()).toContain(countScope)
        expect(other.text()).toMatch(/4\s*KB/)
        expect(statisticsRequests).toHaveLength(1)
    } finally {
        cleanup()
    }
})

it.each(["rejected", "error"])("旧统计的迟到取消%s不解除新统计的取消状态, 仍等待新原请求终态", async (acknowledgement) => {
    const { wrapper, sent, send, statisticsRequests, cleanup } = await setupTables({ autoStatistics: false })
    try {
        document.body.querySelector<HTMLButtonElement>(".operation-message button")!.click()
        await flushPromises()
        const previousCancel = sent[0]!
        send({ id: statisticsRequests[0]!.id, error: { code: "CANCELLED", message: "操作已取消" } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "刷新统计")!
            .trigger("click")
        await flushPromises()
        const cancel = document.body.querySelector<HTMLButtonElement>(".operation-message button")!
        cancel.click()
        await flushPromises()
        expect(sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: statisticsRequests[1]!.id } })
        send({ id: sent[1]!.id, result: { accepted: true } })
        await flushPromises()
        if (acknowledgement === "rejected") send({ id: previousCancel.id, result: { accepted: false } })
        else send({ id: previousCancel.id, error: { code: "CANCEL_FAILED", message: "旧取消确认失败" } })
        await flushPromises()
        expect(cancel.disabled).toBe(true)
        expect(document.body.querySelector(".operation-message")?.textContent).toContain("正在取消")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(wrapper.get('[aria-label="选择表 销售"] input').attributes("disabled")).toBeDefined()
        cancel.click()
        await flushPromises()
        expect(sent).toHaveLength(2)
        expect(statisticsRequests).toHaveLength(2)
        send({ id: statisticsRequests[1]!.id, error: { code: "CANCELLED", message: "操作已取消" } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(wrapper.get('[aria-label="选择表 销售"] input').attributes("disabled")).toBeUndefined()
        expect(document.querySelector(".el-message--warning")).toBeNull()
    } finally {
        cleanup()
    }
})

it.each([
    { locale: "zh-cn", accepted: true, warning: "请求超时" },
    { locale: "en-us", accepted: true, warning: "The request timed out." },
    { locale: "zh-cn", accepted: false, warning: null },
] as const)("$locale 统计超时后等待原请求完成, 取消受理=$accepted", async ({ locale, accepted, warning }) => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] })
    setLocale(locale)
    const project = tablesProject()
    project.connection = { kind: "powerbi-service", label: "Power BI Service", modelId: "service-model" }
    const { wrapper, sent, send, statisticsRequests, replyStatistics, cleanup } = await setupTables({ project, autoStatistics: false })
    try {
        const request = statisticsRequests[0]!
        const count = tableItem(wrapper, "销售").get(".table-title > span:nth-child(2)")
        await vi.advanceTimersByTimeAsync(180000)
        expect(sent[0]).toMatchObject({ method: "request.cancel", params: { requestId: request.id } })
        expect((document.querySelector(".operation-message button") as HTMLButtonElement).disabled).toBe(true)
        send({ id: sent[0]!.id, result: { accepted } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(document.querySelector(".el-message--warning")).toBeNull()
        expect(count.text()).toBe("-")
        if (accepted) send({ id: request.id, error: { code: "CANCELLED", message: "操作已取消" } })
        else replyStatistics(request, [{ table: "销售", rowCount: 123, sizeBytes: null, isQueryCount: true }])
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(count.text()).toBe(accepted ? "-" : "123")
        if (warning) expect(document.querySelector(".el-message--warning")?.textContent).toBe(warning)
        else expect(document.querySelector(".el-message--warning")).toBeNull()
    } finally {
        cleanup()
        vi.useRealTimers()
    }
})
