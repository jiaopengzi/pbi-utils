<!--
 * FilePath    : pbi-utils\web\src\app\ModelTableExportToolbar.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 组装统一导出范围、格式和最近目录操作, 所有副作用由父级处理.
-->
<script setup lang="ts">
import type { ModelExportLocation } from "@/bridge/client"
import { vNumberAccessibility } from "@/ui/numberAccessibility"
import { useI18n } from "@/composables/i18n/useI18n"
const props = defineProps<{
    selectedCount: number
    locked: boolean
    busy: boolean
    exportAvailable: boolean
    validLimit: boolean
    location?: ModelExportLocation
    opening: boolean
}>()
const emit = defineEmits<{ export: []; openDirectory: [] }>()
const format = defineModel<"xlsx" | "csv">("format", { required: true })
const includeLog = defineModel<boolean>("includeLog", { required: true })
const scope = defineModel<"sample" | "all">("scope", { required: true })
const rowLimit = defineModel<number | undefined>("rowLimit")
const { t, locale } = useI18n()
</script>
<template>
    <section class="table-export-toolbar" :class="{ 'locale-en': locale === 'en-us' }" :aria-label="t('model.exportData')">
        <details class="export-settings">
            <summary>
                <span>{{ t("model.exportOptions") }}</span>
                <span class="export-summary-text"
                    >{{ t(format === "xlsx" ? "model.exportExcel" : "model.exportCsv") }} ·
                    {{ scope === "all" ? t("model.exportAll") : t("model.exportSampleSummary", { count: rowLimit ?? "-" }) }}</span
                >
            </summary>
            <el-form class="export-options" label-position="top" @submit.prevent>
                <el-form-item class="export-format" :label="t('model.exportFormat')">
                    <el-select
                        :model-value="format"
                        :aria-label="t('model.exportFormat')"
                        :disabled="locked"
                        :class="{ 'operation-lock': locked }"
                        @update:model-value="format = $event === 'csv' ? 'csv' : 'xlsx'"
                    >
                        <el-option value="xlsx" :label="t('model.exportExcel')" /><el-option value="csv" :label="t('model.exportCsv')" />
                    </el-select>
                </el-form-item>
                <el-form-item :label="t('model.exportScope')">
                    <el-radio-group
                        :model-value="scope"
                        :aria-label="t('model.exportScope')"
                        :disabled="locked"
                        :class="{ 'operation-lock': locked }"
                        @update:model-value="scope = $event === 'all' ? 'all' : 'sample'"
                    >
                        <el-radio-button value="sample">{{ t("model.exportSample") }}</el-radio-button
                        ><el-radio-button value="all">{{ t("model.exportAll") }}</el-radio-button>
                    </el-radio-group>
                </el-form-item>
                <el-form-item v-if="scope === 'sample'" class="export-row-limit" :label="t('model.exportRowLimit')">
                    <el-input-number
                        v-number-accessibility
                        :model-value="rowLimit"
                        :aria-label="t('model.exportRowLimit')"
                        :min="1"
                        :max="2147483647"
                        :precision="0"
                        :step="1"
                        :disabled="locked"
                        :class="{ 'operation-lock': locked }"
                        controls-position="right"
                        @update:model-value="rowLimit = $event"
                    />
                </el-form-item>
                <el-form-item v-if="format === 'xlsx'" class="export-summary" :label="t('model.exportLog')">
                    <el-switch
                        :model-value="includeLog"
                        :aria-label="t('model.exportLog')"
                        :disabled="locked"
                        :class="{ 'operation-lock': locked }"
                        @update:model-value="includeLog = Boolean($event)"
                    />
                </el-form-item>
            </el-form>
            <p class="export-format-options">{{ t("model.exportScopeNotice") }}</p>
            <p v-if="format === 'csv'" class="export-format-options">{{ t("model.exportCsvDestination") }}</p>
        </details>
        <div class="table-export-actions">
            <el-button
                class="table-export-button"
                type="primary"
                :disabled="locked || !exportAvailable || !validLimit"
                :loading="busy"
                :class="{ 'operation-lock': locked && selectedCount > 0 }"
                @click="emit('export')"
                >{{ t("model.exportSelected") }}</el-button
            >
        </div>
        <div v-if="props.location?.displayPath" class="export-location">
            <div>
                <small>{{ t("model.exportLastDirectory") }}</small>
                <p class="export-location-path" :title="location?.displayPath ?? undefined">{{ location?.displayPath }}</p>
            </div>
            <el-button
                class="open-export-directory"
                :disabled="locked || !location?.available"
                :loading="opening"
                :title="location?.warning ?? undefined"
                @click="emit('openDirectory')"
                >{{ t("model.exportOpenDirectory") }}</el-button
            >
        </div>
    </section>
</template>
<style scoped lang="scss">
.table-export-toolbar {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-start;
    gap: 12px;
    padding: 12px 16px;
    margin-bottom: 16px;
    border: 1px solid var(--el-border-color);
    border-radius: 6px;
    background: var(--el-fill-color-light);
}
.export-settings {
    flex: 1 1 420px;
    min-width: 0;
    summary {
        cursor: pointer;
        padding-block: 6px;
        line-height: 20px;
        color: var(--el-text-color-primary);
        &:focus-visible {
            outline: 2px solid var(--el-color-primary);
            outline-offset: 3px;
        }
    }
}
.export-summary-text {
    margin-left: 16px;
    color: var(--el-text-color-regular);
    font-size: 13px;
}
.table-export-actions {
    margin-left: auto;
}
.export-options {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-end;
    gap: 16px;
    margin-top: 16px;
    :deep(.el-form-item) {
        margin-bottom: 0;
        max-width: 100%;
    }
    :deep(.el-select) {
        width: 190px;
        max-width: 100%;
    }
    .export-row-limit {
        display: grid;
        grid-template-columns: max-content;
        :deep(.el-form-item__label) {
            width: max-content;
        }
        :deep(.el-form-item__content) {
            width: 0;
            min-width: 100%;
        }
        :deep(.el-input-number) {
            width: 100%;
        }
    }
}
.locale-en .export-format :deep(.el-select) {
    width: 205px;
}
.export-format-options {
    margin: 12px 0 0;
    color: var(--el-text-color-secondary);
    font-size: 12px;
}
.export-location {
    flex-basis: 100%;
    display: flex;
    gap: 20px;
    align-items: center;
    justify-content: space-between;
    border-top: 1px solid var(--el-border-color);
    padding-top: 14px;
    margin-top: 14px;
    min-width: 0;
    > div {
        min-width: 0;
    }
    small {
        color: var(--el-text-color-secondary);
    }
}
.export-location-path {
    margin: 4px 0 0;
    overflow-wrap: anywhere;
    user-select: text;
}
</style>
