<!--
 * FilePath    : pbi-utils\web\src\app\App.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 组装独立业务工作区、设置与学习入口, 通过共享桥接读取模型和报表.
-->
<!-- 导航只管理显示状态; 业务读取由共享 C# 服务完成. -->
<script setup lang="ts">
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
// 显式调用的确认框需要单独引入品牌 SCSS, 模板按需导入不会补齐其样式.
import "element-plus/es/components/message-box/style/index"
import UiIcon from "@/ui/UiIcon.vue"
import SearchDialog from "./SearchDialog.vue"
import HostDialog from "./HostDialog.vue"
import ModelPanel from "./ModelPanel.vue"
import ModelEditPanel from "./ModelEditPanel.vue"
import SettingsWorkspace from "./SettingsWorkspace.vue"
import NewPanel from "./NewPanel.vue"
import SamplePanel from "./SamplePanel.vue"
import LearningPanel from "./LearningPanel.vue"
import ConnectionPanel from "./ConnectionPanel.vue"
import RecentConnectionsPanel from "./RecentConnectionsPanel.vue"
import { useApplicationWorkspace } from "@/composables/app/useApplicationWorkspace"
import { useProductUpdates } from "@/composables/app/useProductUpdates"
const {
    modules,
    page,
    collapsed,
    targetTab,
    navigationId,
    operationBusy,
    modelPanelBusy,
    modelEditorBusy,
    modelEditorIdentity,
    modelEditorCreated,
    modelEditorActive,
    modelEditorLocked,
    acceptModelEditorState,
    recentConfirming,
    hostDialogActive,
    projectInspecting,
    requestBusy,
    busy,
    initializing,
    project,
    modelEditSession,
    acceptModelEditSession,
    desktopRefreshKey,
    recentLocalItems,
    bridge,
    error,
    saveMessage,
    pendingChanges,
    needsVerification,
    connectedDesktop,
    writable,
    preview,
    base,
    title,
    projectName,
    connectionLabel,
    openProject,
    updateRecentVisibility,
    deleteRecentProject,
    acceptEdit,
    finishEdits,
    navigate,
    disconnect,
    acceptConnection,
    showRecentFiles,
    t,
    elementLocale,
} = useApplicationWorkspace()
const productUpdates = useProductUpdates(
    () => bridge.value,
    () => !initializing.value,
)
useMessageNotices("application-workspace", () => [
    !!error.value && { key: "error", message: error.value, type: "error" },
    !!saveMessage.value && { key: "save", message: saveMessage.value },
    connectedDesktop.value &&
        needsVerification.value &&
        !project.value?.saveResult?.errorCode && {
            key: "verification",
            message: t("configuration.verification"),
        },
])
</script>
<template>
    <el-config-provider :locale="elementLocale">
        <HostDialog :bridge="bridge" @active="hostDialogActive = $event" />
        <a class="skip-link" href="#workspace-main">{{ t("app.skip") }}</a>
        <div :class="['shell', { collapsed }]">
            <aside class="sidebar">
                <a class="brand" href="#" :aria-label="t('app.brandConnections')" @click.prevent="navigate('connections')"
                    ><img class="brand-mark" :src="`${base}branding/logo.svg`" alt="pbi-utils" /><span>pbi-utils</span></a
                >
                <SearchDialog :disabled="busy || initializing" @select="navigate" />
                <nav :aria-label="t('app.navigation')">
                    <el-tooltip v-for="item in modules" :key="item[0]" :content="item[1]" placement="right" :disabled="!collapsed">
                        <el-button
                            :class="{ active: page === item[0] }"
                            :aria-label="item[1]"
                            :aria-current="page === item[0] ? 'page' : undefined"
                            :title="item[1]"
                            :disabled="busy || initializing"
                            @click="navigate(item[0]!)"
                            ><UiIcon :name="item[2]!" /><span class="nav-label">{{ item[1] }}</span></el-button
                        >
                    </el-tooltip>
                </nav>
                <div class="sidebar-footer">
                    <p v-if="preview">{{ t("app.preview") }}</p>
                    <el-button
                        class="collapse-button"
                        :aria-expanded="!collapsed"
                        :aria-label="collapsed ? t('app.expand') : t('app.collapse')"
                        @click="collapsed = !collapsed"
                        ><UiIcon name="panel-left-close" /><span class="nav-label">{{ collapsed ? t("app.expand") : t("app.collapse") }}</span></el-button
                    >
                </div>
            </aside>
            <main id="workspace-main" tabindex="-1">
                <header class="workspace-header">
                    <el-breadcrumb separator="/"
                        ><el-breadcrumb-item>{{ t("app.workspace") }}</el-breadcrumb-item
                        ><el-breadcrumb-item>{{ title }}</el-breadcrumb-item></el-breadcrumb
                    >
                    <div class="header-actions">
                        <span class="project-name" :title="project?.projectPath">{{ projectName }}</span>
                        <el-tag :type="project ? 'primary' : 'info'">{{ connectionLabel }}</el-tag>
                        <el-tag v-if="pendingChanges" type="warning">{{
                            needsVerification ? t("app.verify") : project?.saveResult?.errorCode ? t("app.savePending") : t("app.unsaved")
                        }}</el-tag>
                        <el-button
                            v-if="!connectedDesktop && writable && pendingChanges"
                            type="primary"
                            :class="{ 'operation-lock': busy && !needsVerification }"
                            :disabled="busy || needsVerification"
                            @click="finishEdits()"
                            >{{ needsVerification ? t("app.saveVerify") : t("app.retrySave") }}</el-button
                        >
                        <el-button
                            v-if="!connectedDesktop && pendingChanges"
                            :class="{ 'operation-lock': busy && !needsVerification }"
                            :disabled="busy || needsVerification"
                            @click="finishEdits(true)"
                            >{{ t("app.discard") }}</el-button
                        >
                        <el-button v-if="connectedDesktop && pendingChanges" :disabled="busy" @click="finishEdits(true)">{{ t("app.reloadModel") }}</el-button>
                        <el-button
                            v-if="project"
                            :class="{ 'operation-lock': busy }"
                            :loading="projectInspecting"
                            :disabled="busy"
                            @click="openProject(true)"
                            >{{ t("app.refreshModel") }}</el-button
                        >
                        <el-button
                            v-if="project"
                            :class="{ 'operation-lock': busy && !pendingChanges }"
                            :disabled="busy || pendingChanges"
                            @click="disconnect"
                            >{{ t("app.disconnect") }}</el-button
                        >
                    </div>
                </header>
                <section class="content" :class="{ 'connections-content': page === 'connections' }" :aria-busy="busy || initializing">
                    <template v-if="page === 'connections'">
                        <ConnectionPanel
                            v-if="bridge"
                            :bridge="bridge"
                            :active-project="project"
                            :desktop-refresh-key="desktopRefreshKey"
                            :disabled="busy || pendingChanges"
                            :operation-locked="busy && !pendingChanges"
                            @busy="operationBusy = $event"
                            @connected="acceptConnection"
                            @disconnected="project = undefined"
                            @view-model="navigate('model')"
                        >
                            <template #recent="{ source, serviceSignedIn, serviceRecents, openServiceRecent, deleteServiceRecent }">
                                <RecentConnectionsPanel
                                    v-if="showRecentFiles && (source === 'desktop' || (source === 'service' && serviceSignedIn))"
                                    :items="
                                        source === 'desktop'
                                            ? recentLocalItems
                                            : serviceRecents.map((item: { id: string; name: string; workspace: string }) => ({
                                                  id: item.id,
                                                  name: item.name,
                                                  detail: item.workspace,
                                              }))
                                    "
                                    :disabled="requestBusy || pendingChanges"
                                    :empty-description="source === 'service' ? t('connection.recentEmptyCloud') : undefined"
                                    :visibility-disabled="busy || !bridge || initializing"
                                    @hide="updateRecentVisibility(false)"
                                    @open="source === 'desktop' ? openProject(false, $event) : openServiceRecent($event)"
                                    @remove="source === 'desktop' ? deleteRecentProject($event) : deleteServiceRecent($event)"
                                    @confirming="recentConfirming = $event"
                                />
                            </template>
                        </ConnectionPanel>
                    </template>
                    <ModelPanel
                        v-else-if="page === 'model' && project && bridge"
                        :key="`model:${project.projectId}`"
                        :initial-tab="targetTab"
                        :project="project"
                        :bridge="bridge"
                        external-editor
                        :editor-busy="modelEditorBusy"
                        @busy="modelPanelBusy = $event"
                        @editor-state="acceptModelEditorState"
                        @updated="acceptEdit"
                    />
                    <SettingsWorkspace
                        v-else-if="page === 'system' && bridge"
                        :bridge="bridge"
                        :updates="productUpdates"
                        :initial-tab="targetTab"
                        :navigation-id="navigationId"
                        @busy="operationBusy = $event"
                    />
                    <NewPanel
                        v-else-if="page === 'new' && bridge"
                        :bridge="bridge"
                        :can-connect="!pendingChanges"
                        @busy="operationBusy = $event"
                        @connected="acceptConnection"
                    />
                    <SamplePanel v-else-if="page === 'sample' && bridge" :bridge="bridge" @busy="operationBusy = $event" />
                    <LearningPanel v-else-if="page === 'learning' && bridge" :bridge="bridge" :disabled="busy" @busy="operationBusy = $event" />
                    <UiPanel v-else class="empty"
                        ><el-empty :description="t('app.connectFirst')" :image-size="88"
                            ><p>{{ t("app.accessBoundary") }}</p>
                            <el-button type="primary" :disabled="busy || !bridge" @click="navigate('connections')">{{
                                t("app.connections")
                            }}</el-button></el-empty
                        ></UiPanel
                    >
                    <ModelEditPanel
                        v-if="modelEditorCreated && project && bridge"
                        v-show="modelEditorActive"
                        :key="modelEditorIdentity"
                        initial-operation="table.set"
                        :project="project"
                        :bridge="bridge"
                        :active="modelEditorActive"
                        :locked="modelEditorLocked"
                        :edit-session="modelEditSession"
                        @busy="modelEditorBusy = $event"
                        @updated="acceptEdit"
                        @draft-change="acceptModelEditSession"
                    />
                </section>
            </main>
        </div>
    </el-config-provider>
</template>
