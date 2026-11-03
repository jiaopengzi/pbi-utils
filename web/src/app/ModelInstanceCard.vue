<!--
 * FilePath    : pbi-utils\web\src\app\ModelInstanceCard.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 三种连接来源共用单行模型卡片, 展示真实摘要、未知项、当前状态与行首操作.
-->
<!-- 卡片只展示宿主摘要并发出操作事件; 身份判断、连接与导航由来源面板负责. -->
<script setup lang="ts">
import { computed } from "vue"
import type { ModelSummary } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import UiPanel from "@/ui/UiPanel.vue"

const props = defineProps<{
    title: string
    metadata?: string
    summary?: ModelSummary | null
    current?: boolean
    disabled?: boolean
    operationLocked?: boolean
    actionLabel: string
    status?: "connected" | "starting" | "unavailable"
    statusLabel?: string
    hasReason?: boolean
    reasonDisabled?: boolean
}>()
const emit = defineEmits<{ action: []; reason: [] }>()
const { t } = useI18n()
/** 随当前语言重算字段标签; 未提供页数按用户约定显示零, 其他未知项和实际零值分别保留. */
const statistics = computed(() => [
    { key: "compatibilityLevel", label: t("powerbi.compatibility"), value: props.summary?.compatibilityLevel },
    { key: "tableCount", label: t("powerbi.tables"), value: props.summary?.tableCount },
    { key: "measureCount", label: t("powerbi.measures"), value: props.summary?.measureCount },
    { key: "reportPageCount", label: t("app.pages"), value: props.summary?.reportPageCount },
])
/** 原因入口独立于连接禁用; 仅转发明确用户意图, 业务在途或旧快照禁止发出原因事件. */
function requestReason() {
    if (props.hasReason && !props.reasonDisabled) emit("reason")
}
</script>

<template>
    <UiPanel class="instance-card" :class="{ 'is-current': current }">
        <div class="instance-heading">
            <el-button
                class="instance-connect-button"
                :class="{ 'operation-lock': operationLocked }"
                :type="current ? 'primary' : 'default'"
                :disabled="disabled"
                @click="emit('action')"
                >{{ actionLabel }}</el-button
            >
            <div class="instance-details">
                <h2 :title="title">{{ title }}</h2>
                <span v-if="metadata" class="instance-metadata hint" :title="metadata">{{ metadata }}</span>
            </div>
            <dl class="instance-summary">
                <div v-for="stat in statistics" :key="stat.key" class="instance-stat" :data-stat="stat.key">
                    <dt>{{ stat.label }}</dt>
                    <dd>{{ stat.value ?? (stat.key === "reportPageCount" ? 0 : t("powerbi.unknown")) }}</dd>
                </div>
            </dl>
            <div class="instance-indicators">
                <el-tag class="instance-connection" :type="current ? 'success' : 'info'">{{
                    t(current ? "powerbi.currentConnection" : "powerbi.notConnected")
                }}</el-tag>
                <el-tag v-if="statusLabel || (status && status !== 'connected')" class="instance-status" :type="status === 'starting' ? 'warning' : 'danger'">{{
                    statusLabel || t("powerbi." + status)
                }}</el-tag>
                <el-button
                    v-if="hasReason"
                    class="instance-reason-button"
                    link
                    type="primary"
                    :disabled="reasonDisabled"
                    :aria-label="t('powerbi.reasonFor', { title, metadata: metadata || '' })"
                    @click="requestReason"
                    >{{ t("powerbi.viewReason") }}</el-button
                >
            </div>
        </div>

        <slot />
    </UiPanel>
</template>
