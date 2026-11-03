/**
 * FilePath    : pbi-utils\web\src\i18n\state.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 持有唯一的界面语言、最近文件和 DAX 格式偏好状态, 不增加网页存储来源.
 */

import { ref } from "vue"
import type { AppLocale } from "./index"
import type { UiSettings } from "@/bridge/client"

export const locale = ref<AppLocale>("zh-cn")
export const showRecentFiles = ref(true)
export const daxFormatStyle = ref<UiSettings["daxFormatStyle"]>("long")
