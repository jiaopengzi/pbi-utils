<!--
 * FilePath    : pbi-utils\web\src\app\SamplePanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 示例数据生成与管理, 表单只提交业务参数, 产物和进度由共享 C# 服务回读.
-->
<script setup lang="ts">
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import UiIcon from "@/ui/UiIcon.vue"
import { useSampleOperations, type SampleOperationsProps } from "@/composables/app/useSampleOperations"
const props = defineProps<SampleOperationsProps>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const {
    options,
    text,
    state,
    busy,
    active,
    error,
    nextDate,
    parameterError,
    canGenerate,
    shortcuts,
    inspect,
    generate,
    clear,
    openDirectory,
    useNextDate,
    disabledDate,
    locale,
    formatSize,
} = useSampleOperations(props, emit)
useMessageNotices("sample-panel", () => [
    !!parameterError.value && { key: `parameter:${parameterError.value}`, message: text.value[parameterError.value] },
    !!error.value && { key: "error", message: error.value, type: "error" },
])
</script>
<template>
    <div class="sample-panel" :aria-busy="busy">
        <UiPanel class="sample-config">
            <div class="sample-heading">
                <h2>{{ text.title }}</h2>
            </div>
            <el-form class="sample-form" label-position="top" :disabled="busy" @submit.prevent="generate('full')">
                <div class="sample-dates">
                    <el-form-item :label="text.startDate"
                        ><el-date-picker
                            v-model="options.startDate"
                            :aria-label="text.startDate"
                            type="date"
                            value-format="YYYY-MM-DD"
                            format="YYYY-MM-DD"
                            :shortcuts="shortcuts"
                            :disabled-date="disabledDate"
                            :clearable="false"
                    /></el-form-item>
                    <el-form-item :label="text.endDate"
                        ><el-date-picker
                            v-model="options.endDate"
                            :aria-label="text.endDate"
                            type="date"
                            value-format="YYYY-MM-DD"
                            format="YYYY-MM-DD"
                            :shortcuts="shortcuts"
                            :disabled-date="disabledDate"
                            :clearable="false"
                    /></el-form-item>
                </div>
                <div class="sample-counts">
                    <el-form-item :label="text.products"
                        ><el-input-number
                            :model-value="options.productCount"
                            :value-on-clear="options.productCount"
                            @update:model-value="$event !== undefined && (options.productCount = $event)"
                            :aria-label="text.products"
                            :min="1"
                            :max="10000"
                            :precision="0"
                            controls-position="right"
                    /></el-form-item>
                    <el-form-item :label="text.stores"
                        ><el-input-number
                            :model-value="options.storeCount"
                            :value-on-clear="options.storeCount"
                            @update:model-value="$event !== undefined && (options.storeCount = $event)"
                            :aria-label="text.stores"
                            :min="1"
                            :max="10000"
                            :precision="0"
                            controls-position="right"
                    /></el-form-item>
                    <el-form-item :label="text.inventory"
                        ><el-input-number
                            :model-value="options.inventoryCycle"
                            :value-on-clear="options.inventoryCycle"
                            @update:model-value="$event !== undefined && (options.inventoryCycle = $event)"
                            :aria-label="text.inventory"
                            :min="5"
                            :max="180"
                            :precision="0"
                            controls-position="right"
                    /></el-form-item>
                </div>
                <el-form-item :label="text.output" class="sample-directory">
                    <div class="sample-directory-row">
                        <el-input :model-value="state?.outputPath ?? ''" :aria-label="text.output" readonly /><el-button
                            :loading="active === 'choose'"
                            :disabled="busy"
                            @click="inspect(true)"
                            ><UiIcon name="folder" />{{ text.choose }}</el-button
                        ><el-button :disabled="busy || !state?.outputPath" @click="openDirectory"><UiIcon name="folder" />{{ text.open }}</el-button>
                    </div>
                </el-form-item>
                <div v-if="state?.lastFactDate" class="sample-cutoff">
                    <el-tag type="info">{{ text.cutoff }} {{ state.lastFactDate }}</el-tag
                    ><span v-if="nextDate">{{ text.nextDate }} {{ nextDate }}</span
                    ><el-button v-if="nextDate" text :disabled="busy" @click="useNextDate"><UiIcon name="arrow-right" />{{ text.useNext }}</el-button>
                </div>

                <div class="sample-actions">
                    <el-button type="primary" native-type="submit" :loading="active === 'full'" :disabled="!canGenerate"
                        ><UiIcon name="database" />{{ text.generate }}</el-button
                    ><el-button
                        type="primary"
                        plain
                        :loading="active === 'incremental'"
                        :disabled="!canGenerate || !state?.hasData"
                        @click="generate('incremental')"
                        ><UiIcon name="calendar-days" />{{ text.incremental }}</el-button
                    ><el-button type="danger" plain :disabled="busy || !state?.hasData" :loading="active === 'clear'" @click="clear"
                        ><UiIcon name="trash-2" />{{ text.clear }}</el-button
                    >
                </div>
            </el-form>

            <p class="sample-footer">
                <a href="https://jiaopengzi.com/p/19051044919050241" target="_blank" rel="noopener noreferrer">{{ text.docs }} ↗</a>
            </p>
        </UiPanel>
        <UiPanel v-if="state?.tables.length" class="sample-results"
            ><div class="sample-result-heading">
                <h2>{{ text.result }}</h2>
                <el-tag v-if="state?.isSimulation" type="info">{{ text.simulationLabel }}</el-tag>
            </div>
            <el-table :data="state.tables" row-key="name" stripe :aria-label="text.result"
                ><el-table-column prop="name" :label="text.table" /><el-table-column prop="rows" :label="text.rows" align="right"
                    ><template #default="scope">{{ scope.row.rows.toLocaleString(locale) }}</template></el-table-column
                ><el-table-column :label="text.size" align="right"
                    ><template #default="scope">{{ formatSize(scope.row.size) }}</template></el-table-column
                ></el-table
            ></UiPanel
        >
    </div>
</template>
<style lang="scss" scoped>
.sample-panel {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    align-items: start;
    gap: 22px;
}
.sample-heading {
    display: flex;
    align-items: center;
    gap: 24px;
    justify-content: space-between;
    flex-wrap: wrap;
}
.sample-heading h2 {
    margin: 0;
}
.sample-form {
    margin-top: 24px;
}
.sample-dates {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(200px, 100%), 1fr));
    gap: 18px;
}
.sample-dates :deep(.el-date-editor.el-input) {
    width: 100%;
}
.sample-counts {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(180px, 100%), 1fr));
    gap: 14px;
}
.sample-counts :deep(.el-input-number) {
    width: 100%;
}
.sample-directory-row {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    width: 100%;
    min-width: 0;
}
.sample-directory-row :deep(.el-input) {
    flex: 1 1 220px;
    min-width: 0;
}
.sample-directory-row :deep(.el-button) {
    margin-left: 0;
}
.sample-cutoff {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 10px;
    margin: 18px 0;
    font-size: 12px;
    color: var(--muted);
}
.sample-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    margin-top: 22px;
}
.sample-footer {
    border-top: 1px solid var(--border);
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 10px;
    padding-top: 18px;
    margin: 22px 0 0;
    font-size: 12px;
    color: var(--muted);
}
.sample-footer a {
    color: var(--brand-primary);
}
.sample-result-heading {
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-wrap: wrap;
    gap: 12px;
    margin-bottom: 14px;
}
.sample-result-heading h2 {
    margin: 0;
}
.sample-config,
.sample-results {
    min-width: 0;
}
</style>
