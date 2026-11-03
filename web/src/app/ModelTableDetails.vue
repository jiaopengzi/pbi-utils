<!--
 * FilePath    : pbi-utils\web\src\app\ModelTableDetails.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 以清晰分区展示单表元信息和实体字段, 不改动模型或产生平台副作用.
-->
<script setup lang="ts">
import { computed } from "vue"
import type { ProjectSnapshot } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
type ModelTable = ProjectSnapshot["model"]["tables"][number]
const props = defineProps<{
    table: ModelTable
    limited: boolean
    rowCount: string
    storageSize: string
    countKind: "query" | "estimate" | "storage" | "unknown" | "unavailable"
}>()
const { t, hostText } = useI18n()
/** 内部RowNumber按引擎kind排除, 保留名称相似的普通数据列. */
const fields = computed(() => props.table.columns.filter((column) => column.kind !== "RowNumber"))
</script>
<template>
    <div class="table-detail-content">
        <section class="table-metadata" :aria-label="t('model.tableMetadata')">
            <h3>{{ t("model.tableMetadata") }}</h3>
            <dl>
                <div>
                    <dt>{{ t("model.fieldCount") }}</dt>
                    <dd>{{ fields.length }}</dd>
                </div>
                <div>
                    <dt>{{ t("model.rowCount") }}</dt>
                    <dd>
                        {{ rowCount }}<small v-if="countKind === 'query'" class="count-source">{{ t("model.queryRowCount") }}</small
                        ><small v-else-if="countKind === 'estimate'" class="count-source">{{ t("model.estimatedRowCount") }}</small
                        ><small v-else-if="countKind === 'unavailable'" class="count-source">{{ t("model.rowCountNoColumns") }}</small>
                    </dd>
                </div>
                <div>
                    <dt>{{ t("model.storageSize") }}</dt>
                    <dd>{{ storageSize }}</dd>
                </div>
                <div>
                    <dt>{{ t("model.partitions") }}</dt>
                    <dd>{{ table.partitionKinds.length ? table.partitionKinds.join(", ") : t("model.notProvided") }}</dd>
                </div>
            </dl>
            <p v-if="table.description" class="table-description">{{ table.description }}</p>
        </section>
        <section class="table-fields-section">
            <div class="detail-section-heading">
                <h3>{{ t("model.tableFields") }}</h3>
                <span>{{ t("model.visibleFieldCount", { count: fields.length }) }}</span>
            </div>
            <div class="fields-table-scroll" role="region" :aria-label="t('model.tableFields')" tabindex="0">
                <table class="fields-table" :aria-label="t('model.tableFields')">
                    <thead>
                        <tr>
                            <th scope="col">{{ t("model.columnName") }}</th>
                            <th scope="col">{{ t("model.dataType") }}</th>
                            <th scope="col">{{ t("model.hidden") }}</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-for="column in fields" :key="column.name">
                            <td>{{ column.name }}</td>
                            <td>{{ hostText(column.dataType) }}</td>
                            <td>{{ limited ? t("model.notProvided") : column.isHidden ? t("model.yes") : t("model.no") }}</td>
                        </tr>
                    </tbody>
                </table>
            </div>
        </section>
    </div>
</template>
<style scoped lang="scss">
.table-detail-content {
    display: grid;
    gap: 20px;
    padding: 20px;
    border-top: 1px solid var(--el-border-color);
    background: var(--el-fill-color-light);
}
.table-metadata,
.table-fields-section {
    background: var(--el-bg-color);
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 8px;
    overflow: hidden;
}
.table-metadata {
    padding: 18px 20px;
    h3 {
        padding-left: 10px;
        border-left: 3px solid var(--el-color-primary);
    }
    dl {
        display: grid;
        grid-template-columns: repeat(4, minmax(0, 1fr));
        gap: 20px;
        margin: 16px 0 0;
    }
    dt {
        color: var(--el-text-color-secondary);
        font-size: 12px;
    }
    dd {
        margin: 6px 0 0;
        overflow-wrap: anywhere;
    }
}
h3 {
    margin: 0;
    font-size: 14px;
    font-weight: 600;
}
.count-source {
    display: block;
    margin-top: 4px;
    color: var(--el-text-color-secondary);
    font-size: 12px;
}
.table-description {
    margin: 16px 0 0;
    padding-top: 12px;
    border-top: 1px solid var(--el-border-color-lighter);
    color: var(--el-text-color-secondary);
}
.detail-section-heading {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    padding: 16px 20px;
    background: var(--el-fill-color-light);
    border-bottom: 1px solid var(--el-border-color);
    span {
        color: var(--el-text-color-secondary);
        font-size: 12px;
    }
}
.fields-table-scroll {
    width: 100%;
    overflow: auto;
}
.fields-table {
    width: 100%;
    min-width: 360px;
    border-collapse: collapse;
    text-align: left;
    th,
    td {
        padding: 10px 20px;
        border-bottom: 1px solid var(--el-border-color-lighter);
        overflow-wrap: anywhere;
    }
    th {
        color: var(--el-text-color-secondary);
        font-weight: 500;
    }
    tbody tr:nth-child(even) {
        background: var(--el-fill-color-lighter);
    }
    tbody tr:last-child td {
        border-bottom: 0;
    }
}
</style>
