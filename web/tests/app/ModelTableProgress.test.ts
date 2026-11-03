/**
 * FilePath    : pbi-utils\web\tests\app\ModelTableProgress.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证多表导出复用共享进度条, 保存前不报100%, 取消/失败/迟到结果不伪报完成.
 */
import { beforeEach, expect, it, vi } from "vitest"
import { flushPromises } from "@vue/test-utils"
import { exportResult, setupTables } from "./ModelTablesPanel.support"
import { showMessage } from "@/ui/message"
vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))
beforeEach(() => {
    vi.mocked(showMessage).mockClear()
})

/** 通过真实控件发出两表请求, 后续进度和唯一终态由用例独立控制. */
async function exporting() {
    const h = await setupTables()
    await h.wrapper.get('.table-list-head input[type="checkbox"]').setValue(true)
    await h.wrapper.get(".table-export-button").trigger("click")
    const request = h.sent[0]!
    const progress = async (patch: object = {}) => {
        h.send({
            requestProgress: {
                requestId: request.id,
                kind: "tableExport",
                stage: "reading",
                completedTables: 0,
                totalTables: 2,
                tableIndex: 1,
                rowsRead: 0,
                ...patch,
            },
        })
        await flushPromises()
    }
    return { ...h, request, progress }
}

it("共享进度条跟随真实表数, 文件保存成功前最高99且提交时不可取消", async () => {
    const h = await exporting()
    try {
        await h.progress({ rowsRead: 2300 })
        expect(document.querySelector('[role="progressbar"]')?.getAttribute("aria-valuenow")).toBe("0")
        expect(document.body.textContent).toContain("2,300")
        await h.progress({ completedTables: 1, tableIndex: 2, rowsRead: 30 })
        expect(document.querySelector('[role="progressbar"]')?.getAttribute("aria-valuenow")).toBe("50")
        await h.progress({ stage: "finalizing", completedTables: 2, tableIndex: 2, rowsRead: 30 })
        expect(document.querySelector('[role="progressbar"]')?.getAttribute("aria-valuenow")).toBe("99")
        h.send({ requestPhase: { requestId: h.request.id, phase: "committing" } })
        await flushPromises()
        expect((document.querySelector(".operation-feedback__cancel") as HTMLButtonElement).disabled).toBe(true)
        h.send({
            id: h.request.id,
            result: exportResult(
                "ok.xlsx",
                h.project.model.tables.map((t) => t.name),
            ),
        })
        await flushPromises()
        expect(document.querySelector('[role="progressbar"]')).toBeNull()
        expect(showMessage).toHaveBeenCalledTimes(1)
        expect(vi.mocked(showMessage).mock.calls[0]?.[1]).toBe("success")
    } finally {
        h.cleanup()
    }
})

it.each(["CANCELLED", "EXPORT_IO_FAILED"])("%s终态清除进度并保留失败事实, 迟到进度不重开", async (code) => {
    const h = await exporting()
    try {
        await h.progress({ completedTables: 1, tableIndex: 2, rowsRead: 40 })
        h.send({ id: h.request.id, error: { code, message: code === "CANCELLED" ? "操作已取消" : "无法写入导出文件" } })
        await flushPromises()
        await h.progress({ stage: "finalizing", completedTables: 2, tableIndex: 2, rowsRead: 40 })
        expect(document.querySelector('[role="progressbar"]')).toBeNull()
        expect(vi.mocked(showMessage).mock.calls.some((call) => call[1] === "success")).toBe(false)
    } finally {
        h.cleanup()
    }
})

it("取消确认不提前解锁或完成进度, 等原请求排空后才结束消息", async () => {
    const h = await exporting()
    try {
        await h.progress({ completedTables: 1, tableIndex: 2, rowsRead: 4000 })
        ;(document.querySelector(".operation-feedback__cancel") as HTMLButtonElement).click()
        await flushPromises()
        expect(h.sent[1]?.method).toBe("request.cancel")
        h.send({ id: h.sent[1]!.id, result: { accepted: true } })
        await h.progress({ stage: "finalizing", completedTables: 2, tableIndex: 2, rowsRead: 5000 })
        expect(document.querySelector('[role="progressbar"]')?.getAttribute("aria-valuenow")).toBe("50")
        expect(h.wrapper.get(".table-export-button").attributes()).toHaveProperty("disabled")
        expect(showMessage).not.toHaveBeenCalled()
        h.send({ id: h.request.id, error: { code: "CANCELLED", message: "操作已取消" } })
        await flushPromises()
        expect(document.querySelector('[role="progressbar"]')).toBeNull()
        expect(h.wrapper.get(".table-export-button").attributes()).not.toHaveProperty("disabled")
    } finally {
        h.cleanup()
    }
})

it("卸载清除自己的共享进度, 迟到事件不重建消息或成功提示", async () => {
    const h = await exporting()
    try {
        await h.progress({ rowsRead: 4000 })
        h.unmount()
        await h.progress({ completedTables: 1, tableIndex: 2, rowsRead: 40 })
        expect(document.querySelector('[role="progressbar"]')).toBeNull()
        h.send({
            id: h.request.id,
            result: exportResult(
                "late.xlsx",
                h.project.model.tables.map((t) => t.name),
            ),
        })
        await flushPromises()
        expect(showMessage).not.toHaveBeenCalled()
    } finally {
        h.cleanup()
    }
})
