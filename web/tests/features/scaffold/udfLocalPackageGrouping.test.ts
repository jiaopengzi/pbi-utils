/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfLocalPackageGrouping.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地逻辑包分组与SemVer规则保留精确版本GUID和基线.
 */
import { expect, it } from "vitest"
import { groupLocalUdfPackages, localUdfPackageKey, preferredLocalUdfEntry } from "@/features/scaffold/udfLocalPackages"
import type { LocalUdfEntry } from "@/features/scaffold/contracts"
/** 构造不同精确宿主ID的同名版本. */
function entry(id: string, name: string, version: string): LocalUdfEntry {
    return { id, baseline: "a".repeat(64), definition: { schemaVersion: 1, name, version, expression: "() => 1", description: "", dependencies: [] } }
}
it("同名大小写合并, tag按SemVer降序且不修改输入", () => {
    const items = [
        entry("old", "Local.Calc", "1.9.0"),
        entry("new", "local.calc", "1.10.0"),
        entry("beta", "Local.Calc", "2.0.0-beta.10"),
        entry("other", "Local.Other", "1.0.0"),
    ]
    const original = structuredClone(items),
        groups = groupLocalUdfPackages(items)
    expect(groups).toHaveLength(2)
    expect(groups[0]?.key).toBe(localUdfPackageKey("LOCAL.CALC"))
    expect(groups[0]?.versions.map((item) => item.id)).toEqual(["beta", "new", "old"])
    expect(items).toEqual(original)
})
it("精确当前GUID优先, 默认最新稳定, beta-only使用最高tag", () => {
    const group = groupLocalUdfPackages([entry("old", "Calc", "1.9.0"), entry("new", "Calc", "1.10.0"), entry("beta", "Calc", "2.0.0-beta")])[0]!
    expect(preferredLocalUdfEntry(group, "old").id).toBe("old")
    expect(preferredLocalUdfEntry(group).id).toBe("new")
    expect(preferredLocalUdfEntry(group, "deleted").id).toBe("new")
    const beta = groupLocalUdfPackages([entry("b2", "Beta", "1.0.0-beta.2"), entry("b10", "Beta", "1.0.0-beta.10")])[0]!
    expect(preferredLocalUdfEntry(beta).id).toBe("b10")
})
it("构建元数据tag并存且排序确定, 空包拒绝伪造成功", () => {
    const a = entry("a", "Build", "1.0.0+build-a"),
        b = entry("b", "Build", "1.0.0+build-b")
    expect(groupLocalUdfPackages([b, a])[0]?.versions.map((item) => item.id)).toEqual(groupLocalUdfPackages([a, b])[0]?.versions.map((item) => item.id))
    expect(() => preferredLocalUdfEntry({ key: "empty", name: "Empty", versions: [] })).toThrow()
})

it("同逻辑名相同精确tag重复时拒绝歧义, 不按GUID任取一条", () => {
    expect(() => groupLocalUdfPackages([entry("a", "Local.Calc", "1.0.0"), entry("b", "local.calc", "1.0.0")])).toThrow("Duplicate local UDF package version")
})
