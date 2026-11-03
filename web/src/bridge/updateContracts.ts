/**
 * FilePath    : pbi-utils\web\src\bridge\updateContracts.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 声明固定发布源的更新设置、检查结果和受限打开目标.
 */
export type ProductUpdateSource = "github" | "gitee"
export type ProductUpdateTarget = "release" | "without-dotnet"
export interface ProductUpdateSettings {
    automaticCheck: boolean
    source: ProductUpdateSource
}
export interface ProductUpdateAsset {
    dotNetMode: Exclude<ProductUpdateTarget, "release">
    fileName: string
    downloadUrl: string
    size: number | null
}
export interface ProductUpdateCheck {
    source: ProductUpdateSource
    currentVersion: string
    status: "available" | "current" | "noRelease" | "failed"
    checkedAtUtc: string
    latestVersion: string | null
    releaseUrl: string | null
    notes: string
    assets: ProductUpdateAsset[]
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
