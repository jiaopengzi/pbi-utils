/**
 * FilePath    : pbi-utils\web\tests\i18n\i18n.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证双语词条完整性、参数保真及安全宿主提示的本地显示边界.
 */
import { expect, it } from "vitest"
import { setLocale, setUiSettings, t, hostText, elementLocale, type AppLocale } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import { appMessages } from "@/i18n/appMessages"
import { settingsMessages } from "@/i18n/settingsMessages"
import { diagnosticMessages } from "@/i18n/diagnosticMessages"
import { modelMessages } from "@/i18n/modelMessages"
import { workspaceMessages } from "@/i18n/workspaceMessages"
import { mcpMessages } from "@/i18n/mcpMessages"
import { powerBiMessages } from "@/i18n/powerBiMessages"
import { sampleMessages } from "@/i18n/sampleMessages"
import { hostDialogMessages } from "@/i18n/hostDialogMessages"

it("界面偏好作为整体校验, 非法最近文件开关不会提前改变共享语言", () => {
    setUiSettings({ locale: "zh-cn", showRecentFiles: true, daxFormatStyle: "long" })
    expect(() => setUiSettings({ locale: "en-us", showRecentFiles: "false" as unknown as boolean, daxFormatStyle: "long" })).toThrow("Unsupported UI settings")
    expect(useI18n().locale.value).toBe("zh-cn")
    expect(useI18n().showRecentFiles.value).toBe(true)
    expect(() => setUiSettings({ locale: "en-us", showRecentFiles: false, daxFormatStyle: "LONG" as "long" })).toThrow("Unsupported UI settings")
    expect(useI18n().locale.value).toBe("zh-cn")
    expect(useI18n().showRecentFiles.value).toBe(true)
    expect(useI18n().daxFormatStyle.value).toBe("long")
    setUiSettings({ locale: "en-us", showRecentFiles: false, daxFormatStyle: "short" })
    expect(document.documentElement.lang).toBe("en-us")
    expect(useI18n().showRecentFiles.value).toBe(false)
    expect(useI18n().daxFormatStyle.value).toBe("short")
    expect(hostText("请选择 PBIP 或 PBIX 文件")).toBe("Choose a PBIP or PBIX file.")
})

it.each(
    Object.entries({
        appMessages,
        settingsMessages,
        diagnosticMessages,
        modelMessages,
        workspaceMessages,
        mcpMessages,
        powerBiMessages,
        sampleMessages,
        hostDialogMessages,
    }),
)("%s 双语键与占位参数一一对应, 英文词条不含未翻译中文", (_name, messages) => {
    expect(Object.keys(messages["en-us"]).toSorted()).toEqual(Object.keys(messages["zh-cn"]).toSorted())
    const chinese: Record<string, string> = messages["zh-cn"]
    for (const [key, english] of Object.entries(messages["en-us"])) {
        expect(english.trim()).not.toBe("")
        expect(english).not.toMatch(/\p{Script=Han}/u)
        expect(english.match(/\{\w+\}/g)?.toSorted() ?? []).toEqual(chinese[key]!.match(/\{\w+\}/g)?.toSorted() ?? [])
    }
})

it("同一偏好驱动页面与 Element Plus, 插值保留名称和特殊字符", () => {
    setLocale("en-us")
    expect(document.documentElement.lang).toBe("en-us")
    expect(elementLocale.value.name).toBe("en")
    expect(t("app.settings")).toBe("Settings")
    expect(t("model.copyCode", { language: "DAX" })).toContain("DAX")
    expect(hostText("SUM('销售'[金额]) <script> $& {name}")).toBe("SUM('销售'[金额]) <script> $& {name}")
    expect(() => setLocale("fr-fr" as AppLocale)).toThrow()
    expect(useI18n().locale.value).toBe("en-us")
    setLocale("zh-cn")
    expect(elementLocale.value.name).toBe("zh-cn")
    expect(t("app.settings")).toBe("设置")
})

it("固定安全模板保留诊断代码, 未知错误原文不猜测业务事实", () => {
    setLocale("en-us")
    expect(hostText("模型会话或提交结果待核验, 不得重复保存模型修改")).toContain("Do not save")
    expect(hostText("无法锁定当前 Desktop 模型, 未提交; 请重新读取后重试")).toContain("could not be locked")
    const schema = hostText("MDSCHEMA_HIERARCHIES 未提供 (AdomdErrorResponseException, 0x81230001); 此部分未读取, 不代表对象数量为零")
    expect(schema).toContain("MDSCHEMA_HIERARCHIES")
    expect(schema).toContain("0x81230001")
    expect(schema).toContain("not confirmed to be zero")
    expect(hostText("自定义服务返回未知错误 0x12345678")).toBe("自定义服务返回未知错误 0x12345678")
    for (const value of ["constructor", "toString", "__proto__"]) {
        expect(hostText(value)).toBe(value)
        expect(t(value)).toBe(value)
    }
    expect(hostText("模型修改已保存；未知后续状态 0x12345678")).toBe("Model changes were saved. 未知后续状态 0x12345678")
    expect(hostText("PBIP 文件已保存；未知后续状态 0x12345678")).toBe("The PBIP file was saved. 未知后续状态 0x12345678")
    expect(hostText("PBIP 文件已保存；PBIP 文件已保存, 但未确认 Desktop 完整模型同步; 请核对后重试只读验证")).not.toMatch(/\p{Script=Han}/u)
})

it("云端最近记录的固定存储与账号边界提示支持英文, 原诊断不写入界面词条", () => {
    setLocale("en-us")
    const messages = [
        "最近云端模型保存失败, 原记录保留; 可继续使用模型目录连接",
        "无法读取最近云端模型, 原记录保留; 可继续使用模型目录连接",
        "最近云端模型记录损坏或版本不支持, 原文件保留; 可继续使用模型目录连接",
        "登录状态已改变, 请重新获取模型",
        "服务模型连接被中断, 请重新连接; 当前模型保持不变",
        "账号可用环境已改变, 请重新登录并选择",
    ]
    for (const message of messages) {
        expect(hostText(message)).not.toMatch(/\p{Script=Han}/u)
        expect(hostText(message)).not.toBe(message)
    }
})
