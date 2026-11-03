<!--
 * FilePath    : pbi-utils\web\src\app\ConnectionPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 连接页平铺三种模型来源, 复用类型化桥接与实例操作并保留连接取消边界.
-->
<script setup lang="ts">
import type { ProjectSnapshot } from "@/bridge/client"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import UiTabs from "@/ui/UiTabs.vue"
import PowerBiPanel from "./PowerBiPanel.vue"
import ModelInstanceCard from "./ModelInstanceCard.vue"
import RemoteConnectionPanel from "./RemoteConnectionPanel.vue"
import ServiceLoginForm from "./ServiceLoginForm.vue"
import { useConnectionWorkspace, type ConnectionWorkspaceProps } from "@/composables/app/useConnectionWorkspace"
const props = defineProps<ConnectionWorkspaceProps>()
const emit = defineEmits<{ connected: [value: ProjectSnapshot]; disconnected: []; busy: [value: boolean]; viewModel: [] }>()
const {
    source,
    loading,
    blocked,
    preserveAppearance,
    pendingMethod,
    desktopConnectionRevision,
    error,
    status,
    loginForm,
    login,
    environments,
    recentLoginId,
    serviceRecents,
    recentStorageWarning,
    recentStorageReady,
    loginRules,
    models,
    workspace,
    modelId,
    workspaces,
    visibleModels,
    selectedModel,
    currentService,
    currentServer,
    selectedIsCurrent,
    modelLabel,
    setInstanceBusy,
    setRemoteBusy,
    run,
    accept,
    loadModels,
    openServiceRecent,
    deleteServiceRecent,
    viewModel,
    activateServiceModel,
    signIn,
    selectRecentLogin,
    deleteRecentLogin,
    signOut,
    connectSelected,
    t,
    hostText,
    projectSummary,
} = useConnectionWorkspace(props, emit)
useMessageNotices("connection-panel-errors", () => [!!error.value && { key: "error", message: error.value, type: "error" }])
// Service 选中模型的既有反馈由页面持有, 共享卡片不再因挂载或状态变化产生提示.
useMessageNotices(
    "connection-panel-selected-model",
    () => [!!selectedModel.value?.unavailableReason && { key: selectedModel.value.id, message: selectedModel.value.unavailableReason }],
    { ready: () => source.value === "service" && !loading.value && recentStorageReady.value && !!selectedModel.value },
)
useMessageNotices("connection-panel-info", () => [
    source.value === "service" && !!status.value?.isSimulation && { key: "simulation", message: t("connection.simulationHint"), type: "info", once: true },
])
useMessageNotices("connection-panel-service-state", () => [!!status.value?.storageWarning && { key: "storage", message: status.value.storageWarning }], {
    ready: () => source.value === "service" && !!status.value,
})
// 历史警告在读取目录前会临时清空; 只在完整成功发布后更新基线, 失败/取消不视为真实清除.
useMessageNotices("connection-panel-service-recents", () => [!!recentStorageWarning.value && { key: "recent-storage", message: recentStorageWarning.value }], {
    ready: () => source.value === "service" && recentStorageReady.value,
})
</script>
<template>
    <section class="connection-center" :aria-busy="loading" :aria-label="t('connection.title')">
        <UiTabs
            class="connection-source-tabs"
            v-model="source"
            :aria-label="t('connection.source')"
            :disabled="blocked"
            :items="[
                ['desktop', 'Power BI Desktop'],
                ['service', 'Power BI Service'],
                ['server', 'SSAS / XMLA'],
            ]"
        />
        <KeepAlive>
            <PowerBiPanel
                v-if="source === 'desktop'"
                :bridge="bridge"
                :active-project="activeProject"
                :disabled="blocked"
                :operation-locked="preserveAppearance"
                :file-opening="pendingMethod === 'project.open'"
                :refresh-key="`${desktopRefreshKey ?? 0}:${desktopConnectionRevision}`"
                @busy="setInstanceBusy"
                @connected="accept"
                @open-file="connectSelected"
                @view-model="viewModel"
            />
        </KeepAlive>
        <template v-if="source === 'server'">
            <RemoteConnectionPanel :bridge="bridge" :disabled="blocked" :operation-locked="preserveAppearance" @busy="setRemoteBusy" @connected="accept" />
            <ModelInstanceCard
                v-if="currentServer"
                class="current-model-card"
                :title="currentServer.model.name"
                :metadata="currentServer.connection?.label"
                :summary="projectSummary(currentServer)"
                current
                :disabled="blocked"
                :operation-locked="preserveAppearance"
                :action-label="t('app.viewModel')"
                @action="viewModel"
            />
        </template>
        <template v-else-if="source === 'service'">
            <div v-if="status?.account" class="connection-account">
                <template v-if="status?.account">
                    <span class="account-name">{{ status.account.username }} · {{ hostText(status.account.environmentLabel) }}</span>
                    <el-button :class="{ 'operation-lock': preserveAppearance }" :disabled="blocked" @click="run(loadModels)">{{
                        t("connection.refreshModels")
                    }}</el-button>
                    <el-button :class="{ 'operation-lock': preserveAppearance }" :disabled="blocked" @click="signOut">{{ t("connection.signOut") }}</el-button>
                </template>
            </div>
            <ServiceLoginForm
                v-if="!status?.account"
                v-model:login="login"
                v-model:recent-login-id="recentLoginId"
                :status="status"
                :login-rules="loginRules"
                :environments="environments"
                :blocked="blocked"
                :preserve-appearance="preserveAppearance"
                :loading="loading"
                :pending-method="pendingMethod"
                @form="loginForm = $event"
                @sign-in="signIn"
                @select-recent="selectRecentLogin"
                @delete-recent="deleteRecentLogin"
            />
            <template v-if="status?.account">
                <el-form label-position="top" @submit.prevent>
                    <el-form-item :label="t('connection.workspace')"
                        ><el-select
                            v-model="workspace"
                            :class="{ 'operation-lock': preserveAppearance }"
                            filterable
                            :disabled="blocked"
                            :placeholder="t('connection.selectWorkspace')"
                            :aria-label="t('connection.workspace')"
                        >
                            <el-option
                                v-for="item in workspaces"
                                :key="item.id"
                                :label="`${item.name}${workspaces.filter((other) => other.name === item.name).length > 1 ? ` · ${item.id}` : ''}`"
                                :value="item.id"
                            /> </el-select
                    ></el-form-item>
                    <el-form-item :label="t('connection.semanticModel')"
                        ><el-select
                            v-model="modelId"
                            :class="{ 'operation-lock': preserveAppearance && !!workspace }"
                            filterable
                            :disabled="blocked || !workspace"
                            :placeholder="t('connection.selectModel')"
                            :aria-label="t('connection.semanticModel')"
                        >
                            <el-option
                                v-for="model in visibleModels"
                                :key="model.id"
                                :label="modelLabel(model)"
                                :value="model.id"
                                :disabled="!!model.unavailableReason"
                            /> </el-select
                    ></el-form-item>
                </el-form>
                <el-empty v-if="!loading && !models.length" :description="t('connection.noModels')" :image-size="48" />
                <p v-for="model in visibleModels.filter((item) => item.unavailableReason)" :key="model.id" class="hint">
                    {{ model.name }}: {{ hostText(model.unavailableReason) }}
                </p>
            </template>
            <ModelInstanceCard
                v-if="selectedModel"
                :class="{ 'current-model-card': selectedIsCurrent }"
                :title="selectedModel.name"
                :metadata="selectedModel.workspace"
                :summary="selectedIsCurrent && currentService ? projectSummary(currentService) : null"
                :current="selectedIsCurrent"
                :disabled="blocked || !!selectedModel.unavailableReason"
                :operation-locked="preserveAppearance && !selectedModel.unavailableReason"
                :action-label="t(selectedIsCurrent ? 'app.viewModel' : activeProject ? 'connection.switchSelected' : 'connection.connectSelected')"
                @action="activateServiceModel"
            />
            <ModelInstanceCard
                v-if="currentService && !selectedIsCurrent"
                class="current-model-card"
                :title="currentService.model.name"
                :metadata="currentService.connection?.label"
                :summary="projectSummary(currentService)"
                current
                :disabled="blocked"
                :operation-locked="preserveAppearance"
                :action-label="t('app.viewModel')"
                @action="viewModel"
            />
            <p class="hint">{{ t("connection.serviceReadOnly") }}</p>
        </template>
    </section>
    <slot
        name="recent"
        :source="source"
        :service-signed-in="!!status?.account"
        :service-recents="serviceRecents"
        :open-service-recent="openServiceRecent"
        :delete-service-recent="deleteServiceRecent"
    />
</template>
<style lang="scss" scoped>
.connection-center {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    gap: 16px;
    margin-bottom: 24px;
    .tabs,
    .instance-card {
        margin-bottom: 0;
    }
    h3,
    p {
        margin: 0;
    }
    p {
        line-height: 1.7;
    }
    .el-select {
        width: 100%;
    }
}
.connection-account {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
}
.account-name {
    overflow-wrap: anywhere;
    flex: 1;
    min-width: 0;
}
</style>
