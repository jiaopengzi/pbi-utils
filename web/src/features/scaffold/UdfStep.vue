<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfStep.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按登记来源选择精确函数, 组合目录与本地函数管理并保留依赖和许可边界.
-->
<script setup lang="ts">
import { computed, onMounted, watch } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import ScaffoldField from "./ScaffoldField.vue"
import UdfFunctionList from "./UdfFunctionList.vue"
import UdfLocalBrowser from "./UdfLocalBrowser.vue"
import UdfRevalidationDialog from "./UdfRevalidationDialog.vue"
import UdfPackageDetailDialog from "./UdfPackageDetailDialog.vue"
import UdfTagDialog from "./UdfTagDialog.vue"
import { formatUdfLocalTime } from "./udfDateTime"
import { useUdfCache } from "@/composables/features/scaffold/useUdfCache"
import UdfCatalogBrowser from "./UdfCatalogBrowser.vue"
import UdfSourceManager from "./UdfSourceManager.vue"
import UdfLocalManager from "./UdfLocalManager.vue"
import UdfBulkImportDialog from "./UdfBulkImportDialog.vue"
import type { UdfSelection } from "./contracts"
import { useUdfState } from "@/composables/features/scaffold/useUdfState"
import { useUdfRequests } from "@/composables/features/scaffold/useUdfRequests"
import { useUdfCatalog } from "@/composables/features/scaffold/useUdfCatalog"
import { useLocalUdfLibrary } from "@/composables/features/scaffold/useLocalUdfLibrary"
import { useUdfSources } from "@/composables/features/scaffold/useUdfSources"
import { useUdfBulkImport } from "@/composables/features/scaffold/useUdfBulkImport"
import "./udf.scss"
/** 按来源组合目录与管理器, 仍向外发出请求和模态共同占用的导航锁. */
const model = defineModel<UdfSelection[]>({ required: true })
const props = withDefaults(defineProps<{ bridge: BridgeClient; disabled: boolean; operationLocked?: boolean; active?: boolean }>(), { active: true })
defineSlots<{ actions(props: { canSubmit: boolean }): unknown }>()
const emit = defineEmits<{ busy: [value: boolean]; validity: [value: boolean] }>()
const { t } = useI18n()
const state = useUdfState(props.bridge, model, () => props.disabled)
const requests = useUdfRequests(state, (value) => emit("busy", value))
const catalog = useUdfCatalog(state, requests)
const cache = useUdfCache(state, requests, catalog.load)
const bulk = useUdfBulkImport(state, requests)
const local = useLocalUdfLibrary(state, requests, catalog.commitSelection)
const sourceActions = useUdfSources(state, requests, local.listLocal, cache.load)
const { source, sources, busy, locked, error, query, catalogStatus } = state
const { selectionFields } = catalog
// 编号独立于包来源连续累计, 移除或重排后随真实选择同步更新.
const selectionStarts = computed(() => {
    let start = 1
    return model.value.map((item) => {
        const current = start
        start += item.functions.length
        return current
    })
})
const { changeSource, openSources, sourceLabel, listSources } = sourceActions
const { openManager, listLocal, toggleLocal } = local
// 来源、编辑器和批量导入共享同一错误通道, 只在此处提示一次.
useMessageNotices("scaffold.udf", () => [{ key: "error", message: error.value, type: "error" }])
// 仅在当前来源确认为未初始化且页面可操作时提示; 同一来源配置不因搜索、切页或翻译重复提醒, 不自动发起更新.
useMessageNotices(
    "scaffold.udf.firstUpdate",
    () => [
        catalogStatus.value?.initialized === false &&
            state.currentSource.value?.kind !== "local" &&
            !!state.currentSource.value?.baseUrl?.trim() && {
                key: JSON.stringify([source.value, state.currentSource.value.baseline, state.currentSource.value.baseUrl]),
                message: t("scaffold.catalogFirstUpdateRequired"),
                once: true,
            },
    ],
    { ready: () => props.active !== false && !locked.value && !props.operationLocked && !state.uncertain.value },
)
watch(cache.valid, (value) => emit("validity", value), { immediate: true })
onMounted(async () => {
    await listSources()
    await cache.initialize()
    await listLocal()
})
</script>
<template>
    <div class="udf-step" :aria-busy="busy">
        <section class="udf-source-section" :aria-label="t('scaffold.sourceAndUpdates')">
            <h3>{{ t("scaffold.sourceAndUpdates") }}</h3>
            <div class="scaffold-toolbar udf-source-toolbar">
                <label class="udf-source-picker"
                    >{{ t("scaffold.source") }}
                    <el-select
                        v-model="source"
                        data-test="udf-source"
                        :placeholder="t('scaffold.chooseSource')"
                        :class="{ 'operation-lock': operationLocked || busy }"
                        :disabled="locked"
                        @change="changeSource"
                    >
                        <el-option v-for="item in sources" :key="item.id" :value="item.id" :label="sourceLabel(item.id)" />
                    </el-select>
                </label>
                <el-button
                    data-test="udf-refresh"
                    :class="{ 'operation-lock': operationLocked || busy }"
                    :disabled="locked || state.uncertain.value"
                    @click="cache.refresh"
                    >{{ t("scaffold.refreshSource") }}</el-button
                >
            </div>
            <details class="udf-management">
                <summary>{{ t("scaffold.udfManagement") }}</summary>
                <div class="scaffold-toolbar">
                    <el-button :class="{ 'operation-lock': operationLocked || busy }" :disabled="locked" @click="openSources">{{
                        t("scaffold.sourceManager")
                    }}</el-button>
                    <el-button :class="{ 'operation-lock': operationLocked || busy }" :disabled="locked" @click="openManager">{{
                        t("scaffold.localManager")
                    }}</el-button>
                </div>
            </details>
            <p v-if="catalogStatus?.initialized && source !== 'local'" class="muted" role="status">
                {{ t("scaffold.catalogSummary", { packages: catalogStatus.packageCount, functions: catalogStatus.functionCount }) }} ·
                {{ t("scaffold.lastUpdated") }}: {{ formatUdfLocalTime(catalogStatus.retrievedAt) }}
            </p>
            <section v-if="state.uncertain.value" class="udf-stale" role="status">
                <p>{{ cache.failureMessage.value }}</p>
                <el-button :disabled="busy" @click="cache.verify">{{ t("scaffold.verifyCatalog") }}</el-button>
            </section>
        </section>
        <section class="udf-browse-section" :aria-label="t('scaffold.browseFunctions')">
            <h3>{{ t("scaffold.browseFunctions") }}</h3>
            <template v-if="source && source !== 'local'">
                <UdfCatalogBrowser :state="state" :catalog="catalog" :local="local" :operation-locked="operationLocked" @select-source="bulk.prepare" />
            </template>
            <template v-else-if="source === 'local'">
                <div class="scaffold-toolbar udf-search">
                    <el-input
                        v-model="query"
                        :disabled="locked"
                        :aria-label="t('scaffold.functionNameSearch')"
                        :placeholder="t('scaffold.functionNameSearch')"
                        clearable
                        @keydown.enter.prevent="catalog.search"
                        @compositionstart="catalog.compositionStart"
                        @compositionend="catalog.compositionEnd"
                    />
                </div>
                <section class="udf-browser" :aria-label="t('scaffold.local')">
                    <header class="udf-list-heading">
                        <h4>{{ t("scaffold.local") }}</h4>
                        <div class="scaffold-toolbar udf-list-actions">
                            <el-button :class="{ 'operation-lock': operationLocked || busy }" :disabled="locked" @click="listLocal">{{
                                t("scaffold.refresh")
                            }}</el-button>
                            <el-button
                                :class="{ 'operation-lock': operationLocked || busy }"
                                :disabled="locked || !state.currentSource.value"
                                @click="bulk.prepare"
                                >{{ t("scaffold.bulkImport") }}</el-button
                            >
                        </div>
                    </header>
                    <UdfLocalBrowser
                        v-model:details-open="state.localDetailOpen.value"
                        :items="state.locals.value"
                        :selections="model"
                        :active-versions="state.localPackageTags.value"
                        :query="state.searchQuery.value"
                        :disabled="locked"
                        :busy="busy"
                        :operation-locked="operationLocked || busy"
                        @select="toggleLocal"
                        @change-version="local.changeVersion"
                        @select-page="local.selectPage"
                        @edit="local.edit"
                    />
                </section>
            </template>
            <p v-else class="udf-empty">{{ t("scaffold.chooseSource") }}</p>
        </section>
        <section class="udf-selected" aria-live="polite">
            <h3>
                {{ t("scaffold.selected") }} <span class="muted">{{ model.reduce((total, item) => total + item.functions.length, 0) }}</span>
            </h3>
            <p v-if="!model.length" class="muted">{{ t("scaffold.noSelectedFunctions") }}</p>
            <ScaffoldField
                v-for="(item, index) in model"
                :key="`${item.source}/${item.packageId}`"
                field="packageId"
                :aliases="selectionFields(index + 1)"
                :row="index + 1"
                tag="div"
                class="scaffold-selection udf-selected-package"
            >
                <header class="udf-selected-package-heading">
                    <div class="udf-selected-package-title">
                        <h4>{{ item.source === "local" ? item.functions[0] : item.packageId }}</h4>
                        <span class="muted">{{ sourceLabel(item.source) }}</span>
                        <el-tag effect="plain">{{ cache.versionLabel(item) }}</el-tag>
                        <span class="muted">{{ t("scaffold.packageFunctionCount", { count: item.functions.length }) }}</span>
                    </div>
                    <el-button
                        :class="{ 'operation-lock': operationLocked || busy }"
                        :disabled="locked"
                        :aria-label="t('scaffold.removeSelectedPackage', { name: item.source === 'local' ? item.functions[0]! : item.packageId })"
                        @click="model = model.filter((other) => other !== item)"
                        >{{ t("scaffold.remove") }}</el-button
                    >
                </header>
                <UdfFunctionList :functions="item.functions" :start="selectionStarts[index]" />
            </ScaffoldField>
            <p v-if="!cache.valid.value" class="udf-stale" role="status">{{ t("scaffold.staleSelection") }}</p>
            <el-button v-if="!cache.valid.value" :disabled="locked" @click="cache.prepare">{{ t("scaffold.revalidate") }}</el-button>
            <div class="scaffold-toolbar udf-selected-actions">
                <slot name="actions" :can-submit="cache.valid.value"
                    ><p class="muted">{{ t("scaffold.createUdfHint") }}</p></slot
                >
            </div>
            <p class="muted">{{ t("scaffold.compatibilityHint") }}</p>
        </section>
    </div>
    <UdfRevalidationDialog :actions="cache" />
    <UdfPackageDetailDialog v-model="state.detailOpen.value" :details="state.detail.value" :busy="busy" />
    <UdfTagDialog :state="state" :catalog="catalog" />
    <UdfSourceManager :state="state" :actions="sourceActions" />
    <UdfLocalManager :state="state" :actions="local" :source-label="sourceLabel" />
    <UdfBulkImportDialog :state="state" :actions="bulk" :cancel="requests.cancel" :source-label="sourceLabel(source)" />
</template>
