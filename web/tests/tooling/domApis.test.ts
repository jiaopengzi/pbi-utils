/**
 * FilePath    : pbi-utils\web\tests\tooling\domApis.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 DOM 测试几何桩和剪贴板显式模拟边界, 不宣称真实布局或系统复制成功.
 */
import { expect, it, vi } from "vitest"
import { installDomApiStubs } from "../support/domApis"

it("Range 测量仅提供零尺寸接口, 不伪装真实布局", () => {
    expect(typeof Range.prototype.getClientRects).toBe("function")
    expect(typeof Range.prototype.getBoundingClientRect).toBe("function")
    const range = document.createRange()
    const rectangles = range.getClientRects()
    expect(rectangles.length).toBe(0)
    expect(rectangles.item(0)).toBeNull()
    const rectangle = range.getBoundingClientRect()
    expect(rectangle).toBeInstanceOf(DOMRect)
    expect([rectangle.x, rectangle.y, rectangle.width, rectangle.height]).toEqual([0, 0, 0, 0])
})

it("未由具体测试模拟的剪贴板写入必须拒绝", async () => {
    expect(navigator.clipboard).toBeDefined()
    await expect(navigator.clipboard.writeText("未经模拟的复制")).rejects.toThrow("剪贴板写入必须由具体测试显式模拟")
})

it("具体复制动作可以精确模拟, 恢复后继续拒绝未模拟写入", async () => {
    expect(navigator.clipboard).toBeDefined()
    const copy = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    try {
        await navigator.clipboard.writeText("SUM('销售'[金额])")
        expect(copy).toHaveBeenCalledExactlyOnceWith("SUM('销售'[金额])")
    } finally {
        copy.mockRestore()
    }
    await expect(navigator.clipboard.writeText("恢复后的复制")).rejects.toThrow("剪贴板写入必须由具体测试显式模拟")
})

it("重复初始化保留已有的几何和剪贴板实现, 不覆盖具体用例的模拟", () => {
    const rectangles = vi.spyOn(Range.prototype, "getClientRects")
    const rectangle = vi.spyOn(Range.prototype, "getBoundingClientRect")
    const copy = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    try {
        installDomApiStubs()
        expect(Range.prototype.getClientRects).toBe(rectangles)
        expect(Range.prototype.getBoundingClientRect).toBe(rectangle)
        expect(navigator.clipboard.writeText).toBe(copy)
    } finally {
        vi.restoreAllMocks()
    }
})
