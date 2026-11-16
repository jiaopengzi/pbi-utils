<!--
 * FilePath    : pbi-utils\web\src\ui\UiIcon.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 从本地资源渲染 SVG 图标, 单色操作图标可继承按钮文字色.
-->
<script setup lang="ts">
import { computed } from "vue"

/** 按 name 读取随包分发的装饰图标; monochrome 让单色图标跟随文字色, 彩色图标使用原始资源. */
const props = defineProps<{ name: string; monochrome?: boolean }>()
const source = computed(() => `${import.meta.env.BASE_URL}icons/${props.name}.svg`)
</script>
<template>
    <span v-if="monochrome" class="ui-icon ui-icon--monochrome" :style="{ '--ui-icon-mask': `url('${source}')` }" aria-hidden="true"></span>
    <img v-else class="ui-icon" :src="source" alt="" />
</template>
<style scoped lang="scss">
.ui-icon--monochrome {
    display: inline-block;
    background-color: currentColor;
    mask: var(--ui-icon-mask) center / contain no-repeat;
}
</style>
