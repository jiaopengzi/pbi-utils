<!--
 * FilePath    : pbi-utils\web\src\app\SearchDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 与原型一致的功能目录弹窗和快捷键.
-->
<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
const { t } = useI18n()
import UiDialog from "@/ui/UiDialog.vue"
import UiIcon from "@/ui/UiIcon.vue"
import type { InputInstance } from "element-plus"
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue"
const props = defineProps<{ disabled: boolean }>()
const emit = defineEmits<{ select: [page: string, tab: string] }>()
const visible = ref(false)
const activeIndex = ref(0)
const query = ref("")
const input = ref<InputInstance>()
const resultList = ref<HTMLElement>()
const catalog = computed(() => [
    ["connections", "", t("app.connections"), t("app.connectionsDesc"), "plug"],
    ["new", "new", t("app.new"), t("app.newDesc"), "folder"],
    ["sample", "", t("app.sample"), t("app.sampleDesc"), "flask-conical"],
    ["model", "measures", t("app.measures"), t("app.measuresDesc"), "database"],
    ["model", "templates", t("scaffold.measures"), t("app.templatesDesc"), "braces"],
    ["model", "time", t("scaffold.calendar"), t("app.calendarDesc"), "calendar-days"],
    ["model", "udf", t("app.udf"), t("configuration.applyUdfs"), "braces"],
    ["report", "pages", t("app.pageEdit"), t("app.pageEditDesc"), "panels-top-left"],
    ["learning", "", t("learning.title"), t("learning.description"), "book-open"],
    ["system", "general", t("app.general"), t("app.generalDesc"), "settings"],
    ["system", "mcp", t("mcp.title"), t("mcp.searchDescription"), "plug"],
    ["system", "environment", t("diagnostic.heading"), t("diagnostic.runtimeNote"), "settings"],
    ["system", "updates", t("updates.title"), t("updates.description"), "settings"],
    ["system", "data", t("dataReset.title"), t("dataReset.searchDescription"), "settings"],
])
const results = computed(() => catalog.value.filter((c) => `${c[2]} ${c[3]}`.toLowerCase().includes(query.value.trim().toLowerCase())))
watch([query, () => catalog.value], () => {
    activeIndex.value = 0
})
/** 打开组件模态框, 聚焦搜索框, Escape 关闭后返回触发按钮. */
async function open() {
    if (props.disabled) return
    query.value = ""
    visible.value = true
    await nextTick()
    input.value?.focus()
}
/** 搜索跳转携带二级目标, Element Plus 负责关闭后的焦点恢复. */
function choose(item: string[]) {
    if (props.disabled) return
    visible.value = false
    emit("select", item[0]!, item[1]!)
}
/** 上下键移动当前匹配项并滚动到可见范围; 无匹配时保留零索引. */
async function moveResult(delta: number) {
    activeIndex.value = Math.max(0, Math.min(activeIndex.value + delta, results.value.length - 1))
    await nextTick()
    resultList.value?.querySelectorAll("button")[activeIndex.value]?.scrollIntoView({ block: "nearest" })
}
/** 斜杠快捷键只在非输入区生效, 不干扰输入法或其他弹窗. */
function keydown(event: KeyboardEvent) {
    const node = event.target as HTMLElement
    if (
        event.key !== "/" ||
        event.ctrlKey ||
        event.altKey ||
        event.metaKey ||
        event.isComposing ||
        event.repeat ||
        (node instanceof Element && node.closest("input, textarea, select, [contenteditable=true], [role=dialog], [role=combobox]"))
    )
        return
    event.preventDefault()
    void open()
}
onMounted(() => window.addEventListener("keydown", keydown))
onUnmounted(() => window.removeEventListener("keydown", keydown))
</script>
<template>
    <el-button class="nav-search" :disabled="disabled" :title="t('app.searchTitle')" :aria-label="t('app.search')" @click="open"
        ><UiIcon name="search" /><span class="nav-label">{{ t("app.search") }}</span
        ><kbd>/</kbd></el-button
    >
    <UiDialog v-model="visible" :title="t('app.search')" @opened="input?.focus()">
        <el-input
            ref="input"
            v-model="query"
            clearable
            :aria-label="t('app.searchQuery')"
            :placeholder="t('app.searchPlaceholder')"
            @keydown.enter.prevent="results[activeIndex] && choose(results[activeIndex]!)"
            @keydown.down.prevent="moveResult(1)"
            @keydown.up.prevent="moveResult(-1)"
        />
        <div ref="resultList" class="search-results">
            <el-button v-for="(item, index) in results" :key="item[2]" :class="{ active: index === activeIndex }" @click="choose(item)"
                ><UiIcon :name="item[4]!" /><span>{{ item[2] }} · {{ item[3] }}</span></el-button
            ><el-empty v-if="!results.length" :description="t('app.searchEmpty')" :image-size="64" />
        </div>
    </UiDialog>
</template>
