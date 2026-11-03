<!--
 * FilePath    : pbi-utils\web\src\ui\UiDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 统一模态居中、焦点恢复与滚动范围, 保存中锁定关闭入口.
-->
<script setup lang="ts">
import { nextTick, onBeforeUnmount, useId, watch } from "vue"
/** 标题与宽度由调用方声明; scrollResetKey 为可选精确内容身份, 仅启用方在打开或身份变化时回到正文起点. */
const props = defineProps<{ modelValue: boolean; title: string; busy?: boolean; width?: string; scrollResetKey?: string }>()
const emit = defineEmits<{ "update:modelValue": [value: boolean]; opened: [] }>()
const scrollBodyClass = `ui-dialog-scroll-${useId()}`
let scrollRevision = 0,
    resetPending = false
/** 等待真实正文挂载后重置双轴; 关闭、卸载或后续身份使旧回调失效, 不操作页面和其他弹窗的滚动. */
async function resetBodyScroll(revision: number, key: string | undefined): Promise<void> {
    await nextTick()
    if (!resetPending || revision !== scrollRevision || !props.modelValue || key === undefined || key !== props.scrollResetKey) return
    const body = document.getElementsByClassName(scrollBodyClass).item(0)
    if (!(body instanceof HTMLElement)) return
    resetPending = false
    body.scrollTop = 0
    body.scrollLeft = 0
}
watch(
    () => [props.modelValue, props.scrollResetKey] as const,
    ([open, key]) => {
        const revision = ++scrollRevision
        resetPending = open && key !== undefined
        if (resetPending) void resetBodyScroll(revision, key)
    },
    { immediate: true, flush: "post" },
)
/** opened 只补偿首次渲染尚未出现的正文; 已归零后不因动画终态再次打断用户阅读, 原事件仍同步转发. */
function opened(): void {
    if (resetPending) void resetBodyScroll(scrollRevision, props.scrollResetKey)
    emit("opened")
}
onBeforeUnmount(() => {
    scrollRevision++
    resetPending = false
})
</script>
<template>
    <el-dialog
        :model-value="modelValue"
        :title="title"
        :width="width ?? 'min(640px, calc(100vw - 32px))'"
        :body-class="scrollResetKey === undefined ? undefined : scrollBodyClass"
        class="feature-dialog"
        append-to-body
        align-center
        :show-close="!busy"
        :close-on-click-modal="false"
        :close-on-press-escape="!busy"
        @update:model-value="emit('update:modelValue', $event)"
        @opened="opened"
    >
        <slot />
        <template v-if="$slots.footer" #footer><slot name="footer" /></template>
    </el-dialog>
</template>
