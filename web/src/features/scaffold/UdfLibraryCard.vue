<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfLibraryCard.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 包级目录卡片, 顶部集中版本、作者与更新日期, 底部提供函数数量与详情操作.
-->
<script setup lang="ts">
import { ref, watch } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import UdfHighlight from "./UdfHighlight.vue"
import UiIcon from "@/ui/UiIcon.vue"
import { formatUdfLocalTime } from "./udfDateTime"
import type { UdfCatalogPackage } from "./contracts"
/** 卡片只使用宿主摘要; 选择由父级完整核验包函数, 禁用或预发行候选时拒绝选择. */
const props = defineProps<{
    pkg: UdfCatalogPackage
    query: string
    checked: boolean
    disabled: boolean
    operationLocked?: boolean
    icon?: string | null
    candidate?: boolean
}>()
const emit = defineEmits<{ select: [checked: boolean]; details: []; versions: [] }>()
const { t } = useI18n()
const iconFailed = ref(false)
watch(
    () => props.icon,
    () => {
        iconFailed.value = false
    },
)
/** 仅透传可操作的整包勾选, 异步失败后仍由父级权威值控制状态. */
function select(value: unknown, event?: Event): void {
    if (!props.disabled && !props.candidate) emit("select", value === true)
    if (event?.target instanceof HTMLInputElement) event.target.checked = props.checked
}
</script>
<template>
    <article class="udf-library-card" :class="{ 'is-selected': checked }" :data-package="pkg.id">
        <header class="udf-package-heading">
            <img v-if="icon && !iconFailed" class="udf-package-icon" :src="icon" alt="" loading="lazy" decoding="async" @error="iconFailed = true" />
            <UiIcon v-else name="square-function" class="udf-package-icon" />
            <h3><UdfHighlight :text="pkg.id" :query="query" /></h3>
            <el-checkbox
                :model-value="checked"
                :disabled="disabled || candidate"
                :class="{ 'operation-lock': operationLocked && !candidate }"
                :aria-label="t('scaffold.selectPackage', { name: pkg.id })"
                @change="select"
            />
        </header>
        <div class="udf-package-version">
            <el-button
                :disabled="disabled"
                :class="{ 'operation-lock': operationLocked }"
                :aria-label="t('scaffold.packageTag', { name: pkg.id })"
                :title="candidate ? t('scaffold.choosePrereleaseTag') : undefined"
                @click="!disabled && emit('versions')"
                >{{ pkg.version }}</el-button
            >
            <div class="udf-package-meta">
                <span class="udf-package-author">{{ t("scaffold.authors") }}: <UdfHighlight :text="pkg.authors || '-'" :query="query" /></span>
                <span class="udf-package-date">{{ t("scaffold.committedAt") }}: {{ formatUdfLocalTime(pkg.committedAt).slice(0, 10) }}</span>
            </div>
        </div>
        <p class="udf-package-description"><UdfHighlight :text="pkg.description || '-'" :query="query" /></p>
        <div class="udf-package-tags" :aria-label="t('scaffold.categoryTags')">
            <el-tag v-for="tag in pkg.tags" :key="tag" effect="plain"><UdfHighlight :text="tag" :query="query" /></el-tag
            ><span v-if="!pkg.tags.length" class="muted">-</span>
        </div>
        <footer>
            <span class="udf-package-count muted">{{ t("scaffold.packageFunctionCount", { count: pkg.functionCount }) }}</span>
            <el-button
                :disabled="disabled"
                :class="{ 'operation-lock': operationLocked }"
                :aria-label="`${t('scaffold.packageDetails')} ${pkg.id}`"
                @click="!disabled && emit('details')"
                >{{ t("scaffold.packageDetails") }}</el-button
            >
        </footer>
    </article>
</template>
