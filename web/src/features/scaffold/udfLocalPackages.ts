/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfLocalPackages.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 以宿主已验证的函数名聚合本地逻辑包, 保留每个tag的精确GUID与基线.
 */
import type { LocalUdfEntry } from "./contracts"
import { latestStableUdfVersion, sortUdfVersions } from "./udfVersions"
/** 逻辑包仅影响展示与当前tag选择, 不替代宿主精确版本ID. */
export interface LocalUdfPackage {
    key: string
    name: string
    versions: LocalUdfEntry[]
}
/** 将宿主验证的ASCII函数名转为大小写无关逻辑身份, 不修改原始名称. */
export function localUdfPackageKey(name: string): string {
    return name.toLowerCase()
}
/** 按原始字符稳定排序, 不受系统语言影响. */
function ordinal(first: string, second: string): number {
    return first < second ? -1 : first > second ? 1 : 0
}
/** 按函数名分组并按SemVer降序列出精确tag; 构建元数据同优先级时按tag/GUID确定顺序, 输入不变. */
export function groupLocalUdfPackages(items: LocalUdfEntry[]): LocalUdfPackage[] {
    const groups = new Map<string, LocalUdfEntry[]>()
    for (const item of items) {
        const key = localUdfPackageKey(item.definition.name),
            entries = groups.get(key) ?? []
        if (entries.some((entry) => entry.definition.version === item.definition.version)) throw new Error("Duplicate local UDF package version")
        entries.push(item)
        groups.set(key, entries)
    }
    return [...groups.entries()]
        .toSorted(([a], [b]) => ordinal(a, b))
        .map(([key, entries]) => {
            const sorted = entries.toSorted((a, b) => ordinal(a.definition.version, b.definition.version) || ordinal(a.id, b.id))
            const tags = sortUdfVersions([...new Set(sorted.map((item) => item.definition.version))]),
                ranks = new Map(tags.map((tag, index) => [tag, index]))
            const versions = sorted.toSorted((a, b) => ranks.get(a.definition.version)! - ranks.get(b.definition.version)!)
            const group = { key, name: versions[0]!.definition.name, versions }
            group.name = preferredLocalUdfEntry(group).definition.name
            return group
        })
}
/** 当前GUID存在时优先该tag, 否则最新稳定版, beta-only使用最高tag; 空组为调用错误. */
export function preferredLocalUdfEntry(group: LocalUdfPackage, selectedId?: string): LocalUdfEntry {
    const selected = group.versions.find((item) => item.id === selectedId)
    if (selected) return selected
    const stable = latestStableUdfVersion(group.versions.map((item) => item.definition.version))
    const result = group.versions.find((item) => item.definition.version === stable) ?? group.versions[0]
    if (!result) throw new RangeError("Local UDF package has no versions")
    return result
}
