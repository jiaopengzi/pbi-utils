/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfSelectionValidation.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享包和本地依赖按固定版本自动解析预发行, 保留闭包预算与取消保护.
 */
import { expect, it, vi } from "vitest"
import { validateUdfSelections } from "@/features/scaffold/udfSelectionValidation"
import type { UdfPackage, UdfSelection, LocalUdfEntry } from "@/features/scaffold/contracts"
import type { UdfRequest } from "@/composables/features/scaffold/useUdfRequests"
const hash = "c".repeat(64)
/** 构造精确包和根选择, 不调用网络或文件系统. */
function pkg(id: string, version: string, dependencies: { id: string; version: string }[] = []): UdfPackage {
    return {
        id,
        version,
        hash,
        description: "",
        readme: "",
        licenseFiles: {},
        dependencies,
        functions: [{ name: `${id}.Function`, expression: "() => 1", description: "", annotations: {} }],
        retrievedAt: "2026-10-23T00:00:00Z",
        fromCache: true,
        committedAt: null,
    }
}
function selection(id: string): UdfSelection {
    return { source: "daxlib", packageId: id, version: "1.0.0", baseline: hash, catalogBaseline: hash, functions: [`${id}.Function`] }
}
const statuses = { daxlib: { initialized: true, baseline: hash, retrievedAt: "2026-10-23T00:00:00Z", packageCount: 3, versionCount: 3, functionCount: 3 } }
const packages = {
    A: pkg("A", "1.0.0", [{ id: "B", version: "1.0.0-beta" }]),
    B: pkg("B", "1.0.0-beta"),
    C: pkg("C", "1.0.0", [{ id: "A", version: "1.0.0" }]),
}
it("稳定根共享预发行依赖时自动解析固定版本, 两种根顺序均保持选择", async () => {
    const request = (async (_method: string, params: { packageId: keyof typeof packages }) => ({ package: packages[params.packageId] })) as UdfRequest
    for (const next of [
        [selection("A"), selection("C")],
        [selection("C"), selection("A")],
    ]) {
        const before = JSON.stringify(next)
        // oxlint-disable-next-line no-await-in-loop -- 分别复现两个根顺序, 完整观察拒绝终态再比较原选择.
        await expect(validateUdfSelections(request, next, statuses, () => new Error("invalid"))).resolves.toHaveProperty("daxlib/b")
        expect(JSON.stringify(next)).toBe(before)
    }
})
it("多个本地根复用预发行远端依赖时自动解析闭包", async () => {
    const approved: LocalUdfEntry = {
        id: "approved",
        baseline: hash,
        definition: {
            schemaVersion: 1,
            name: "Local.Approved",
            version: "1.0.0",
            expression: "() => 1",
            description: "",
            dependencies: [{ source: "daxlib", packageId: "A", version: "1.0.0", function: "A.Function", baseline: hash }],
        },
    }
    const strict: LocalUdfEntry = {
        id: "strict",
        baseline: hash,
        definition: {
            schemaVersion: 1,
            name: "Local.Strict",
            version: "1.0.0",
            expression: "() => 1",
            description: "",
            dependencies: [{ source: "local", packageId: "approved", version: "1.0.0", function: "Local.Approved", baseline: hash }],
        },
    }
    const request = (async (method: string, params: { id?: string; packageId: keyof typeof packages }) =>
        method === "udf.localGet" ? (params.id === "strict" ? strict : approved) : { package: packages[params.packageId] }) as UdfRequest
    const next: UdfSelection[] = [approved, strict].map((item) => ({
        source: "local",
        packageId: item.id,
        version: item.definition.version,
        baseline: hash,
        functions: [item.definition.name],
    }))
    await expect(validateUdfSelections(request, next, statuses, () => new Error("invalid"))).resolves.toHaveProperty("daxlib/b")
})
it("35包共享DAG按精确版本线性去重, 纯缓存遍历仍观察取消", async () => {
    const nodes = Object.fromEntries(
        Array.from({ length: 35 }, (_, index) => [
            String(index),
            pkg(
                String(index),
                "1.0.0",
                [index + 1, index + 2].filter((next) => next < 35).map((next) => ({ id: String(next), version: "1.0.0" })),
            ),
        ]),
    )
    const request = vi.fn(async (_method: string, params: { packageId: string }) => ({ package: nodes[params.packageId] }))
    const current = vi.fn(() => true)
    await validateUdfSelections(request as UdfRequest, [selection("0"), selection("1")], statuses, () => new Error("invalid"), { current })
    expect(request).toHaveBeenCalledTimes(35)
    expect(current.mock.calls.length).toBeLessThan(200)
    let visits = 0
    await expect(
        validateUdfSelections(request as UdfRequest, [selection("0"), selection("1")], statuses, () => new Error("invalid"), {
            current: () => ++visits < 40,
            cancelled: () => new Error("cancelled during cached graph"),
        }),
    ).rejects.toThrow("cancelled during cached graph")
})
