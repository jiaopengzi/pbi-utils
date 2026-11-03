/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfPackageSelection.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 精确包的整包选择、版本变更与只读详情, 复用同源依赖和一次取消边界.
 */
import type { UdfCatalogPackage, UdfPackage, UdfSelection } from "@/features/scaffold/contracts"
import { latestStableUdfVersion } from "@/features/scaffold/udfVersions"
import { validateUdfSelections } from "@/features/scaffold/udfSelectionValidation"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "./useUdfState"
import type { UdfRequest, UdfRequests } from "./useUdfRequests"

/** 包选择以完整函数集合传入现有导入契约, 失败/取消/来源漂移保留原草稿. */
export function useUdfPackageSelection(state: UdfState, requests: UdfRequests, load: () => Promise<boolean>) {
    const { t } = useI18n()
    /** 为整轮操作固定来源与目录见证; 返回检查器拒绝迟到回执及并发草稿变化. */
    function context() {
        const origin = { ...state.catalogReadOrigin.value },
            model = state.model.value
        return {
            origin,
            current: () =>
                !state.lifecycle.disposed &&
                state.model.value === model &&
                state.source.value === origin.source &&
                state.catalogReadOrigin.value.sourceBaseline === origin.sourceBaseline &&
                state.catalogStatus.value?.baseline === origin.catalogBaseline,
        }
    }
    /** 校验全部显式根与依赖, 不改变原数组; 返回宿主读取的精确包事实. */
    async function validate(request: UdfRequest, next: UdfSelection[]) {
        return await validateUdfSelections(
            request,
            next,
            state.statuses.value,
            () => new Error(t("scaffold.dependencyPreviewInvalid")),
            requests.validationOptions,
        )
    }
    /** 精确包身份和完整函数集合必须匹配当前卡片; 空包或漂移均拒绝. */
    function checkPackage(pkg: UdfPackage | undefined, item: Pick<UdfCatalogPackage, "id" | "version" | "hash">): UdfPackage {
        if (!pkg || pkg.id !== item.id || pkg.version !== item.version || pkg.hash !== item.hash || !pkg.functions.length)
            throw new Error(t("scaffold.functionUnavailable"))
        return pkg
    }
    /** 全局选择只有整批核验成功且所属状态未变化才原子更新. */
    async function commitSelection(next: UdfSelection[]): Promise<boolean> {
        const ctx = context(),
            resolved = await requests.run((request) => validate(request, next))
        if (!resolved || !ctx.current()) return false
        state.resolved.value = { ...state.resolved.value, ...resolved }
        state.model.value = next
        return true
    }
    /** 同库换版切换到新版本的全部函数, 失败时保持原 tag、hash 和选择. */
    async function changeTag(id: string, tag: string): Promise<void> {
        if (state.busy.value || (state.disabled() && !state.tagPickerOpen.value) || state.uncertain.value) return
        const ctx = context(),
            previousTags = { ...state.packageTags.value }
        const result = await requests.run(async (request) => {
            const tags = state.tagOptions.value[id] ?? (await request("udf.catalog", { action: "versions", ...ctx.origin, packageId: id })).versions ?? []
            const exact = tag === "latest" ? latestStableUdfVersion(tags) : tag
            if (!exact || !tags.includes(exact)) throw new Error(t("scaffold.functionUnavailable"))
            const pkg = (await request("udf.catalog", { action: "package", ...ctx.origin, packageId: id, version: exact })).package
            if (!pkg || pkg.id !== id || pkg.version !== exact || !pkg.functions.length) throw new Error(t("scaffold.functionUnavailable"))
            const selection: UdfSelection = {
                ...ctx.origin,
                packageId: id,
                version: exact,
                baseline: pkg.hash,
                functions: pkg.functions.map((fn) => fn.name),
            }
            const exists = state.model.value.some((item) => item.source === ctx.origin.source && item.packageId === id)
            const next = state.model.value.map((item) => (item.source === ctx.origin.source && item.packageId === id ? selection : item))
            const resolved = await validate(request, exists ? next : [...next, selection])
            return { next, resolved, exact }
        })
        if (!result || !ctx.current()) return
        state.model.value = result.next
        state.resolved.value = { ...state.resolved.value, ...result.resolved }
        state.packageTags.value =
            tag === "latest" ? Object.fromEntries(Object.entries(previousTags).filter(([key]) => key !== id)) : { ...previousTags, [id]: result.exact }
        // 精确选择预发行 tag 同时要求可见, 声明依赖按固定版本自动解析.
        if (result.exact.split("+")[0]!.includes("-")) state.prerelease.value = true
        state.tagPickerOpen.value = false
        state.page.value = 1
        await load()
    }
    /** 卡片选中与本页全选均读取整包, 统一版本、依赖及预算核验后一次合并. */
    async function selectPackages(items: UdfCatalogPackage[]): Promise<void> {
        const ctx = context()
        const result = await requests.run(async (request) => {
            const next = state.model.value.map((item) => ({ ...item, functions: [...item.functions] }))
            for (const item of items) {
                if (item.version.split("+")[0]!.includes("-") && state.packageTags.value[item.id] !== item.version)
                    throw new Error(t("scaffold.choosePrereleaseTag"))
                const index = next.findIndex((entry) => entry.source === ctx.origin.source && entry.packageId === item.id),
                    existing = next[index]
                if (
                    existing &&
                    (existing.version !== item.version || existing.baseline !== item.hash || existing.catalogBaseline !== ctx.origin.catalogBaseline)
                )
                    throw new Error(t("scaffold.staleSelection"))
                const pkg = checkPackage(
                    // oxlint-disable-next-line no-await-in-loop -- 整批包选择共用一个取消身份, 首错停止且不发布部分草稿.
                    (await request("udf.catalog", { action: "package", ...ctx.origin, packageId: item.id, version: item.version })).package,
                    item,
                )
                const selection: UdfSelection = {
                    ...ctx.origin,
                    packageId: item.id,
                    version: item.version,
                    baseline: item.hash,
                    functions: pkg.functions.map((fn) => fn.name),
                }
                if (index >= 0) next[index] = selection
                else next.push(selection)
            }
            return { next, resolved: await validate(request, next) }
        })
        if (!result || !ctx.current()) return
        state.resolved.value = { ...state.resolved.value, ...result.resolved }
        state.model.value = result.next
    }
    /** 勾选整包, 取消勾选立即移除该包; 过期选择仍可移除, 禁用时拒绝事件. */
    async function selectPackage(item: UdfCatalogPackage, checked: boolean): Promise<void> {
        if (state.locked.value || state.uncertain.value) return
        if (!checked) {
            state.model.value = state.model.value.filter((entry) => entry.source !== state.source.value || entry.packageId !== item.id)
            return
        }
        await selectPackages([item])
    }
    /** 当前显示页以完整包加入, 搜索/分页隐藏的其他来源和包选择保持不变. */
    async function selectPage(): Promise<void> {
        if (state.locked.value || state.uncertain.value || !state.packageItems.value.length) return
        await selectPackages(state.packageItems.value)
    }
    /** 只读完整包详情, 复核精确身份与请求开始的来源/目录, 不改变已选草稿. */
    async function openPackage(item: UdfCatalogPackage): Promise<void> {
        if (state.locked.value || state.uncertain.value) return
        const ctx = context(),
            result = await requests.run(async (request) =>
                checkPackage((await request("udf.catalog", { action: "package", ...ctx.origin, packageId: item.id, version: item.version })).package, item),
            )
        if (!result || !ctx.current()) return
        state.detail.value = result
        state.detailOpen.value = true
    }
    return { commitSelection, changeTag, selectPackage, selectPage, openPackage }
}
