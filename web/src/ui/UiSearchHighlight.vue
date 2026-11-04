<!--
 * FilePath    : pbi-utils\web\src\ui\UiSearchHighlight.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 将空白分词查询的字面命中渲染为安全文本标记, 供模型名称与目录复用.
-->
<script setup lang="ts">
import { computed } from "vue"

/** text 为原始名称, query 按空白分词且忽略大小写; 空查询原样显示, 特殊字符不作为正则语法或HTML执行. */
const props = defineProps<{ text: string; query: string }>()
const parts = computed(() => {
    const words = [...new Set(props.query.trim().split(/\s+/u).filter(Boolean))].toSorted((a, b) => b.length - a.length)
    if (!words.length) return [{ text: props.text, match: false }]
    const pattern = new RegExp(words.map((word) => word.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&")).join("|"), "giu")
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
    <template v-for="(part, index) in parts" :key="index"
        ><mark v-if="part.match">{{ part.text }}</mark
        ><template v-else>{{ part.text }}</template></template
    >
</template>
<style scoped lang="scss">
mark {
    color: inherit;
    background: var(--el-color-warning-light-7);
    border-radius: 2px;
}
</style>
