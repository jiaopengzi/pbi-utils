<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfMarkdown.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 以完整文档排版展示 UDF 说明, 保留既有局部锚点与详情阅读位置.
-->
<script setup lang="ts">
import SafeMarkdown from "@/features/markdown/SafeMarkdown.vue"
/** 接收 UDF 文档原文, 共用安全 Markdown 内核; 保持详情容器拥有阅读位置和内容身份重置策略. */
defineProps<{ source: string }>()
</script>
<template>
    <SafeMarkdown :source="source" heading-prefix="udf-heading-" class="udf-markdown" />
</template>
