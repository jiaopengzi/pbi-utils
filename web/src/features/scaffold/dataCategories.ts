/**
 * FilePath    : pbi-utils\web\src\features\scaffold\dataCategories.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享文本模板度量值标准分类目录, 与后端契约校验保持一致.
 */

// 模板值生成文本表达式, 分类顺序与官方 Desktop 的 Text 分类集保持一致.
export const templateMeasureCategories = Object.freeze([
    "Uncategorized",
    "Address",
    "Place",
    "City",
    "County",
    "StateOrProvince",
    "PostalCode",
    "Country",
    "Continent",
    "Latitude",
    "Longitude",
    "WebUrl",
    "ImageUrl",
    "Barcode",
] as const)

/** 判断固定大小写的业务分类值, 不静默解释未知值或别名. */
export function isTemplateMeasureCategory(value: string): boolean {
    return templateMeasureCategories.some((category) => category === value)
}
