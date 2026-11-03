/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfSearch.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地函数名称大小写无关匹配, 与宿主共用连续优先的有序子序列规则.
 */
/** 不扩张UTF-16字符的简单大写折叠, 保留原文索引. */
function fold(character: string): string {
    const upper = character.toUpperCase()
    return upper.length === 1 ? upper : character
}
/** 比较原字符或单字符大写, 不按显示语言改变业务名称. */
function same(first: string, second: string): boolean {
    return first === second || fold(first) === fold(second)
}
/** 只匹配函数名称, 返回 UTF-16 [起点, 长度] 区间; 未命中返回 null, 空查询返回空区间. */
export function matchUdfName(name: string, query: string): number[][] | null {
    const needle = query.trim()
    if (!needle) return []
    // 按UTF-16字符比较, 不让İ/ß等大小写展开改变原文索引, 与宿主返回的区间坐标一致.
    for (let start = 0; start <= name.length - needle.length; start++) {
        let matched = true
        for (let index = 0; index < needle.length; index++)
            if (!same(name[start + index]!, needle[index]!)) {
                matched = false
                break
            }
        if (matched) return [[start, needle.length]]
    }
    const ranges: [number, number][] = []
    let offset = 0
    for (let queryIndex = 0; queryIndex < needle.length; queryIndex++) {
        const character = needle[queryIndex]!
        let index = offset
        while (index < name.length && !same(name[index]!, character)) index++
        if (index === name.length) index = -1
        if (index < 0) return null
        const last = ranges.at(-1)
        if (last && last[0] + last[1] === index) last[1] += character.length
        else ranges.push([index, character.length])
        offset = index + character.length
    }
    return ranges
}
