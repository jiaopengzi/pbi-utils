<!--
 * FilePath    : pbi-utils\web\src\ui\UiSearchHighlight.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 将查询的字面命中渲染为安全文本标记, 供模型名称与目录复用.
-->
<script setup lang="ts">
import { computed } from "vue"

/** text 为原始名称; query 默认按空白分词, exact 保留完整筛选词与空格, 两种模式都忽略大小写. */
const props = defineProps<{ text: string; query: string; exact?: boolean }>()
const parts = computed(() => {
    const words = props.exact
        ? [props.query].filter(Boolean)
        : [...new Set(props.query.trim().split(/\s+/u).filter(Boolean))].toSorted((a, b) => b.length - a.length)
    if (!words.length) return [{ text: props.text, match: false }]
    // 完整筛选词与 Element Plus 的默认选项筛选使用同一大小写匹配规则.
    const pattern = new RegExp(words.map((word) => word.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&")).join("|"), props.exact ? "gi" : "giu")
    const result: { text: string; match: boolean }[] = []
    let cursor = 0
    for (const match of props.text.matchAll(pattern)) {
        if (match.index > cursor) result.push({ text: props.text.slice(cursor, match.index), match: false })
        result.push({ text: match[0], match: true })
        cursor = match.index + match[0].length
    }
    if (cursor < props.text.length) result.push({ text: props.text.slice(cursor), match: false })
    return result
})
</script>
<template>
    <template v-for="(part, index) in parts" :key="index"><mark v-if="part.match">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template>
</template>
<style scoped lang="scss">
mark {
    color: inherit;
    background: var(--el-color-warning-light-7);
    border-radius: 2px;
}
</style>
