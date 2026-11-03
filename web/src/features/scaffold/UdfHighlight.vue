<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfHighlight.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 用纯文本片段标记搜索命中, 不解析 HTML 或正则表达式.
-->
<script setup lang="ts">
import { computed } from "vue"
/** 输入原文与普通搜索文本; 空搜索原样显示, 不执行任何用户内容. */
const props = defineProps<{ text: string; query?: string; matches?: number[][] }>()
const parts = computed(() => {
    const query = (props.query ?? "").trim().toLocaleLowerCase(),
        result: { text: string; match: boolean }[] = []
    if (props.matches) {
        let cursor = 0
        for (const range of props.matches) {
            const [start, length] = range
            if (!Number.isInteger(start) || !Number.isInteger(length) || start! < cursor || length! <= 0 || start! + length! > props.text.length)
                return [{ text: props.text, match: false }]
            if (start! > cursor) result.push({ text: props.text.slice(cursor, start), match: false })
            result.push({ text: props.text.slice(start, start! + length!), match: true })
            cursor = start! + length!
        }
        if (cursor < props.text.length) result.push({ text: props.text.slice(cursor), match: false })
        return result
    }
    if (!query) return [{ text: props.text, match: false }]
    const lower = props.text.toLocaleLowerCase()
    let offset = 0,
        index = lower.indexOf(query)
    while (index >= 0) {
        if (index > offset) result.push({ text: props.text.slice(offset, index), match: false })
        result.push({ text: props.text.slice(index, index + query.length), match: true })
        offset = index + query.length
        index = lower.indexOf(query, offset)
    }
    if (offset < props.text.length) result.push({ text: props.text.slice(offset), match: false })
    return result
})
</script>
<template>
    <template v-for="(part, index) in parts" :key="index"
        ><mark v-if="part.match">{{ part.text }}</mark
        ><template v-else>{{ part.text }}</template></template
    >
</template>
