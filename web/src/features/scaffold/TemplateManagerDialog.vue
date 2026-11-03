<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\TemplateManagerDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 固定模板目录说明与已登记模板管理展示, 不持有路径能力.
-->
<script setup lang="ts">
import UiDialog from "@/ui/UiDialog.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { PbipTemplateInfo } from "./contracts"
/** 展示宿主目录和模板登记, 打开或刷新由父级受控业务链执行. */
const templateManager = defineModel<boolean>({ required: true })
defineProps<{
    formLocked: boolean
    templateDirectory: string
    customTemplates: PbipTemplateInfo[]
    visibleError: string
    templateCounts: { available: number; invalid: number }
    warningText: (value: string) => string
}>()
const emit = defineEmits<{ open: []; refresh: [] }>()
const { t } = useI18n()
</script>
<template>
    <UiDialog v-model="templateManager" :title="t('configuration.manageTemplates')" :busy="formLocked">
        <div class="scaffold-toolbar">
            <span class="scaffold-path" :title="templateDirectory">{{ templateDirectory || "-" }}</span>
            <el-button :disabled="formLocked" @click="emit('open')">{{ t("scaffold.openTemplates") }}</el-button
            ><el-button :disabled="formLocked" @click="emit('refresh')">{{ t("scaffold.refresh") }}</el-button>
        </div>
        <p>{{ t("configuration.templateStructure") }}</p>
        <pre>{{ t("configuration.templateExample") }}</pre>
        <p>{{ t("configuration.templateBinding") }}</p>
        <ul class="scaffold-template-list">
            <li v-for="item in customTemplates" :key="item.id">
                <strong>{{ item.name }}</strong> · {{ t(item.baseline ? "configuration.available" : "configuration.invalid") }}
                <p v-for="warning in item.warnings" :key="warning">{{ warningText(warning) }}</p>
            </li>
        </ul>
        <el-empty v-if="!customTemplates.length" :description="t('configuration.templateCounts', templateCounts)" :image-size="48" />
        <template #footer
            ><el-button :disabled="formLocked" @click="templateManager = false">{{ t("configuration.close") }}</el-button></template
        >
    </UiDialog>
</template>
