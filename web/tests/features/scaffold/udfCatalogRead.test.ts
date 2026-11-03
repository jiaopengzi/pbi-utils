/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfCatalogRead.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证本地目录分页与精确依赖闭包的完整性、基线和循环预算保护.
 */
import { expect, it, vi } from "vitest"
import { readUdfCatalogPages } from "@/features/scaffold/udfCatalogPages"
import { readUdfDependencyPreview } from "@/features/scaffold/udfDependencyPreview"
import type { UdfRequest } from "@/composables/features/scaffold/useUdfRequests"
import type { UdfPackage } from "@/features/scaffold/contracts"
/** 构造同一来源内的精确包事实, 不执行真实来源访问. */
function pkg(id: string, dependencies: { id: string; version: string }[] = []): UdfPackage {
    return {
        id,
        version: "1.0.0",
        hash: "c".repeat(64),
        description: "",
        readme: "",
        licenseFiles: { LICENSE: id },
        dependencies,
        functions: [{ name: id, expression: "() => 1", description: "", annotations: {} }],
        retrievedAt: "2026-10-23T00:00:00Z",
        fromCache: true,
        committedAt: null,
    }
}
const invalid = () => new Error("invalid local catalog")
it("目录响应缺少分页字段时拒绝整轮读取, 不把不完整响应当成空目录", async () => {
    const request = vi.fn(() => Promise.resolve({ packages: [] }))
    await expect(readUdfCatalogPages(request as unknown as UdfRequest, { source: "daxlib" }, false, invalid)).rejects.toThrow("invalid local catalog")
})
it("全部分页均在同一目录见证下读取且顺序合并, 只发送空查询", async () => {
    const request = vi.fn((_method: string, params: { offset: number; catalogBaseline?: string }) =>
        Promise.resolve({
            packages: [{ id: `Example.${params.offset}`, version: "1.0.0", description: "" }],
            total: 2,
            nextOffset: params.offset === 0 ? 1 : null,
            status: { baseline: "c".repeat(64) },
        }),
    )
    const result = await readUdfCatalogPages(request as unknown as UdfRequest, { source: "daxlib", catalogBaseline: "c".repeat(64) }, false, invalid)
    expect(result.packages?.map((item) => item.id)).toEqual(["Example.0", "Example.1"])
    expect(request.mock.calls.map(([, params]) => params.offset)).toEqual([0, 1])
    expect(request.mock.calls.every(([, params]) => params.catalogBaseline === "c".repeat(64))).toBe(true)
})
it.each(["duplicate", "stalled", "changed", "truncated"])("分页 %s 破坏完整性时拒绝整轮目录", async (failure) => {
    const request = vi.fn((_method: string, params: { offset: number }) =>
        Promise.resolve({
            packages: [{ id: failure === "duplicate" ? "Same" : `Example.${params.offset}`, version: "1.0.0", description: "" }],
            total: 2,
            nextOffset: failure === "stalled" ? 0 : params.offset === 0 && failure !== "truncated" ? 1 : null,
            status: { baseline: failure === "changed" && params.offset > 0 ? "d".repeat(64) : "c".repeat(64) },
        }),
    )
    await expect(readUdfCatalogPages(request as unknown as UdfRequest, { source: "daxlib", catalogBaseline: "c".repeat(64) }, false, invalid)).rejects.toThrow(
        "invalid local catalog",
    )
})
it("递归依赖只读取同来源的精确版本并保留许可事实, 重复共享依赖只读取一次", async () => {
    const entries = {
        Root: pkg("Root", [
            { id: "One", version: "1.0.0" },
            { id: "Two", version: "1.0.0" },
        ]),
        One: pkg("One", [{ id: "Two", version: "1.0.0" }]),
        Two: pkg("Two"),
    }
    const request = vi.fn((_method: string, params: { packageId: keyof typeof entries }) => Promise.resolve({ package: entries[params.packageId] }))
    const result = await readUdfDependencyPreview(
        request as unknown as UdfRequest,
        { source: "source:example", sourceBaseline: "a".repeat(64), catalogBaseline: "b".repeat(64) },
        entries.Root,
        invalid,
    )
    expect(result.map((item) => item.id)).toEqual(["One", "Two"])
    expect(result[1]?.licenseFiles).toEqual({ LICENSE: "Two" })
    expect(request).toHaveBeenCalledTimes(2)
    expect(request.mock.calls[0]?.[1]).toMatchObject({
        source: "source:example",
        sourceBaseline: "a".repeat(64),
        catalogBaseline: "b".repeat(64),
        version: "1.0.0",
    })
})
it.each(["cycle", "version", "budget"])("精确依赖 %s 越界必须失败, 不返回部分闭包", async (failure) => {
    const root = pkg("Root", [{ id: "Node1", version: "1.0.0" }])
    const request = vi.fn((_method: string, params: { packageId: string }) => {
        const number = Number(params.packageId.slice(4))
        const current = pkg(
            params.packageId,
            failure === "cycle"
                ? [{ id: "Root", version: "1.0.0" }]
                : failure === "version"
                  ? [{ id: "Root", version: "2.0.0" }]
                  : [{ id: `Node${number + 1}`, version: "1.0.0" }],
        )
        return Promise.resolve({ package: current })
    })
    await expect(readUdfDependencyPreview(request as unknown as UdfRequest, { source: "daxlib" }, root, invalid)).rejects.toThrow("invalid local catalog")
    expect(request.mock.calls.length).toBeLessThanOrEqual(128)
})
