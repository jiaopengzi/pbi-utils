/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfVersions.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证精确 tag 按 SemVer 降序展示, 保留稳定版默认及相同优先级的宿主顺序.
 */
import { expect, it } from "vitest"
import { sortUdfVersions, latestStableUdfVersion } from "@/features/scaffold/udfVersions"

it("版本降序按主次修订数字比较, 不改变宿主原数组", () => {
    const versions = ["1.9.0", "1.10.0", "2.0.0", "1.10.2", "1.10.11"]
    const before = [...versions]
    expect(sortUdfVersions(versions)).toEqual(["2.0.0", "1.10.11", "1.10.2", "1.10.0", "1.9.0"])
    expect(versions).toEqual(before)
})

it("同核心稳定版在预发行前, 数字标识和 ASCII 标识遵守 SemVer 优先级", () => {
    const ascending = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"]
    expect(sortUdfVersions(ascending)).toEqual(ascending.toReversed())
    expect(sortUdfVersions(["2.0.0-beta.2", "1.9.9", "2.0.0-beta.10"])).toEqual(["2.0.0-beta.10", "2.0.0-beta.2", "1.9.9"])
    expect(sortUdfVersions(["1.0.0-Z", "1.0.0-a", "1.0.0-9"])).toEqual(["1.0.0-a", "1.0.0-Z", "1.0.0-9"])
})

it("构建元数据不改变优先级, 纯数字标识不因超过安全整数范围失序", () => {
    expect(sortUdfVersions(["1.0.0+build.2", "1.0.0+build.10", "1.0.0", "1.0.0-beta+stable"])).toEqual([
        "1.0.0+build.2",
        "1.0.0+build.10",
        "1.0.0",
        "1.0.0-beta+stable",
    ])
    expect(sortUdfVersions(["1.0.0-beta.9007199254740992", "1.0.0-beta.9007199254740993"])).toEqual([
        "1.0.0-beta.9007199254740993",
        "1.0.0-beta.9007199254740992",
    ])
})

it("默认 latest 始终选最新稳定版, 空列表和仅预发行不产生隐式选择", () => {
    expect(latestStableUdfVersion(["3.0.0-beta.1", "1.9.0", "1.10.0+release", "1.10.0-beta.9"])).toBe("1.10.0+release")
    expect(latestStableUdfVersion([])).toBe("")
    expect(latestStableUdfVersion(["1.0.0-beta+build.1"])).toBe("")
})
