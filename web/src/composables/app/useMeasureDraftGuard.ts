/**
 * FilePath    : pbi-utils\web\src\composables\app\useMeasureDraftGuard.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 在度量值选择变更前保护未保存草稿, 保存成功后才继续原用户意图.
 */
import { computed, nextTick, onScopeDispose, shallowRef, ref, watch } from "vue"
import type { ProjectSnapshot } from "@/bridge/client"
import { sameModelTarget } from "@/features/model/operationFreshness"

export type MeasureNavigation = { kind: "selection"; keys: string[] } | { kind: "create" } | { kind: "edit" }

/** 接收实时草稿/目标与原子保存入口, 返回受控确认状态; 失败、取消或目标变化不继续旧选择. */
export function useMeasureDraftGuard(options: {
    project: () => ProjectSnapshot
    dirty: () => boolean
    busy: () => boolean
    invalidated?: () => boolean
    save: () => Promise<boolean>
    apply: (navigation: MeasureNavigation) => void
}) {
    const pending = shallowRef<MeasureNavigation>()
    const saving = ref(false)
    const confirming = computed(() => !!pending.value)
    let disposed = false
    watch(options.project, (next, previous) => {
        if (!sameModelTarget(next, previous)) pending.value = undefined
    })
    if (options.invalidated)
        watch(
            options.invalidated,
            (invalid) => {
                if (invalid) pending.value = undefined
            },
            { flush: "sync" },
        )
    onScopeDispose(() => {
        disposed = true
        pending.value = undefined
    })

    /** 记录唯一导航意图; 在途或已有确认时拒绝重复输入, 无草稿时直接切换. */
    function request(navigation: MeasureNavigation): void {
        if (disposed || options.busy() || pending.value) return
        if (options.dirty()) pending.value = navigation
        else options.apply(navigation)
    }

    /** 按保存/放弃/取消处理当前确认; 保存失败保留弹窗和草稿, 不因等待结束推断成功. */
    async function resolve(action: "save" | "discard" | "cancel"): Promise<void> {
        const navigation = pending.value
        if (!navigation || saving.value || options.busy()) return
        if (action === "cancel") {
            pending.value = undefined
            return
        }
        if (action === "save") {
            saving.value = true
            let saved = false
            try {
                saved = await options.save()
                // 等待权威快照沿父级回填, 避免新选择绑定保存前的定义.
                await nextTick()
            } finally {
                saving.value = false
            }
            if (!saved || disposed || pending.value !== navigation) return
        }
        pending.value = undefined
        if (!disposed) options.apply(navigation)
    }
    return { confirming, saving, request, resolve }
}
