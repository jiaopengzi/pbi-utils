<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfCatalogBrowser.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 包级 UDF 目录卡片分页, 精确版本与完整详情按需读取, 隐藏选择保持.
-->
<script setup lang="ts">
import { computed } from "vue"
import UdfLibraryCard from "./UdfLibraryCard.vue"
import UdfLocalBrowser from "./UdfLocalBrowser.vue"
import UdfFunctionPageActions from "./UdfFunctionPageActions.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
import type { UdfCatalog } from "@/composables/features/scaffold/useUdfCatalog"
import type { LocalUdfLibrary } from "@/composables/features/scaffold/useLocalUdfLibrary"
import { matchUdfName } from "./udfSearch"
/** 展示宿主返回的包摘要; 查询与分页以包为单位, 不预载全部函数正文. */
const props = defineProps<{ state: UdfState; catalog: UdfCatalog; local: LocalUdfLibrary; operationLocked?: boolean }>()
const emit = defineEmits<{ selectSource: [] }>()
const {
    packageItems,
    model,
    source,
    query,
    searchQuery,
    searching,
    locals,
    prerelease,
    busy,
    locked,
    catalogStatus,
    currentSource,
    page,
    pageSize,
    total,
    packageTags,
    icons,
    uncertain,
} = props.state
const { t } = useI18n()
const hasLocalMatches = computed(() => locals.value.some((item) => matchUdfName(item.definition.name, searchQuery.value) !== null))
</script>
<template>
    <div class="scaffold-toolbar udf-search">
        <el-input
            v-model="query"
            :class="{ 'operation-lock': operationLocked || busy }"
            :disabled="(locked && !searching) || uncertain || (!catalogStatus?.initialized && !locals.length)"
            :aria-label="t('scaffold.packageSearch')"
            :placeholder="t('scaffold.packageSearch')"
            clearable
            @keydown.enter.prevent="catalog.search"
            @compositionstart="catalog.compositionStart"
            @compositionend="catalog.compositionEnd"
        />
        <el-checkbox
            :model-value="prerelease"
            :disabled="locked || uncertain"
            :class="{ 'operation-lock': operationLocked || busy }"
            @update:model-value="prerelease = $event === true"
            @change="catalog.search"
            >{{ t("scaffold.prerelease") }}</el-checkbox
        >
        <el-button
            :class="{ 'operation-lock': operationLocked || busy }"
            :disabled="locked || uncertain || !currentSource || (catalogStatus?.initialized === false && !locals.length)"
            @click="catalog.search"
            >{{ t("scaffold.search") }}</el-button
        >
    </div>
    <section class="udf-browser">
        <p v-if="catalogStatus && !catalogStatus.initialized" class="udf-empty">{{ t("scaffold.catalogFirstUse") }}</p>
        <header class="udf-list-heading">
            <h4>{{ currentSource?.name ?? "-" }}</h4>
            <el-button
                :disabled="locked || uncertain || !currentSource || !catalogStatus?.initialized"
                :class="{ 'operation-lock': operationLocked || busy }"
                @click="!locked && !uncertain && currentSource && catalogStatus?.initialized && emit('selectSource')"
                >{{ t("scaffold.bulkImport") }}</el-button
            >
        </header>
        <div class="udf-library-grid">
            <UdfLibraryCard
                v-for="item in packageItems"
                :key="item.id"
                :pkg="item"
                :query="searchQuery"
                :icon="item.iconKey ? icons[item.iconKey] : undefined"
                :checked="
                    model.some(
                        (selection) =>
                            selection.source === source &&
                            selection.packageId === item.id &&
                            selection.version === item.version &&
                            selection.baseline === item.hash &&
                            selection.functions.length === item.functionCount,
                    )
                "
                :candidate="item.version.split('+')[0]!.includes('-') && !packageTags[item.id]"
                :disabled="locked || uncertain"
                :operation-locked="operationLocked || busy"
                @select="catalog.selectPackage(item, $event)"
                @details="catalog.openPackage(item)"
                @versions="catalog.loadTags(item.id)"
            />
        </div>
        <UdfFunctionPageActions
            v-if="catalogStatus?.initialized"
            :page="page"
            :page-size="pageSize"
            :total="total"
            :has-items="packageItems.length > 0"
            package-mode
            :disabled="locked || uncertain"
            :operation-locked="operationLocked || busy"
            @select-page="catalog.selectPage"
            @change-page="catalog.changePage"
            @change-size="catalog.changePageSize"
        />
        <section v-if="locals.length" class="udf-local-browser" :aria-label="t('scaffold.local')">
            <h4>{{ t("scaffold.local") }}</h4>
            <UdfLocalBrowser
                v-model:details-open="state.localDetailOpen.value"
                :items="locals"
                :selections="model"
                :active-versions="state.localPackageTags.value"
                :query="searchQuery"
                :disabled="locked || uncertain"
                :busy="busy"
                :operation-locked="operationLocked || busy"
                embedded
                @select="local.toggleLocal"
                @change-version="local.changeVersion"
                @select-page="local.selectPage"
                @edit="local.edit"
            />
        </section>
        <p v-if="catalogStatus?.initialized && !packageItems.length && !hasLocalMatches" class="udf-empty" role="status">{{ t("scaffold.noPackages") }}</p>
    </section>
</template>
