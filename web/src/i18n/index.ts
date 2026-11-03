/**
 * FilePath    : pbi-utils\web\src\i18n\index.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享中文英文偏好与本地字典, 不翻译模型定义或用户原文.
 */
import { computed } from "vue"
import { daxFormatStyle, locale, showRecentFiles } from "./state"
import type { UiSettings } from "@/bridge/client"
import zhCn from "element-plus/es/locale/lang/zh-cn"
import en from "element-plus/es/locale/lang/en"
import { appMessages } from "./appMessages"
import { settingsMessages } from "./settingsMessages"
import { updateMessages } from "./updateMessages"
import { diagnosticMessages } from "./diagnosticMessages"
import { modelMessages } from "./modelMessages"
import { measureFieldMessages } from "./measureFieldMessages"
import { workspaceMessages } from "./workspaceMessages"
import { mcpMessages } from "./mcpMessages"
import { powerBiMessages } from "./powerBiMessages"
import { sampleMessages } from "./sampleMessages"
import { hostMessages } from "./hostMessages"
import { hostDialogMessages } from "./hostDialogMessages"
import { scaffoldMessages } from "./scaffoldMessages"
import { configurationMessages } from "./configurationMessages"
import { learningMessages } from "./learningMessages"

export type AppLocale = "zh-cn" | "en-us"
type Messages = Record<string, string>
const dictionaries: Record<AppLocale, Messages> = {
    "zh-cn": {
        ...appMessages["zh-cn"],
        ...settingsMessages["zh-cn"],
        ...updateMessages["zh-cn"],
        ...diagnosticMessages["zh-cn"],
        ...modelMessages["zh-cn"],
        ...measureFieldMessages["zh-cn"],
        ...workspaceMessages["zh-cn"],
        ...mcpMessages["zh-cn"],
        ...powerBiMessages["zh-cn"],
        ...sampleMessages["zh-cn"],
        ...hostDialogMessages["zh-cn"],
        ...scaffoldMessages["zh-cn"],
        ...configurationMessages["zh-cn"],
        ...learningMessages["zh-cn"],
    },
    "en-us": {
        ...appMessages["en-us"],
        ...settingsMessages["en-us"],
        ...updateMessages["en-us"],
        ...diagnosticMessages["en-us"],
        ...modelMessages["en-us"],
        ...measureFieldMessages["en-us"],
        ...workspaceMessages["en-us"],
        ...mcpMessages["en-us"],
        ...powerBiMessages["en-us"],
        ...sampleMessages["en-us"],
        ...hostDialogMessages["en-us"],
        ...scaffoldMessages["en-us"],
        ...configurationMessages["en-us"],
        ...learningMessages["en-us"],
    },
}
const fixedKeys = new Map<string, string>()
for (const [key, original] of Object.entries(dictionaries["zh-cn"])) {
    if (!fixedKeys.has(original)) fixedKeys.set(original, key)
    const english = dictionaries["en-us"][key]
    if (english && !fixedKeys.has(english)) fixedKeys.set(english, key)
}
const hostOriginals = new Map(Object.entries(hostMessages).map(([original, english]) => [english, original]))
export const elementLocale = computed(() => (locale.value === "en-us" ? en : zhCn))

/** 设置已由宿主成功保存的白名单语言, 不在网页存储中另建偏好来源. */
export function setLocale(value: AppLocale): void {
    if (value !== "zh-cn" && value !== "en-us") throw new Error("Unsupported UI locale")
    locale.value = value
    if (typeof document !== "undefined") document.documentElement.lang = value
}

/** 同步宿主已读取或成功保存的界面偏好, 先校验全部字段以避免只更新部分设置. */
export function setUiSettings(value: Pick<UiSettings, "locale" | "showRecentFiles" | "daxFormatStyle">): void {
    if (
        (value.locale !== "zh-cn" && value.locale !== "en-us") ||
        typeof value.showRecentFiles !== "boolean" ||
        (value.daxFormatStyle !== "long" && value.daxFormatStyle !== "short")
    )
        throw new Error("Unsupported UI settings")
    setLocale(value.locale)
    showRecentFiles.value = value.showRecentFiles
    daxFormatStyle.value = value.daxFormatStyle
}

/** 按当前语言读取本地键并替换具名参数, 未知键保留键以暴露开发缺项. */
export function t(key: string, params: Record<string, string | number> = {}): string {
    const current = dictionaries[locale.value]
    const fallback = dictionaries["zh-cn"]
    const message = Object.hasOwn(current, key) ? current[key]! : Object.hasOwn(fallback, key) ? fallback[key]! : key
    return message.replace(/\{([a-zA-Z0-9_]+)\}/g, (token, name: string) => (Object.hasOwn(params, name) ? String(params[name]) : token))
}

/** 仅翻译已知固定宿主消息, 未知诊断和用户内容保持权威原文. */
export function hostText(value: string | null | undefined): string {
    if (!value) return ""
    const cleanupMessages = ["本轮临时目录清理失败, 可能仍有临时文件.", "导出临时文件清理失败, 请检查目标目录"]
    for (const message of cleanupMessages) {
        const suffix = [message, hostMessages[message]].find((candidate) => candidate && value.endsWith(`; ${candidate}`))
        if (suffix) return `${hostText(value.slice(0, -suffix.length - 2))}; ${hostText(message)}`
    }
    const translated = Object.hasOwn(hostMessages, value) ? hostMessages[value] : undefined
    if (translated) return locale.value === "en-us" ? translated : value
    const original = hostOriginals.get(value)
    if (original) return locale.value === "en-us" ? value : original
    if (locale.value === "en-us") {
        const schema = value.match(/^(MDSCHEMA_[A-Z_]+) 未提供 \(([A-Za-z0-9_.]+), (0x[0-9A-Fa-f]{8})\); 此部分未读取, 不代表对象数量为零$/)
        if (schema) return `${schema[1]} is unavailable (${schema[2]}, ${schema[3]}). This part was not read; the object count is not confirmed to be zero.`
        const http = value.match(/^Power BI 服务请求失败 \(HTTP (\d{3})\), 请稍后重试$/)
        if (http) return `Power BI service request failed (HTTP ${http[1]}). Retry later.`
        if (value.startsWith("模型修改已保存；")) return `Model changes were saved. ${hostText(value.slice(8))}`
        if (value.startsWith("PBIP 文件已保存；")) return `The PBIP file was saved. ${hostText(value.slice("PBIP 文件已保存；".length))}`
    }
    const key = fixedKeys.get(value)
    if (key) return t(key)
    return value
}

export { t as translate }
