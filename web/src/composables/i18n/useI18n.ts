/**
 * FilePath    : pbi-utils\web\src\composables\i18n\useI18n.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为页面提供同一语言与偏好状态及当前本地翻译函数.
 */

import { daxFormatStyle, locale, showRecentFiles } from "@/i18n/state"
import { t, hostText } from "@/i18n"

/** 为所有页面返回同一语言状态和本地文案函数, 无需额外网络服务. */
export function useI18n() {
    return { locale, showRecentFiles, daxFormatStyle, t, hostText }
}
