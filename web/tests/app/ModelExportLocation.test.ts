/**
 * FilePath    : pbi-utils\web\tests\app\ModelExportLocation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证最近导出目录由宿主身份绑定, 保存警告不丢导出事实, 卸载不显示迟到通知.
 */
import { expect, it } from "vitest"
import { flushPromises, type VueWrapper } from "@vue/test-utils"
import type { ModelExportLocation } from "@/bridge/modelContracts"
import { setLocale } from "@/i18n"
import { exportResult, selectTable, setupTables } from "./ModelTablesPanel.support"

/** 只返回用户可见的受限目录按钮, 测试不调用私有方法或提交路径作为打开参数. */
function openButton(wrapper: VueWrapper) {
    const button = wrapper.findAll("button").find((item) => /^(打开导出目录|Open export (directory|folder))$/.test(item.text()))
    if (!button) throw new Error("Bound export directory button is missing")
    return button
}

const firstLocation: ModelExportLocation = {
    id: "a".repeat(32),
    displayPath: "C:\\isolated-export\\first",
    available: true,
    warning: null,
    isSimulation: false,
}
const secondLocation: ModelExportLocation = {
    id: "b".repeat(32),
    displayPath: "C:\\isolated-export\\second",
    available: true,
    warning: null,
    isSimulation: false,
}

it.each(["zh-cn", "en-us"] as const)("%s挂载读取最近成功导出目录, 打开只提交当前记录id而不提交路径", async (locale) => {
    const { wrapper, locationRequests, directoryRequests, send, cleanup } = await setupTables({ location: firstLocation })
    try {
        setLocale(locale)
        await flushPromises()
        expect(locationRequests).toHaveLength(1)
        expect(locationRequests[0]!.params).toEqual({})
        expect(wrapper.text()).toContain(firstLocation.displayPath)
        await openButton(wrapper).trigger("click")
        expect(directoryRequests).toHaveLength(1)
        expect(directoryRequests[0]!.params).toEqual({ id: firstLocation.id })
        send({ id: directoryRequests[0]!.id, result: { opened: true, isSimulation: false } })
        await flushPromises()
    } finally {
        cleanup()
    }
})

it("已记录但不存在的目录仍可展示, 打开按钮禁用且不回退其它路径", async () => {
    const { wrapper, directoryRequests, cleanup } = await setupTables({ location: { ...firstLocation, available: false, warning: "导出目录已不存在或不可用" } })
    try {
        expect(wrapper.text()).toContain(firstLocation.displayPath)
        expect(openButton(wrapper).attributes("disabled")).toBeDefined()
        await openButton(wrapper).trigger("click")
        expect(directoryRequests).toHaveLength(0)
        expect(document.querySelector(".el-message--warning")?.textContent).toContain("导出目录已不存在或不可用")
    } finally {
        cleanup()
    }
})

it("导出完成刷新最近目录, 记录保存警告同时保留文件成功事实", async () => {
    const { wrapper, sent, send, locationRequests, directoryRequests, setLocation, cleanup } = await setupTables({ location: firstLocation })
    try {
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-export-button").trigger("click")
        const warning = "文件已导出, 但无法保存导出目录记录"
        setLocation({ ...secondLocation, warning })
        send({ id: sent[0]!.id, result: { ...exportResult("saved.xlsx", ["销售"], 20), locationWarning: warning } })
        await flushPromises()
        expect(locationRequests).toHaveLength(2)
        expect(wrapper.text()).toContain(secondLocation.displayPath)
        expect(document.querySelector(".el-message--success")?.textContent).toContain("saved.xlsx")
        expect(document.querySelector(".el-message--warning")?.textContent).toContain(warning)
        expect(document.querySelector(".el-message--error")).toBeNull()
        await openButton(wrapper).trigger("click")
        expect(directoryRequests[0]!.params).toEqual({ id: secondLocation.id })
        send({ id: directoryRequests[0]!.id, result: { opened: true, isSimulation: false } })
        await flushPromises()
    } finally {
        cleanup()
    }
})

