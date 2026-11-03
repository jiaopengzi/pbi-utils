/**
 * FilePath    : pbi-utils\web\src\ui\message.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共用浮动消息提示, 自动关闭并合并重复反馈, 不挤占页面布局.
 */
import { ElMessage } from "element-plus"
import { hostText } from "@/i18n"
// 浮动提示也走品牌 SCSS, 默认 CSS 会重新注入基础变量并覆盖全局品牌色.
import "element-plus/es/components/message/style/index"

/** 显示纯文本浮动提示; 相同消息合并, 不改变调用页面的高度. */
export function showMessage(message: string, type: "success" | "error" | "warning" | "info" = "success") {
    ElMessage({
        message: hostText(message),
        type,
        customClass: "app-feedback-message",
        showClose: true,
        grouping: true,
        duration: type === "error" ? 6000 : 3000,
    })
}
