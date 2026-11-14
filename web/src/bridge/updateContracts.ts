/**
 * FilePath    : pbi-utils\web\src\bridge\updateContracts.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 声明固定发布源的更新设置、检查结果和发布列表.
 */
export type ProductUpdateSource = "github" | "gitee"
export interface ProductUpdateSettings {
    automaticCheck: boolean
    source: ProductUpdateSource
}
/** 按宿主顺序展示的发布记录; version 与 tag 均保留精确发布标签, 说明始终作为纯文本. */
export interface ProductUpdateRelease {
    tag: string
    version: string
    prerelease: boolean
    notes: string
    notesTruncated: boolean
}
export interface ProductUpdateCheck {
    source: ProductUpdateSource
    /** 当前应用的完整发布标签. */
    currentVersion: string
    status: "available" | "current" | "noRelease" | "failed"
    checkedAtUtc: string
    /** 所选来源最新发布的完整标签, 无发布时为 null. */
    latestVersion: string | null
    releases: ProductUpdateRelease[]
    errorCode: string | null
    isSimulation: boolean
}
export interface ProductUpdateState {
    settings: ProductUpdateSettings
    lastCheck: ProductUpdateCheck | null
}
export interface ProductUpdateCheckResponse {
    checkedNow: boolean
    check: ProductUpdateCheck | null
}
