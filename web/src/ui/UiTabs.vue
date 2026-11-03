<!--
 * FilePath    : pbi-utils\web\src\ui\UiTabs.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用 Element Plus 默认尺寸的二级页签, 保留键盘导航、窄容器换行及在途切换保护.
-->
<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue"
import type { TabsInstance } from "element-plus"

const props = defineProps<{ modelValue: string; items: string[][]; disabled?: boolean }>()
const emit = defineEmits<{ "update:modelValue": [value: string] }>()
const tabs = ref<TabsInstance>()
let tabElement: HTMLElement | undefined

/** 整组锁定时阻止页签导航键进入无可用项的组件逻辑, 保留 Tab 离开焦点及其他按键. */
function guardLockedNavigation(event: KeyboardEvent): void {
    if (!props.disabled || !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Enter", "NumpadEnter", "Space", " "].includes(event.code || event.key)) return
    event.preventDefault()
    event.stopPropagation()
}

// 在实际根元素的捕获阶段拦截, 保留组件根节点与属性透传, 不依赖第三方未声明的组件事件.
onMounted(() => {
    tabElement = tabs.value?.$el as HTMLElement | undefined
    tabElement?.addEventListener("keydown", guardLockedNavigation, true)
})
onBeforeUnmount(() => {
    tabElement?.removeEventListener("keydown", guardLockedNavigation, true)
    tabElement = undefined
})
</script>
<template>
    <el-tabs ref="tabs" class="tabs" :model-value="modelValue" @tab-change="emit('update:modelValue', String($event))">
        <el-tab-pane v-for="item in items" :key="item[0]" :name="item[0]" :label="item[1]" :disabled="disabled" />
    </el-tabs>
</template>
