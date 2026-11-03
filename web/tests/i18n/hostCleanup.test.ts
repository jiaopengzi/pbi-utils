/**
 * FilePath    : pbi-utils\web\tests\i18n\hostCleanup.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 固定终态清理后缀独立翻译, 未知主错误保持原文.
 */
import { afterEach, expect, it } from "vitest"
import { hostText } from "@/i18n"
import { locale } from "@/i18n/state"
afterEach(() => {
    locale.value = "zh-cn"
})
it("只翻译已知终态清理后缀并保留未知主诊断", () => {
    locale.value = "en-us"
    expect(hostText("Unknown primary; 本轮临时目录清理失败, 可能仍有临时文件.")).toBe(
        "Unknown primary; Cleaning up this operation's temporary directory failed. Temporary files may remain.",
    )
    expect(hostText("Unknown primary; 未知清理诊断")).toBe("Unknown primary; 未知清理诊断")
})
it("导出已发布、恢复和未发布备份事实在英文保持区别, 清理后缀可双向切语言", () => {
    locale.value = "en-us"
    expect(hostText("文件已成功导出, 但原目标备份清理失败; 请检查目标目录")).toBe(
        "The file was exported successfully, but cleanup of the original destination backup failed. Check the destination folder.",
    )
    expect(hostText("导出发布失败, 原目标文件已恢复")).toBe("Publishing the export failed. The original destination file was restored.")
    expect(hostText("导出未发布; 目标已被其他操作占用, 原文件保留为目标目录中的导出备份")).toBe(
        "The export was not published. Another operation now occupies the destination. The original file remains as an export backup in the destination folder.",
    )
    const original = "模型已变化, 请刷新后重新导出; 导出临时文件清理失败, 请检查目标目录"
    const english = hostText(original)
    expect(english).toBe("The model changed. Refresh and export again.; Export temporary file cleanup failed. Check the destination folder.")
    expect(hostText("Unknown primary; 导出临时文件清理失败, 请检查目标目录")).toBe(
        "Unknown primary; Export temporary file cleanup failed. Check the destination folder.",
    )
    locale.value = "zh-cn"
    expect(hostText(english)).toBe(original)
})
