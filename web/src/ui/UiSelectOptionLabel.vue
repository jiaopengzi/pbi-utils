<!--
 * FilePath    : pbi-utils\web\src\ui\UiSelectOptionLabel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按 Element Plus 当前筛选词高亮下拉选项的可见标签.
-->
<script setup lang="ts">
import { selectKey } from "element-plus"
import { inject } from "vue"
import UiSearchHighlight from "./UiSearchHighlight.vue"

/** text 为选项原始标签; 读取所属下拉框的筛选输入, 缺少上下文或筛选词时显示原文. */
defineProps<{ text: string }>()
const select = inject(selectKey, null)
</script>

<template>
    <span><UiSearchHighlight :text="text" :query="select?.states.inputValue ?? ''" exact /></span>
</template>
