<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\ScaffoldField.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 复用原生字段容器显示双语错误, 将错误标记关联到真实输入并在清除时恢复属性.
-->
<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, onUpdated, ref, shallowRef } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import { scaffoldValidationKey, scaffoldValidationPrefixKey } from "./validationContext"
const props = defineProps<{ field: string; row?: number; aliases?: string[]; tag?: "label" | "div" | "td"; errorFirst?: boolean }>()
const root = ref<HTMLElement>()
const issue = inject(scaffoldValidationKey, shallowRef(undefined))
const prefix = inject(scaffoldValidationPrefixKey, "scaffold")
const { t } = useI18n()
const active = computed(() => {
    const value = issue.value
    return value && (value.field === props.field || props.aliases?.includes(value.field)) && (props.row === undefined || props.row === value.row)
        ? value
        : undefined
})
const errorId = computed(() => `${prefix}-error-${props.field}${props.row === undefined ? "" : `-${props.row}`}`)
const message = computed(() => (active.value ? t(active.value.key, active.value.params) : ""))
let marked: { control: HTMLElement; invalid: string | null; describedBy: string | null; id: string }[] = []

/** 移除本字段添加的错误关联, 保留控件原有无障碍属性和其他描述. */
function clear(): void {
    for (const { control, invalid, describedBy, id } of marked) {
        if (control.getAttribute("aria-invalid") === "true") {
            if (invalid === null) control.removeAttribute("aria-invalid")
            else control.setAttribute("aria-invalid", invalid)
        }
        const descriptions = (control.getAttribute("aria-describedby") ?? "").split(/\s+/).filter((value) => value && value !== id)
        if (descriptions.length) control.setAttribute("aria-describedby", descriptions.join(" "))
        else if (describedBy === null) control.removeAttribute("aria-describedby")
        else control.setAttribute("aria-describedby", describedBy)
    }
    marked = []
}
/** 只标记真正的输入或集合操作按钮, 不把第三方组合根误当成可编辑字段. */
function synchronize(): void {
    clear()
    if (!active.value || !root.value) return
    const inputs = [...root.value.querySelectorAll<HTMLElement>('input:not([type="hidden"]),textarea')]
    const controls = inputs.length ? inputs : [...root.value.querySelectorAll<HTMLElement>("button,[tabindex]")]
    for (const control of controls) {
        const invalid = control.getAttribute("aria-invalid"),
            describedBy = control.getAttribute("aria-describedby"),
            id = errorId.value
        marked.push({ control, invalid, describedBy, id })
        control.setAttribute("aria-invalid", "true")
        control.setAttribute("aria-describedby", [...new Set([...(describedBy ?? "").split(/\s+/).filter(Boolean), id])].join(" "))
    }
}
onMounted(synchronize)
onUpdated(synchronize)
onBeforeUnmount(clear)
</script>
<template>
    <component
        :is="tag ?? 'label'"
        ref="root"
        class="scaffold-field"
        :data-scaffold-field="active?.field ?? field"
        :data-scaffold-row="row"
        :data-scaffold-invalid="active ? true : undefined"
    >
        <span v-if="active && errorFirst" :id="errorId" class="scaffold-field__error">{{ message }}</span>
        <slot />
        <span v-if="active && !errorFirst" :id="errorId" class="scaffold-field__error">{{ message }}</span>
    </component>
</template>
