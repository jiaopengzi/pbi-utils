<!--
 * FilePath    : pbi-utils\web\src\features\markdown\SafeMarkdown.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 安全展示 Markdown 文档与流式片段, 统一局部锚点、本地代码着色和禁止外部导航的边界.
-->
<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue"
import { paintCode } from "@/features/code/highlight"
import { renderMarkdown } from "./markdown"
/** 显示原文及可选的局部锚点前缀, HTML 输出仅来自净化入口; 不发起请求、不执行代码或授予外链导航. */
const props = withDefaults(defineProps<{ source: string; headingPrefix?: string }>(), { headingPrefix: "markdown-heading-" })
const documentElement = ref<HTMLElement>()
const content = computed(() => renderMarkdown(props.source, props.headingPrefix))
/** 净化后的代码围栏复用本地语法树着色, 未知语言和超长内容按共享边界保持完整原文.
 * 仅改写当前文档中的代码节点, 不执行代码或访问原始 HTML/外部媒体.
 */
function highlightFences(): void {
    for (const code of documentElement.value?.querySelectorAll<HTMLElement>("pre > code") ?? []) {
        const language = /\blanguage-(\S+)/.exec(code.className)?.[1] ?? "plaintext"
        paintCode(code, code.textContent ?? "", language)
    }
}
onMounted(highlightFences)
watch(content, highlightFences, { flush: "post" })
/** 阻止点击和辅助点击触发宿主导航, 不扩大宿主的外部地址白名单.
 * @param event 当前文档内的鼠标或键盘合成点击; 仅主点击的有效标题锚点可滚动当前文档.
 * @returns 无返回值; 畸形编码、外部链接和未知锚点保持原位置.
 */
function preventNavigation(event: MouseEvent): void {
    if (!(event.target instanceof Element)) return
    const anchor = event.target.closest("a")
    if (!anchor) return
    event.preventDefault()
    const href = anchor.getAttribute("href")
    if (event.type !== "click" || event.button !== 0 || !href?.startsWith("#")) return
    let fragment: string
    try {
        fragment = decodeURIComponent(href.slice(1))
    } catch {
        return
    }
    const heading = [...(documentElement.value?.querySelectorAll<HTMLElement>("h1[id],h2[id],h3[id],h4[id],h5[id],h6[id]") ?? [])].find(
        (item) => item.id === `${props.headingPrefix}${fragment}`,
    )
    heading?.scrollIntoView({ block: "start" })
}
</script>
<template>
    <div ref="documentElement" class="safe-markdown" @click="preventNavigation" @auxclick="preventNavigation" v-html="content" />
</template>
<style scoped lang="scss">
.safe-markdown {
    font-size: var(--markdown-font-size, 14px);
    line-height: var(--markdown-line-height, 1.75);
    overflow-wrap: anywhere;
    min-width: 0;
    color: var(--el-text-color-primary);
    :deep(> :first-child) {
        margin-top: 0;
    }
    :deep(> :last-child) {
        margin-bottom: 0;
    }
    :deep(h1),
    :deep(h2),
    :deep(h3),
    :deep(h4),
    :deep(h5),
    :deep(h6) {
        line-height: 1.4;
        margin: var(--markdown-heading-margin, 24px 0 12px);
        font-weight: 600;
        scroll-margin-top: 16px;
    }
    :deep(h1) {
        font-size: var(--markdown-h1-size, 28px);
        letter-spacing: -0.02em;
    }
    :deep(h2) {
        font-size: var(--markdown-h2-size, 22px);
        margin-top: var(--markdown-h2-margin-top, 32px);
    }
    :deep(h3) {
        font-size: var(--markdown-h3-size, 18px);
    }
    :deep(h4),
    :deep(h5),
    :deep(h6) {
        font-size: 15px;
    }
    :deep(p),
    :deep(ul),
    :deep(ol),
    :deep(blockquote),
    :deep(pre),
    :deep(table) {
        margin: var(--markdown-block-margin, 16px 0);
    }
    :deep(ul),
    :deep(ol) {
        padding-left: 24px;
    }
    :deep(li + li) {
        margin-top: 6px;
    }
    :deep(li > ul),
    :deep(li > ol) {
        margin-block: 6px;
    }
    :deep(blockquote) {
        margin-inline: 0;
        padding: 4px 16px;
        border-left: 3px solid var(--el-color-primary);
        background: var(--el-fill-color-light);
        color: var(--el-text-color-regular);
    }
    :deep(code) {
        font-family: var(--font-code, Consolas, monospace);
        font-size: 0.92em;
    }
    :deep(:not(pre) > code) {
        padding: 2px 5px;
        border-radius: 4px;
        background: var(--el-fill-color);
    }
    :deep(pre) {
        padding: var(--markdown-code-padding, 16px);
        overflow: auto;
        max-width: 100%;
        border: 1px solid var(--el-border-color-light);
        border-radius: var(--el-border-radius-base);
        background: var(--el-fill-color-light);
        white-space: pre;
        overflow-wrap: normal;
    }
    :deep(pre > code) {
        display: block;
        line-height: 1.7;
    }
    :deep(table) {
        display: block;
        overflow-x: auto;
        border-collapse: collapse;
        max-width: 100%;
    }
    :deep(th),
    :deep(td) {
        padding: 8px 12px;
        border: 1px solid var(--el-border-color);
        text-align: left;
    }
    :deep(th) {
        background: var(--el-fill-color-light);
        font-weight: 600;
    }
    :deep(tr:nth-child(even)) {
        background: var(--el-fill-color-lighter);
    }
    :deep(a) {
        color: var(--el-color-primary);
        text-decoration: underline;
        text-underline-offset: 3px;
    }
    :deep(a:focus-visible) {
        outline: 2px solid var(--el-color-primary);
        outline-offset: 3px;
    }
    :deep(hr) {
        margin-block: 20px;
        border: 0;
        border-top: 1px solid var(--el-border-color);
    }
}
</style>
