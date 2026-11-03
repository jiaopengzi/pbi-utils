/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useScaffoldValidationFocus.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 新建字段错误的注入、跨步骤路由和延迟DOM聚焦.
 */
import { nextTick, watch, provide, useId, onBeforeUnmount } from "vue"
import { scaffoldValidationKey, scaffoldValidationPrefixKey } from "@/features/scaffold/validationContext"
import type { ScaffoldValidationIssue } from "@/features/scaffold/validation"
import type { ScaffoldState } from "@/composables/features/scaffold/useScaffoldState"
/** 仅对当前挂载的真实控件定位; 草稿变化和卸载立即废弃旧焦点任务.
 * @param state 当前步骤、校验问题及真实面板元素.
 * @returns 错误路由和清理入口; 未挂载或锁定时延迟聚焦.
 */
export function useScaffoldValidationFocus(state: ScaffoldState) {
    const {
        lifecycle,
        step,
        validationIssue,
        panelRoot,
        locked,
        projectName,
        templateId,
        output,
        measuresEnabled,
        sampleEnabled,
        calendarEnabled,
        periodsEnabled,
        udfsEnabled,
        measures,
        sample,
        calendar,
        periods,
        udfs,
        selectedTemplateQueries,
        templateQueryGroups,
    } = state
    let pendingFocus: ScaffoldValidationIssue | undefined
    provide(scaffoldValidationKey, validationIssue)
    provide(scaffoldValidationPrefixKey, `scaffold-${useId()}`)
    /** 切换到确定的输入错误, 延迟挂载与子步骤读取完成后才聚焦真实控件. */
    async function showIssue(value: ScaffoldValidationIssue): Promise<void> {
        step.value = value.step
        validationIssue.value = value
        pendingFocus = value
        await focusIssue()
    }
    /** 子组件渲染完成且未处于业务锁时定位字段, 新编辑或卸载会使旧焦点任务失效. */
    async function focusIssue(): Promise<void> {
        const expected = pendingFocus
        if (!expected || lifecycle.disposed || locked.value) return
        await nextTick()
        if (pendingFocus !== expected || validationIssue.value !== expected || lifecycle.disposed || locked.value) return
        const field = panelRoot.value?.querySelector<HTMLElement>('[data-scaffold-invalid="true"]')
        const control =
            field?.querySelector<HTMLElement>('input:not([type="hidden"]):not(:disabled),textarea:not(:disabled)') ??
            field?.querySelector<HTMLElement>('button:not(:disabled),[tabindex="0"]')
        if (!control) return
        pendingFocus = undefined
        control.scrollIntoView?.({ block: "center", inline: "nearest" })
        control.focus({ preventScroll: true })
    }
    watch(
        locked,
        (value) => {
            if (!value && pendingFocus) void focusIssue()
        },
        { flush: "post" },
    )
    // 输入变化清除旧字段错误, 全局语言切换保留问题并由字典重新渲染, 不触发新业务请求.
    watch(
        [
            projectName,
            templateId,
            output,
            measuresEnabled,
            sampleEnabled,
            calendarEnabled,
            periodsEnabled,
            udfsEnabled,
            measures,
            sample,
            calendar,
            periods,
            udfs,
            selectedTemplateQueries,
            templateQueryGroups,
        ],
        () => {
            validationIssue.value = undefined
            pendingFocus = undefined
        },
        { deep: true, flush: "sync" },
    )

    /** 清除旧字段问题及尚未执行的焦点任务, 不改变草稿或业务请求. */
    function clearIssue(): void {
        validationIssue.value = undefined
        pendingFocus = undefined
    }
    onBeforeUnmount(() => {
        pendingFocus = undefined
    })
    return { showIssue, clearIssue }
}
export type ScaffoldValidationFocus = ReturnType<typeof useScaffoldValidationFocus>
