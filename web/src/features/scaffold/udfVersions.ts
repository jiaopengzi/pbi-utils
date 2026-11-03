/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfVersions.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 单函数与全量 UDF 选择共用的 SemVer 标签排序和最新稳定版选择.
 */
/** 比较宿主已核验的数字标识, 避免超过 JavaScript 安全整数时精度丢失.
 * @param first 第一个十进制数字标识.
 * @param second 第二个十进制数字标识.
 * @returns 标准升序比较结果; 非数字输入由 BigInt 拒绝.
 */
function compareNumeric(first: string, second: string): number {
    const a = BigInt(first),
        b = BigInt(second)
    return a < b ? -1 : a > b ? 1 : 0
}
/** 分离精确标签的核心和预发行, 构建元数据不参与版本优先级.
 * @param version 宿主已校验的精确 SemVer 标签.
 * @returns 核心三段和预发行标识; 稳定版预发行数组为空.
 */
function versionParts(version: string) {
    const value = version.split("+")[0]!,
        separator = value.indexOf("-")
    return { core: (separator < 0 ? value : value.slice(0, separator)).split("."), prerelease: separator < 0 ? [] : value.slice(separator + 1).split(".") }
}
/** 按 SemVer 优先级降序比较已核验标签; 相同优先级保持宿主原有顺序.
 * @param first 第一个精确标签.
 * @param second 第二个精确标签.
 * @returns 标准降序比较结果, 不读取外部状态.
 */
function compareDescending(first: string, second: string): number {
    const a = versionParts(first),
        b = versionParts(second)
    for (let index = 0; index < 3; index++) {
        const difference = compareNumeric(b.core[index]!, a.core[index]!)
        if (difference) return difference
    }
    if (!a.prerelease.length || !b.prerelease.length) return Number(!b.prerelease.length) - Number(!a.prerelease.length)
    for (let index = 0; index < Math.max(a.prerelease.length, b.prerelease.length); index++) {
        const x = a.prerelease[index],
            y = b.prerelease[index]
        if (x === undefined || y === undefined) return x === undefined ? 1 : -1
        if (x === y) continue
        const numericX = /^\d+$/.test(x),
            numericY = /^\d+$/.test(y)
        if (numericX && numericY) {
            const difference = compareNumeric(y, x)
            if (difference) return difference
        } else if (numericX !== numericY) return numericX ? 1 : -1
        else return x < y ? 1 : -1
    }
    return 0
}
/** 复制并按 SemVer 版本从高到低排列宿主精确 tag, 不修改缓存或选择.
 * @param versions 宿主已校验的版本列表.
 * @returns 新排序数组, 构建元数据相异的同优先级版本保留原顺序.
 */
export function sortUdfVersions(versions: readonly string[]): string[] {
    return versions.toSorted(compareDescending)
}
/** 从宿主已验证的精确标签中按数字选择最新稳定版.
 * @param versions 宿主提供的版本列表.
 * @returns 最新稳定标签, 没有稳定版时返回空字符串.
 */
export function latestStableUdfVersion(versions: string[]): string {
    return sortUdfVersions(versions.filter((item) => !item.split("+")[0]!.includes("-")))[0] ?? ""
}
