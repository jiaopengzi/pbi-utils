/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfFunctionBrowsing.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证包摘要短页、名称模糊匹配与本地时间, 不触发整库正文读取.
 */
import { expect, it, vi } from "vitest"
import { readUdfPackagePage } from "@/features/scaffold/udfPackagePages"
import { matchUdfName } from "@/features/scaffold/udfSearch"
import { formatUdfLocalTime } from "@/features/scaffold/udfDateTime"
import type { UdfRequest } from "@/composables/features/scaffold/useUdfRequests"
const baseline = "c".repeat(64)
const invalid = () => new Error("invalid package page")
/** 构造桥接短页; 总量大于当前显示页, 以请求计数证明不会读取后续页. */
function page(offset: number, total: number, count = 20, catalogBaseline = baseline) {
    const size = Math.min(count, total - offset)
    return {
        packagePage: {
            items: Array.from({ length: size }, (_, index) => ({
                id: `SVG.Package${offset + index}`,
                version: "1.0.0",
                hash: baseline,
                description: "Package documentation",
                authors: "Author",
                tags: ["SVG"],
                committedAt: null,
                iconKey: null,
                functionCount: 3,
            })),
            total,
            nextOffset: offset + size < total ? offset + size : null,
            status: {
                initialized: true,
                baseline: catalogBaseline,
                retrievedAt: "2026-10-23T00:00:00Z",
                packageCount: 1,
                versionCount: 1,
                functionCount: total,
            },
        },
    }
}
it("短传输页只累积本次50行显示页, 不预取剩余目录或正文", async () => {
    const request = vi.fn((_method: string, params: { offset: number; limit: number }) => Promise.resolve(page(params.offset, 700, Math.min(20, params.limit))))
    const result = await readUdfPackagePage(
        request as unknown as UdfRequest,
        { source: "daxlib", catalogBaseline: baseline },
        { query: "", includePrerelease: false, offset: 0, limit: 50 },
        invalid,
    )
    expect(result.items).toHaveLength(50)
    expect(result.total).toBe(700)
    expect(result.nextOffset).toBe(50)
    expect(request.mock.calls.map(([, params]) => [params.offset, params.limit])).toEqual([
        [0, 50],
        [20, 30],
        [40, 10],
    ])
    expect(request.mock.calls.every(([method]) => method === "udf.catalog")).toBe(true)
})
it.each(["changed", "duplicate", "stalled", "truncated", "count"])("包页%s必须拒绝整轮, 保留旧列表", async (failure) => {
    const request = vi.fn((_method: string, params: { offset: number }) => {
        const response = page(params.offset, 55)
        if (failure === "changed" && params.offset > 0) response.packagePage.status.baseline = "d".repeat(64)
        if (failure === "duplicate") response.packagePage.items[1]!.id = response.packagePage.items[0]!.id
        if (failure === "stalled") response.packagePage.nextOffset = params.offset
        if (failure === "truncated") response.packagePage.nextOffset = null
        if (failure === "count") response.packagePage.items[0]!.functionCount = -1
        return Promise.resolve(response)
    })
    await expect(
        readUdfPackagePage(
            request as unknown as UdfRequest,
            { source: "daxlib", catalogBaseline: baseline },
            { query: "", includePrerelease: false, offset: 0, limit: 50 },
            invalid,
        ),
    ).rejects.toThrow("invalid package page")
})
it("名称忽略大小写且支持有序子序列, 库/说明不作为输入", () => {
    expect(matchUdfName("SVG.Circle", "svgcirc")).toEqual([
        [0, 3],
        [4, 4],
    ])
    expect(matchUdfName("SVG.Circle", "CIRCLE")).toEqual([[4, 6]])
    expect(matchUdfName("SVG.Circle", "wrong")).toBeNull()
    expect(matchUdfName("SVG.Circle", "  ")).toEqual([])
    expect(matchUdfName("<img>.*", "<.*")).toEqual([
        [0, 1],
        [5, 2],
    ])
    expect(matchUdfName("İ.Name", "i")).toBeNull()
    expect(matchUdfName("İ.Name", "name")).toEqual([[2, 4]])
})
it("UTC以当前本地时区固定两位时间展示, 未知为短横线且没有offset", () => {
    const value = new Date("2026-10-23T02:03:04Z")
    const local =
        [value.getFullYear(), String(value.getMonth() + 1).padStart(2, "0"), String(value.getDate()).padStart(2, "0")].join("-") +
        " " +
        [value.getHours(), value.getMinutes(), value.getSeconds()].map((part) => String(part).padStart(2, "0")).join(":")
    expect(formatUdfLocalTime("2026-10-23T02:03:04Z")).toBe(local)
    expect(formatUdfLocalTime(null)).toBe("-")
    expect(formatUdfLocalTime("invalid")).toBe("-")
    expect(formatUdfLocalTime("2026-10-23T02:03:04Z")).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/)
})

it("版本更新时间允许未知null和带时区绝对时间, 缺失当前契约字段时拒绝整页", async () => {
    const read = (response: ReturnType<typeof page>) =>
        readUdfPackagePage(
            (() => Promise.resolve(response)) as unknown as UdfRequest,
            { source: "daxlib", catalogBaseline: baseline },
            { query: "", includePrerelease: false, offset: 0, limit: 1 },
            invalid,
        )
    const known = page(0, 1, 1)
    Object.assign(known.packagePage.items[0]!, { committedAt: "2026-10-20T03:28:40.731292+08:00" })
    expect((await read(known)).items[0]?.committedAt).toBe("2026-10-20T03:28:40.731292+08:00")
    const missing = page(0, 1, 1)
    delete (missing.packagePage.items[0]! as Record<string, unknown>).committedAt
    await expect(read(missing)).rejects.toThrow("invalid package page")
    const malformed = page(0, 1, 1)
    Object.assign(malformed.packagePage.items[0]!, { committedAt: "not-a-Git-commit-time" })
    await expect(read(malformed)).rejects.toThrow("invalid package page")
})
