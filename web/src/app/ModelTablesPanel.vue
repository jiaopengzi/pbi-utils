<!--
 * FilePath    : pbi-utils\web\src\app\ModelTablesPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 组装表格选择、显式精确计数、抽样导出和分区详情, 保持共享操作锁与迟到结果保护.
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue"
import UiPanel from "@/ui/UiPanel.vue"
import UiSearchHighlight from "@/ui/UiSearchHighlight.vue"
import ModelTableDetails from "./ModelTableDetails.vue"
import ModelTableExportToolbar from "./ModelTableExportToolbar.vue"
import { useModelTableExport, type ModelTableExportProps } from "@/composables/app/useModelTableExport"
import type { ProjectSnapshot } from "@/bridge/client"
import { useModelTableStatistics } from "@/composables/app/useModelTableStatistics"
import { useModelExportLocation } from "@/composables/app/useModelExportLocation"
import { useModelTableDeletion } from "@/composables/app/useModelTableDeletion"
const props = defineProps<ModelTableExportProps>()
const emit = defineEmits<{ busy: [value: boolean]; updated: [value: ProjectSnapshot] }>()
const locations = useModelExportLocation(props)
const exporting = useModelTableExport(props, () => locations.refresh(false))
const {
    t,
    selected,
    selectionBaselineId,
    format,
    includeLog,
    scope,
    rowLimit,
    validLimit,
    busy,
    selectedTables,
    exportAvailable,
    selectAll,
    selectTable,
    selectionUnavailable,
    exportTables,
} = exporting
const deletion = useModelTableDeletion(
    props,
    () => selectedTables.value.map((table) => table.name),
    () => selectionBaselineId.value,
    () => busy.value || statistics.busy.value || locations.busy.value || !!props.locked,
    (project) => emit("updated", project),
)
const statistics = useModelTableStatistics(
    props,
    (project) => emit("updated", project),
    () => exporting.busy.value || locations.busy.value || deletion.busy.value,
)
const operationBusy = computed(() => busy.value || statistics.busy.value || locations.busy.value || deletion.busy.value)
const locked = computed(() => operationBusy.value || !!props.locked)
const query = ref("")
const visibleTables = computed(() => {
    const words = query.value.trim().toLowerCase().split(/\s+/u)
    return props.project.model.tables.filter((table) => words.every((word) => table.name.toLowerCase().includes(word)))
})
const visibleEligible = computed(() => visibleTables.value.filter((table) => !selectionUnavailable(table)).map((table) => table.name))
const visibleSelected = computed(() => visibleEligible.value.filter((name) => selected.value.includes(name)))
const allSelected = computed(() => visibleEligible.value.length > 0 && visibleSelected.value.length === visibleEligible.value.length)
const indeterminate = computed(() => visibleSelected.value.length > 0 && !allSelected.value)
const hiddenSelected = computed(() => selectedTables.value.length - visibleSelected.value.length)
// 同模型统计刷新保留查询, 切换真实目标后清空, 避免旧条件遮住新模型.
watch(
    () => [props.project.projectId, props.project.connection?.instanceId, props.project.connection?.modelId],
    (next, previous) => {
        if (next.some((value, index) => value !== previous[index])) query.value = ""
    },
)
let disposed = false
watch(
    operationBusy,
    (value) => {
        if (!disposed) emit("busy", value)
    },
    { flush: "sync", immediate: true },
)
onBeforeUnmount(() => {
    disposed = true
})
/** 所有界面动作再次检查共享锁, 不能通过合成事件绕过真实disabled. */
function perform(action: () => void | Promise<void>): void {
    if (!locked.value) void action()
}
/** 只将当前所选表交给精确计数, 不在展开或进入页面时隐式扫描全部业务数据. */
function countSelected(): void {
    perform(() => statistics.countExact(selectedTables.value.map((table) => table.name)))
}
/** 内部行号不参与可见字段数量, 保留名称相似的普通数据列. */
const fieldCount = (table: ModelTableExportProps["project"]["model"]["tables"][number]) => table.columns.filter((column) => column.kind !== "RowNumber").length
</script>
<template>
    <UiPanel class="model-tables-panel">
        <div class="tables-heading">
            <div>
                <h2>{{ t("model.tableCount", { count: project.model.tables.length }) }}</h2>
                <p v-if="statistics.hasEstimates.value" class="statistics-estimate-help">{{ t("model.statisticsEstimateHelp") }}</p>
            </div>
            <div class="tables-actions">
                <el-button :disabled="locked" :class="{ 'operation-lock': locked }" @click="perform(statistics.refresh)">{{
                    t("model.statisticsRefresh")
                }}</el-button>
                <el-button
                    class="exact-row-count"
                    :disabled="locked || !selectedTables.length"
                    :loading="statistics.counting.value"
                    :title="t('model.exactRowsHelp')"
                    :class="{ 'operation-lock': locked && selectedTables.length > 0 }"
                    @click="countSelected"
                    >{{ t("model.exactSelectedRows") }}</el-button
                >
            </div>
        </div>
        <ModelTableExportToolbar
            v-model:format="format"
            v-model:include-log="includeLog"
            v-model:scope="scope"
            v-model:row-limit="rowLimit"
            :selected-count="selectedTables.length"
            :locked="locked"
            :busy="busy"
            :export-available="exportAvailable"
            :can-delete="deletion.writable.value"
            :deleting="deletion.busy.value"
            :valid-limit="validLimit"
            :location="locations.location.value"
            :opening="locations.opening.value"
            @export="perform(exportTables)"
            @delete="perform(deletion.deleteSelected)"
            @open-directory="perform(locations.open)"
        />
        <div class="table-search-toolbar">
            <el-input v-model="query" clearable :disabled="locked" :aria-label="t('model.tableSearch')" :placeholder="t('model.tableSearch')" />
        </div>
        <p class="table-search-summary" role="status">
            {{
                t("model.tableSearchSummary", {
                    visible: visibleTables.length,
                    total: project.model.tables.length,
                    selected: selectedTables.length,
                    hidden: hiddenSelected,
                })
            }}
        </p>
        <div class="table-list-head">
            <el-checkbox
                :model-value="allSelected"
                :indeterminate="indeterminate"
                :disabled="locked || !visibleEligible.length"
                :aria-label="t(query.trim() ? 'model.tableSelectVisible' : deletion.writable.value ? 'model.tableDeleteSelectAll' : 'model.exportSelectAll')"
                @update:model-value="!locked && selectAll(Boolean($event), visibleEligible)"
            />
            <div class="table-title">
                <span>{{ t("model.tableName") }}</span
                ><span>{{ t("model.rowCount") }}</span
                ><span>{{ t("model.storageSize") }}</span
                ><span>{{ t("model.fieldCount") }}</span>
            </div>
        </div>
        <div v-for="table in visibleTables" :key="table.name" class="model-table-entry">
            <el-checkbox
                class="table-selection"
                :model-value="selected.includes(table.name)"
                :disabled="locked || !!selectionUnavailable(table)"
                :aria-label="t('model.exportSelectTable', { table: table.name })"
                :title="selectionUnavailable(table) ? t(selectionUnavailable(table)) : undefined"
                @update:model-value="!locked && selectTable(table, Boolean($event))"
            />
            <el-collapse
                ><el-collapse-item :name="table.name">
                    <template #title
                        ><div class="table-title">
                            <span class="table-name"><UiSearchHighlight :text="table.name" :query="query" /></span
                            ><span :title="statistics.countKind(table.name) === 'query' ? t('model.queryRowCount') : undefined">{{
                                statistics.rowCount(table.name)
                            }}</span
                            ><span>{{ statistics.size(table.name) }}</span
                            ><span>{{ fieldCount(table) }}</span>
                        </div></template
                    >
                    <ModelTableDetails
                        :table="table"
                        :limited="!!project.connection?.isLimited"
                        :row-count="statistics.rowCount(table.name)"
                        :storage-size="statistics.size(table.name)"
                        :count-kind="statistics.countKind(table.name)"
                    /> </el-collapse-item
            ></el-collapse>
        </div>
        <el-empty
            v-if="!visibleTables.length"
            :description="t(project.model.tables.length ? 'model.tableSearchEmpty' : 'model.exportNoTables')"
            :image-size="64"
        />
        <p v-if="project.connection?.isLimited">{{ t("model.relationsUnavailable") }}</p>
        <p v-else>{{ t("model.relationCounts", { relationships: project.model.relationshipCount, roles: project.model.roleCount }) }}</p>
    </UiPanel>
