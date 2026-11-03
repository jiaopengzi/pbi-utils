<!--
 * FilePath    : pbi-utils\web\src\features\code\CodeBlock.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 展示与编辑器同源的 DAX/M/TMDL 高亮和原文复制, 超长内容自动降级.
-->
<!-- 代码节点只由 paintCode 管理; 不绑定原始 HTML. -->
<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
import { computed, onMounted, ref, watch } from "vue"
import { normalizeLanguage, paintCode } from "./highlight"
import { useFeedback } from "@/composables/ui/useFeedback"
const { t } = useI18n()
const { showFeedback } = useFeedback()
/** code 为原始文本, language 选择本地着色, copyLabel 可提供业务复制名称; 复制失败经共享反馈提示. */
const props = defineProps<{ code: string; language?: string; copyLabel?: string }>()
const element = ref<HTMLElement>()
const language = computed(() => normalizeLanguage(props.language))
/** 从原文重绘, 始终保留原始文本边界. */
function paint() {
    if (element.value) paintCode(element.value, props.code, language.value)
}
/** 只复制原始字符串; 剪贴板不可用时显示明确错误. */
async function copy() {
    try {
        await navigator.clipboard.writeText(props.code)
        showFeedback({ message: t("model.copied") })
    } catch {
        showFeedback({ message: t("model.copyFailed"), type: "error" })
    }
}
onMounted(paint)
watch([() => props.code, language], paint, { flush: "post" })
</script>
<template>
    <section class="code-block">
        <div class="code-toolbar">
            <el-button @click="copy">{{ copyLabel || t("model.copyCode", { language: language.toUpperCase() }) }}</el-button>
        </div>
        <pre :data-lang="language"><code ref="element"></code></pre>
    </section>
</template>
<style scoped lang="scss">
.code-block {
    min-width: 0;
    margin-block: 16px;
}
.code-toolbar {
    display: flex;
    justify-content: flex-end;
}
pre {
    max-width: 100%;
    margin: 12px 0 0;
    background: #f6f7fb;
    border: 1px solid var(--border);
    border-radius: 6px;
}
code {
    display: block;
    padding: 16px;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 12px;
    line-height: 1.7;
    color: #25305b;
}
</style>
