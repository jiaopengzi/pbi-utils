/**
 * FilePath    : pbi-utils\web\tests\setup.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为 jsdom 组件测试补齐受限 API, 提供简体中文配置和独立弹层清理, 不替换 Element Plus 组件.
 */
import { testDiagnosticsPlugin } from "./support/bootstrapDiagnostics"
import { installDomApiStubs } from "./support/domApis"
import { afterEach } from "vitest"
import { config } from "@vue/test-utils"
import ElementPlus, { ElMessage } from "element-plus"
import zhCn from "element-plus/es/locale/lang/zh-cn"
import { setUiSettings } from "@/i18n"
installDomApiStubs()
config.global.plugins = [[ElementPlus, { locale: zhCn }], testDiagnosticsPlugin]
afterEach(() => {
    setUiSettings({ locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "long" })
    // 同时清理分组实例与 DOM, 避免下一用例将相同消息合并进已移除的节点.
    ElMessage.closeAll()
    document.body.innerHTML = ""
})
