<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfTagDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 全列表共用一个按需版本选择器, 同库统一 tag 并自动核验精确依赖.
-->
<script setup lang="ts">
import { computed } from "vue"
import UiDialog from "@/ui/UiDialog.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import { latestStableUdfVersion, sortUdfVersions } from "./udfVersions"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
import type { UdfCatalog } from "@/composables/features/scaffold/useUdfCatalog"
/** 草稿只在明确确认且精确依赖核验成功后变更; 取消不切tag. */
const props = defineProps<{ state: UdfState; catalog: UdfCatalog }>()
const { tagPickerOpen, tagPickerId, tagPickerValue, tagOptions, busy } = props.state
const { t } = useI18n()
const options = computed(() => sortUdfVersions(tagOptions.value[tagPickerId.value] ?? []))
const stable = computed(() => latestStableUdfVersion(options.value))
const valid = computed(() => (tagPickerValue.value === "latest" ? !!stable.value : options.value.includes(tagPickerValue.value)))
</script>
<template>
    <UiDialog v-model="tagPickerOpen" :title="t('scaffold.packageTag', { name: tagPickerId })" :busy="busy" class="udf-tag-dialog">
        <p>{{ t("scaffold.tagHint") }}</p>
        <el-select v-model="tagPickerValue" :disabled="busy" :aria-label="t('scaffold.selectedVersion')" :placeholder="t('scaffold.choosePrereleaseTag')">
            <el-option v-if="stable" value="latest" :label="`${t('scaffold.latestTag')} · ${stable}`" />
            <el-option v-for="tag in options" :key="tag" :value="tag" :label="tag" />
        </el-select>
        <template #footer
            ><el-button :disabled="busy" @click="tagPickerOpen = false">{{ t("scaffold.cancel") }}</el-button
            ><el-button type="primary" :disabled="busy || !valid" @click="catalog.changeTag(tagPickerId, tagPickerValue)">{{
                t("scaffold.confirmTag")
            }}</el-button></template
        >
    </UiDialog>
</template>
