/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableCounts.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证显式精确计数只查询勾选表, 保留大小与失败前统计并守住取消/身份边界.
 */
import { expect, it } from "vitest"
import { flushPromises } from "@vue/test-utils"
import { selectTable, setupTables, tableItem, type StatisticsRows } from "./ModelTablesPanel.support"

const estimates: StatisticsRows = [
    { table: "销售", rowCount: 500, sizeBytes: 2048, isEstimate: true, isQueryCount: false },
    { table: "另一张表", rowCount: 700, sizeBytes: 4096, isEstimate: true, isQueryCount: false },
]

it.each([0, 622])("精确统计%d行需要显式选择, 只更新所选表账号可见行数且保留存储大小", async (rowCount) => {
    const { wrapper, rowCountRequests, replyStatistics, cleanup } = await setupTables({ statistics: estimates })
    try {
        const count = wrapper.findAll("button").find((item) => item.text() === "精确统计所选表")!
        expect(count.attributes("disabled")).toBeDefined()
        await count.trigger("click")
        expect(rowCountRequests).toHaveLength(0)
        await selectTable(wrapper, "另一张表")
        expect(count.attributes("disabled")).toBeUndefined()
        await count.trigger("click")
        expect(rowCountRequests).toHaveLength(1)
        expect(rowCountRequests[0]!.params).toEqual({ projectId: "preview", baselineId: "preview-only", tables: ["另一张表"] })
        replyStatistics(rowCountRequests[0]!, [{ table: "另一张表", rowCount, sizeBytes: null, isEstimate: false, isQueryCount: true }])
        await flushPromises()
        const sales = tableItem(wrapper, "销售"),
            other = tableItem(wrapper, "另一张表")
        await other.get(".el-collapse-item__header").trigger("click")
        expect(sales.get(".el-collapse-item__header").text()).toContain("≈")
        expect(sales.get(".el-collapse-item__header").text()).toContain("500")
        expect(other.get(".el-collapse-item__header").text()).toContain(String(rowCount))
        expect(other.get(".el-collapse-item__header").text()).not.toContain("≈")
        expect(other.text()).toMatch(/4\s*KB/)
        expect(other.text()).toMatch(/账号可见/)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})

it.each(["failure", "cancel"])("精确统计%s保留原估计与大小, 取消受理不提前解锁", async (reason) => {
    const { wrapper, sent, send, rowCountRequests, cleanup } = await setupTables({ statistics: estimates })
    try {
        await selectTable(wrapper, "销售")
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "精确统计所选表")!
            .trigger("click")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        if (reason === "cancel") {
            document.body.querySelector<HTMLButtonElement>(".operation-message button")!.click()
            await flushPromises()
            expect(sent[0]).toMatchObject({ method: "request.cancel", params: { requestId: rowCountRequests[0]!.id } })
            send({ id: sent[0]!.id, result: { accepted: true } })
            await flushPromises()
            expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
            expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        }
        send({
            id: rowCountRequests[0]!.id,
            error: { code: reason === "cancel" ? "CANCELLED" : "COUNT_FAILED", message: reason === "cancel" ? "操作已取消" : "精确计数读取失败" },
        })
        await flushPromises()
        const header = tableItem(wrapper, "销售").get(".el-collapse-item__header").text()
        expect(header).toContain("≈")
        expect(header).toContain("500")
        expect(header).toMatch(/2\s*KB/)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        if (reason === "failure") expect(document.querySelector(".el-message--warning")?.textContent).toContain("精确计数读取失败")
    } finally {
        cleanup()
    }
})

it.each(["target", "unmount"])("精确计数在%s后取消旧请求, 迟到数据不覆盖或通知新页面", async (change) => {
    const { wrapper, project, sent, send, rowCountRequests, replyStatistics, unmount, cleanup } = await setupTables({ statistics: estimates })
    try {
        await selectTable(wrapper, "销售")
        await wrapper
            .findAll("button")
            .find((item) => item.text() === "精确统计所选表")!
            .trigger("click")
        if (change === "target") await wrapper.setProps({ project: { ...project, projectId: "other-project", baselineId: "other-baseline" } })
        else unmount()
        expect(sent[0]).toMatchObject({ method: "request.cancel", params: { requestId: rowCountRequests[0]!.id } })
        send({ id: sent[0]!.id, result: { accepted: true } })
        replyStatistics(rowCountRequests[0]!, [{ table: "销售", rowCount: 999, sizeBytes: null, isEstimate: false, isQueryCount: true }])
        await flushPromises()
        if (change === "target") expect(tableItem(wrapper, "销售").text()).not.toContain("999")
        expect(document.body.textContent).not.toContain("999")
    } finally {
        cleanup()
    }
})

it.each(["rejected", "error"])("旧计数的迟到取消%s不解除新计数已取消中状态, 仍等新原请求终态", async (acknowledgement) => {
    const { wrapper, sent, send, rowCountRequests, cleanup } = await setupTables({ statistics: estimates })
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".exact-row-count").trigger("click")
        await flushPromises()
        document.body.querySelector<HTMLButtonElement>(".operation-message button")!.click()
        await flushPromises()
        const previousCancel = sent[0]!
        send({ id: rowCountRequests[0]!.id, error: { code: "CANCELLED", message: "操作已取消" } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        await wrapper.get(".exact-row-count").trigger("click")
        await flushPromises()
        const cancel = document.body.querySelector<HTMLButtonElement>(".operation-message button")!
        cancel.click()
        await flushPromises()
        expect(sent[1]).toMatchObject({ method: "request.cancel", params: { requestId: rowCountRequests[1]!.id } })
        send({ id: sent[1]!.id, result: { accepted: true } })
        await flushPromises()
        if (acknowledgement === "rejected") send({ id: previousCancel.id, result: { accepted: false } })
        else send({ id: previousCancel.id, error: { code: "CANCEL_FAILED", message: "旧取消确认失败" } })
        await flushPromises()
        expect(cancel.disabled).toBe(true)
        expect(document.body.querySelector(".operation-message")?.textContent).toContain("正在取消")
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        cancel.click()
        await flushPromises()
        expect(sent).toHaveLength(2)
        expect(rowCountRequests).toHaveLength(2)
        send({ id: rowCountRequests[1]!.id, error: { code: "CANCELLED", message: "操作已取消" } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})