</template>
<style scoped lang="scss">
.table-search-toolbar {
    display: flex;
    gap: 12px;
    align-items: center;
    margin-top: 16px;
    :deep(.el-input) {
        max-width: 420px;
    }
}
.table-search-summary {
    color: var(--el-text-color-regular);
    font-size: 12px;
    margin: 12px 0;
}
.tables-heading {
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    gap: 16px 24px;
    margin-bottom: 20px;
    h2 {
        margin: 0 0 8px;
    }
    p {
        margin: 4px 0 0;
        color: var(--el-text-color-secondary);
        font-size: 12px;
    }
}
.tables-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 12px;
    align-items: center;
    :deep(.el-button + .el-button) {
        margin-left: 0;
    }
}
.table-list-head,
.model-table-entry {
    display: grid;
    grid-template-columns: 28px minmax(0, 1fr);
    gap: 12px;
}
.table-list-head {
    align-items: center;
    color: var(--el-text-color-secondary);
    font-size: 12px;
    padding: 0 46px 8px 0;
    .table-title {
        padding-left: 20px;
    }
}
.table-title {
    display: grid;
    grid-template-columns: minmax(140px, 1fr) 120px 160px 82px;
    width: 100%;
    align-items: center;
    gap: 12px;
    text-align: left;
}
.table-name {
    overflow-wrap: anywhere;
    font-weight: 600;
}
.model-table-entry {
    margin-bottom: 16px;
    :deep(.el-collapse) {
        border: 1px solid var(--el-border-color);
        border-radius: 10px;
        overflow: hidden;
    }
    :deep(.el-collapse-item) {
        border-left: 3px solid transparent;
    }
    :deep(.el-collapse-item.is-active) {
        border-left-color: var(--el-color-primary);
    }
    :deep(.el-collapse-item__header) {
        min-height: 56px;
        height: auto;
        padding: 12px 16px;
        border-bottom: 0;
        line-height: 1.5;
    }
    :deep(.el-collapse-item.is-active > .el-collapse-item__header) {
        background: var(--el-color-primary-light-9);
    }
    :deep(.el-collapse-item__wrap) {
        border-bottom: 0;
    }
    :deep(.el-collapse-item__content) {
        padding-bottom: 0;
    }
}
.table-selection {
    align-self: start;
    margin-top: 14px;
}
</style>