it("文件已经导出后目录读取失败只警告, 不将成功改为导出错误", async () => {
    const { wrapper, sent, send, locationRequests, setAutoLocation, cleanup } = await setupTables({ location: firstLocation })
    try {
        setAutoLocation(false)
        await selectTable(wrapper, "销售")
        await wrapper.get(".table-export-button").trigger("click")
        send({ id: sent[0]!.id, result: exportResult("completed.xlsx", ["销售"], 12) })
        await flushPromises()
        expect(locationRequests).toHaveLength(2)
        send({ id: locationRequests[1]!.id, error: { code: "LOCATION_READ_FAILED", message: "最近导出目录读取失败" } })
        await flushPromises()
        expect(document.querySelector(".el-message--success")?.textContent).toContain("completed.xlsx")
        expect(document.querySelector(".el-message--warning")?.textContent).toContain("最近导出目录读取失败")
        expect(document.querySelector(".el-message--error")).toBeNull()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        cleanup()
    }
})

it("另一窗口改变目录后旧id失败会刷新记录, 不自动打开新的不同目录", async () => {
    const { wrapper, send, locationRequests, directoryRequests, setLocation, cleanup } = await setupTables({ location: firstLocation })
    try {
        await openButton(wrapper).trigger("click")
        expect(directoryRequests[0]!.params).toEqual({ id: firstLocation.id })
        setLocation(secondLocation)
        send({ id: directoryRequests[0]!.id, error: { code: "EXPORT_LOCATION_CHANGED", message: "导出目录记录已变化, 请刷新后再打开" } })
        await flushPromises()
        expect(locationRequests).toHaveLength(2)
        expect(directoryRequests).toHaveLength(1)
        expect(wrapper.text()).toContain(secondLocation.displayPath)
        await openButton(wrapper).trigger("click")
        expect(directoryRequests[1]!.params).toEqual({ id: secondLocation.id })
        send({ id: directoryRequests[1]!.id, result: { opened: true, isSimulation: false } })
        await flushPromises()
    } finally {
        cleanup()
    }
})

it("目录初始化回包在卸载后不展示路径或迟到警告", async () => {
    const { locationRequests, replyLocation, unmount, cleanup } = await setupTables({ autoLocation: false })
    try {
        expect(locationRequests).toHaveLength(1)
        unmount()
        replyLocation(locationRequests[0]!, { ...firstLocation, warning: "迟到目录读取警告" })
        await flushPromises()
        expect(document.body.textContent).not.toContain("迟到目录读取警告")
    } finally {
        cleanup()
    }
})

it("目录打开终态在卸载后不弹出迟到失败", async () => {
    const { wrapper, directoryRequests, send, unmount, cleanup } = await setupTables({ location: firstLocation })
    try {
        await openButton(wrapper).trigger("click")
        unmount()
        send({ id: directoryRequests[0]!.id, error: { code: "EXPORT_DIRECTORY_FAILED", message: "迟到目录打开失败" } })
        await flushPromises()
        expect(document.body.textContent).not.toContain("迟到目录打开失败")
    } finally {
        cleanup()
    }
})

it("目录打开进入提交后取消按钮禁用, 等原打开终态才释放busy与共享锁", async () => {
    const { wrapper, directoryRequests, sent, send, cleanup } = await setupTables({ location: firstLocation })
    try {
        await selectTable(wrapper, "销售")
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeUndefined()
        await openButton(wrapper).trigger("click")
        await flushPromises()
        send({ requestPhase: { requestId: directoryRequests[0]!.id, phase: "committing" } })
        await flushPromises()
        const cancel = document.body.querySelector<HTMLButtonElement>(".operation-message button")!
        expect(cancel.disabled).toBe(true)
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        expect(openButton(wrapper).attributes("disabled")).toBeDefined()
        expect(wrapper.get(".table-export-button").attributes("disabled")).toBeDefined()
        cancel.click()
        await flushPromises()
        expect(sent).toHaveLength(0)
        expect(document.body.querySelector(".operation-message")).not.toBeNull()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        send({ id: directoryRequests[0]!.id, result: { opened: true, isSimulation: false } })
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(document.body.querySelector(".operation-message")).toBeNull()
    } finally {
        cleanup()
    }
})
