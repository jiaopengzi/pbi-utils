<!--
 * FilePath    : pbi-utils\web\src\app\ModelUdfLint.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : UDF 未使用函数只读分析及完整删除集合的显式预览确认.
-->
<script setup lang="ts">
import type { ProjectSnapshot } from "@/bridge/client"
import { useUdfLint, type UdfLintProps } from "@/composables/app/useUdfLint"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import UiDialog from "@/ui/UiDialog.vue"
const props = defineProps<UdfLintProps>()
const emit = defineEmits<{ busy: [value: boolean]; updated: [value: ProjectSnapshot] }>()
const { t, result, busy, dialog, error, canAnalyze, canDelete, temporarilyLocked, withinBudget, analyze, remove } = useUdfLint(props, emit)
useMessageNotices("model-udf-lint", () => [
    !!error.value && { key: "error", message: error.value, type: "error" },
    !!result.value && !result.value.complete && { key: "incomplete", message: t("model.udfLintUnknown") },
    !!result.value?.complete && !result.value.unused.length && { key: "none-unused", message: t("model.udfLintNone"), type: "info" },
    dialog.value && { key: "confirm", message: t("model.udfLintConfirm") },
    dialog.value && !withinBudget.value && { key: "limit", message: t("model.udfLintLimit") },
])
</script>
<template>
    <div class="actions">
        <el-button :disabled="!canAnalyze" :loading="busy" :class="{ 'operation-lock': temporarilyLocked }" @click="analyze">{{
            t("model.udfLint")
        }}</el-button>
    </div>
    <p>{{ t("model.udfLintNotice") }}</p>

    <UiDialog v-model="dialog" :title="t('model.udfLintTitle')" :busy="busy">
        <p>{{ t("model.udfLintSummary", { count: result?.unused.length ?? 0, total: result?.totalCount ?? 0 }) }}</p>
        <ul>
            <li v-for="name in result?.unused ?? []" :key="name">{{ name }}</li>
        </ul>

        <template #footer>
            <el-button :disabled="busy" @click="dialog = false">{{ t("model.cancel") }}</el-button>
            <el-button type="danger" :disabled="!canDelete" :loading="busy" @click="remove">{{ t("model.udfLintDelete") }}</el-button>
        </template>
    </UiDialog>
</template>
