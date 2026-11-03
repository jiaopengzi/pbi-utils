/**
 * FilePath    : pbi-utils\web\src\features\model\measureFieldOptions.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 度量值静态格式预设与现有显示文件夹建议, 不改写模型定义或用户自定义文本.
 */

/**
 * 度量值静态格式预设参考 DAX FORMAT 官方文档; 常规预设使用空串, nn 表示带前导零的分钟 (00-59), 标签在渲染时翻译.
 * 命名预定义格式保留官方标识, 显示遵循模型区域性; Medium Date 与 Long Date 同义, 界面语言只切换标签.
 * 中文: https://learn.microsoft.com/zh-cn/dax/format-function-dax
 * English: https://learn.microsoft.com/en-us/dax/format-function-dax
 */
export const measureFormatPresets: readonly { value: string; labelKey: string }[] = [
    { value: "", labelKey: "measureField.general" },
    { value: "0", labelKey: "measureField.integer" },
    { value: "#,##0", labelKey: "measureField.groupedInteger" },
    { value: "0.00", labelKey: "measureField.decimal" },
    { value: "#,##0.00", labelKey: "measureField.groupedDecimal" },
    { value: "0%", labelKey: "measureField.percent" },
    { value: "0.00%", labelKey: "measureField.decimalPercent" },
    { value: "¥#,##0.00", labelKey: "measureField.yuan" },
    { value: "$#,##0.00", labelKey: "measureField.dollar" },
    { value: "€#,##0.00", labelKey: "measureField.euro" },
    { value: "0.00E+00", labelKey: "measureField.scientific" },
    { value: "General Number", labelKey: "measureField.predefinedGeneralNumber" },
    { value: "Currency", labelKey: "measureField.predefinedCurrency" },
    { value: "Fixed", labelKey: "measureField.predefinedFixed" },
    { value: "Standard", labelKey: "measureField.predefinedStandard" },
    { value: "Percent", labelKey: "measureField.predefinedPercent" },
    { value: "Scientific", labelKey: "measureField.predefinedScientific" },
    { value: "General Date", labelKey: "measureField.predefinedGeneralDate" },
    { value: "Long Date", labelKey: "measureField.predefinedLongDate" },
    { value: "Medium Date", labelKey: "measureField.predefinedMediumDate" },
    { value: "Short Date", labelKey: "measureField.predefinedShortDate" },
    { value: "Long Time", labelKey: "measureField.predefinedLongTime" },
    { value: "Medium Time", labelKey: "measureField.predefinedMediumTime" },
    { value: "Short Time", labelKey: "measureField.predefinedShortTime" },
    { value: "yyyy-mm-dd", labelKey: "measureField.date" },
    { value: "yyyy/mm/dd", labelKey: "measureField.slashDate" },
    { value: "mm/dd/yyyy", labelKey: "measureField.monthFirstDate" },
    { value: "dd/mm/yyyy", labelKey: "measureField.dayFirstDate" },
    { value: "dddd, mmmm d, yyyy", labelKey: "measureField.longDate" },
    { value: "yyyy-mm-dd hh:nn:ss", labelKey: "measureField.dateTime" },
    { value: "hh:nn:ss", labelKey: "measureField.time" },
    { value: "hh:nn", labelKey: "measureField.shortTime" },
    { value: "h:nn:ss AM/PM", labelKey: "measureField.twelveHourTime" },
    { value: "True/False", labelKey: "measureField.trueFalse" },
    { value: "Yes/No", labelKey: "measureField.yesNo" },
    { value: "On/Off", labelKey: "measureField.onOff" },
]

const folderOrder = new Intl.Collator("zh-CN", { numeric: true, sensitivity: "base" })

/** 从模型原始属性拆分多显示位置并补齐祖先, 忽略空层级, 不区分大小写去重且保留首次原文; 不修改输入. */
export function displayFolderOptions(values: readonly string[]): string[] {
    const unique = new Map<string, string>()
    for (const value of values) {
        for (const location of value.split(";")) {
            const parts = location.split("\\").filter((part) => part.trim().length > 0)
            for (let depth = 1; depth <= parts.length; depth++) {
                const path = parts.slice(0, depth).join("\\")
                const key = path.toLowerCase()
                if (!unique.has(key)) unique.set(key, path)
            }
        }
    }
    return [...unique.values()].toSorted(folderOrder.compare)
}
