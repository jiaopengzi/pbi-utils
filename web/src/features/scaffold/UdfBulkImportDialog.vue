<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfBulkImportDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 全量函数选择的来源、许可和依赖审阅, 加载中允许取消并等待排空.
-->
<script setup lang="ts">
import UdfFunctionList from "./UdfFunctionList.vue"
import UiDialog from "@/ui/UiDialog.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
import type { UdfRequests } from "@/composables/features/scaffold/useUdfRequests"
import type { UdfBulkImport } from "@/composables/features/scaffold/useUdfBulkImport"
import { formatUdfLocalTime } from "./udfDateTime"
/** 只展示已核验的全量包与原始许可; 取消不更新外部选择. */
const props = defineProps<{
    state: Pick<UdfState, "bulkImport" | "busy" | "cancelling" | "error">
    actions: UdfBulkImport
    cancel: UdfRequests["cancel"]
    sourceLabel: string
}>()
const { bulkImport, busy, cancelling } = props.state
const { entries, ready, cancelled, count, canConfirm, confirm } = props.actions
const { t } = useI18n()
</script>
<template>
    <UiDialog v-model="bulkImport" :title="t('scaffold.bulkImport')" :busy="busy">
        <div class="udf-bulk-preview" :aria-busy="busy">
            <p>{{ t("scaffold.bulkHint", { source: sourceLabel }) }}</p>
            <p v-if="ready" role="status">{{ t("scaffold.bulkSummary", { packages: entries.length, functions: count }) }}</p>
            <p v-if="ready && !entries.length" class="udf-empty">{{ t("scaffold.bulkEmpty") }}</p>
            <p v-if="cancelled" role="status">{{ t("scaffold.bulkCancelled") }}</p>
            <section v-for="(item, index) in entries" :key="item.selection.packageId" class="udf-bulk-package">
                <h3>
                    {{ item.selection.source === "local" ? item.selection.functions[0] : item.selection.packageId }} @
                    {{ item.package?.version ?? item.selection.version }}
                </h3>
                <UdfFunctionList
                    :functions="item.selection.functions"
                    :start="1 + entries.slice(0, index).reduce((total, entry) => total + entry.selection.functions.length, 0)"
                />
                <template v-if="item.package">
                    <p v-if="item.package.fromCache" class="muted">
                        {{ t("scaffold.cache") }} · {{ t("scaffold.retrievedAt") }}: {{ formatUdfLocalTime(item.package.retrievedAt) }}
                    </p>
                    <p v-if="item.package.dependencies.length">
                        {{ t("scaffold.declaredDependencies") }}:
                        {{ item.package.dependencies.map((dependency) => `${dependency.id}@${dependency.version}`).join(", ") }}
                    </p>
                    <details>
                        <summary>{{ t("scaffold.licenses") }}</summary>
                        <section v-for="(license, name) in item.package.licenseFiles" :key="name">
                            <h4>{{ name }}</h4>
                            <pre>{{ license }}</pre>
                        </section>
                    </details>
                </template>
            </section>
        </div>
        <template #footer>
            <el-button :disabled="busy && cancelling" @click="busy ? cancel() : (bulkImport = false)">{{ t("scaffold.cancel") }}</el-button>
            <el-button type="primary" :loading="busy" :disabled="!canConfirm" @click="confirm">{{ t("scaffold.bulkConfirm") }}</el-button>
        </template>
    </UiDialog>
</template>
