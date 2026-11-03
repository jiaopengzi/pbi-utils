/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfSourceRepository.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 来源表单和显式模拟共用的 HTTP(S) Git 仓库地址及引用校验.
 */

/** 离线规范多平台和内网仓库地址, 宿主仍独立验证真实连接与内容.
 * @param value 不含首尾空白、凭据、查询或片段的 HTTP(S) 仓库地址.
 * @returns GitHub 地址规范为小写 owner/repo; 其他平台保留路径大小写和 .git 路由. 非法地址抛出固定错误.
 */
export function normalizeUdfRepositoryUrl(value: string): string {
    if (value.length > 2048 || /[^\x21-\x7e]/.test(value) || /[%\\?#@]/.test(value)) throw invalid()
    const address = /^(https?):\/\/([^/]+)\/(.+?)\/?$/i.exec(value)
    if (!address || address[0] !== value) throw invalid()
    const segments = address[3]!.split("/")
    if (segments.some((part) => part.length > 255 || !/^[a-z0-9_.-]+$/i.test(part) || [".", "..", ".git"].includes(part))) throw invalid()
    let parsed: URL
    try {
        parsed = new URL(value)
    } catch {
        throw invalid()
    }
    if (
        !parsed.hostname ||
        parsed.hostname === "localhost" ||
        parsed.hostname.endsWith(".localhost") ||
        parsed.hostname.endsWith(".") ||
        (parsed.port && Number(parsed.port) < 1)
    )
        throw invalid()
    if (parsed.hostname !== "github.com") return `${parsed.origin}/${segments.join("/")}`
    const match = /^https:\/\/github\.com(?::443)?\/([a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?)\/([a-z0-9_.-]{1,104})\/?$/i.exec(value)
    if (!match || match[0] !== value) throw invalid()
    const repository = match[2]!.replace(/\.git$/i, "")
    if (repository.length < 1 || repository.length > 100 || [".", ".."].includes(repository)) throw invalid()
    return `https://github.com/${match[1]!.toLowerCase()}/${repository.toLowerCase()}`
}

/** 地址校验仅返回固定安全错误, 不在消息中回显仓库地址或潜在凭据. */
function invalid(): Error {
    return new Error("UDF_SOURCE_REPOSITORY_INVALID")
}

/** 校验分支、标签或提交语法, 不联网解析引用.
 * @param value null 表示使用默认分支, 非空输入最多 256 个字符.
 * @returns 原引用或 null; 不安全的引用抛出固定错误.
 */
export function validateUdfRepositoryReference(value: string | null): string | null {
    if (value === null) return null
    if (
        value.length < 1 ||
        value.length > 256 ||
        value.startsWith("-") ||
        value.startsWith("/") ||
        value.endsWith("/") ||
        value.endsWith(".") ||
        value.includes("..") ||
        value.includes("@{") ||
        value.includes("//") ||
        hasControlCharacters(value) ||
        [...value].some((character) => /\s/u.test(character) || "~^:?*[\\".includes(character)) ||
        value === "@" ||
        value.split("/").some((part) => part.startsWith(".") || /\.lock$/i.test(part))
    )
        throw new Error("UDF_SOURCE_PROTOCOL_INVALID")
    return value
}

/** 校验纯显示名称, 与宿主保持 80 字符及控制字符边界.
 * @param value 已去除首尾空白的来源名称.
 * @returns 原名称; 非法名称抛出固定错误.
 */
export function validateUdfSourceName(value: string): string {
    if (!value || value !== value.trim() || value.length > 80 || hasControlCharacters(value)) throw new Error("UDF_SOURCE_INVALID")
    return value
}

/** 按 Unicode 控制字符范围检查文本, 返回是否存在无法显示的控制字符. */
function hasControlCharacters(value: string): boolean {
    return [...value].some((character) => {
        const code = character.charCodeAt(0)
        return code <= 31 || (code >= 127 && code <= 159)
    })
}
