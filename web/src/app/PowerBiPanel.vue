<!--
 * FilePath    : pbi-utils\web\src\app\PowerBiPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 复用 Desktop 操作栏与单行模型卡片, 展示实例身份、模型摘要及连接状态.
-->

<!-- 页面只显示共享服务结果, 不自行发现或连接端口. -->
<script setup lang="ts">
import type { ProjectSnapshot } from "@/bridge/client"
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import UiIcon from "@/ui/UiIcon.vue"
import ModelInstanceCard from "./ModelInstanceCard.vue"
import { useDesktopInstances, type DesktopInstancesProps } from "@/composables/app/useDesktopInstances"
const props = defineProps<DesktopInstancesProps>()
const emit = defineEmits<{ busy: [value: boolean]; connected: [value: ProjectSnapshot]; openFile: []; viewModel: [] }>()
const {
    snapshot,
    snapshotFresh,
    snapshotStale,
    connectableCount,
    hasReadableModel,
    instanceStatusLabel,
    instanceReason,
    showInstanceReason,
    loading,
    preserveAppearance,
    operation,
    error,
    refresh,
    isCurrentInstance,
    activateInstance,
    locale,
    t,
    projectSummary,
    scannedSummary,
} = useDesktopInstances(props, emit)
useMessageNotices("desktop-instances", () => [!!error.value && { key: "error", message: error.value, type: "error" }])
</script>

<template>
    <section class="powerbi-panel" :aria-busy="loading || fileOpening" :aria-label="t('powerbi.aria')">
        <div class="desktop-instance-actions">
            <el-button
                class="desktop-file-button"
                :class="{ 'operation-lock': preserveAppearance }"
                type="primary"
                :loading="fileOpening"
                :disabled="loading || disabled"
                @click="emit('openFile')"
            >
                <UiIcon name="power-bi" />{{ t("connection.selectDesktopFile") }}
            </el-button>
            <el-button
                class="instance-refresh-button"
                :class="{ 'operation-lock': preserveAppearance }"
                :loading="loading && operation === 'scan'"
                :disabled="loading || disabled"
                @click="refresh()"
                >{{ t("powerbi.refresh") }}</el-button
            >
        </div>
        <p v-if="snapshot && snapshotStale" class="hint" role="status">{{ t("powerbi.staleSnapshot") }}</p>
        <template v-if="snapshot"
            ><div class="scan-summary">
                <span>{{ t("powerbi.instanceSummary", { count: snapshot.instances.length, available: connectableCount }) }}</span
                ><time :datetime="snapshot.timestamp">{{ t("powerbi.updated", { time: new Date(snapshot.timestamp).toLocaleTimeString(locale) }) }}</time>
            </div>
            <UiPanel v-if="!snapshot.instances.length" class="empty-state"
                ><el-empty :description="t('powerbi.empty')" :image-size="64"
                    ><p>{{ t("powerbi.emptyHint") }}</p></el-empty
                ></UiPanel
            >
            <template v-for="instance in snapshot.instances" :key="instance.instanceId || instance.processId + ':' + instance.port">
                <ModelInstanceCard
                    :title="instance.title"
                    :metadata="`${t('powerbi.process')} ${instance.processId}${instance.port ? ` · localhost:${instance.port}` : ''}`"
                    :summary="
                        isCurrentInstance(instance) && activeProject
                            ? projectSummary(activeProject)
                            : instance.models.length === 1 && instance.models[0]
                              ? scannedSummary(instance.models[0])
                              : null
                    "
                    :current="isCurrentInstance(instance)"
                    :operation-locked="preserveAppearance && !snapshotStale && (isCurrentInstance(instance) || hasReadableModel(instance))"
                    :status="instance.status"
                    :status-label="instanceStatusLabel(instance)"
                    :has-reason="!!instanceReason(instance)"
                    :reason-disabled="loading || disabled || !snapshotFresh"
                    :disabled="loading || disabled || !snapshotFresh || (!isCurrentInstance(instance) && !hasReadableModel(instance))"
                    :action-label="t(isCurrentInstance(instance) ? 'app.viewModel' : activeProject ? 'powerbi.switch' : 'powerbi.connect')"
                    @action="activateInstance(instance)"
                    @reason="showInstanceReason(instance)"
                />
            </template>
        </template>
    </section>
</template>
