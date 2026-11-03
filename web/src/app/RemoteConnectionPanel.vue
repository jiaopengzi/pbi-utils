<!--
 * FilePath    : pbi-utils\web\src\app\RemoteConnectionPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 连接页平铺 SSAS/XMLA 表单, 使用一次性宿主上下文并保留凭据隔离与取消排空.
-->
<script setup lang="ts">
import { useTemplateRef } from "vue"
import type { ProjectSnapshot } from "@/bridge/client"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { useRemoteConnection, type RemoteConnectionProps } from "@/composables/app/useRemoteConnection"
const formRoot = useTemplateRef<HTMLElement>("formRoot")
const props = defineProps<RemoteConnectionProps>()
const emit = defineEmits<{ connected: [value: ProjectSnapshot]; busy: [value: boolean] }>()
const {
    context,
    form,
    loading,
    connecting,
    error,
    recent,
    selectedRecent,
    xmla,
    explicitWindows,
    blocked,
    preserveAppearance,
    validValues,
    canSubmit,
    selectRecent,
    loadForm,
    mutate,
    connect,
    t,
} = useRemoteConnection(props, emit, formRoot)
useMessageNotices("remote-connection-panel", () => [
    !!context.value?.isSimulation && { key: "simulation", once: true, message: t("hostDialog.simulation"), type: "info" },
    !!(error.value || context.value?.error) && { key: "error", message: error.value || context.value?.error || "", type: "error" },
])
</script>
<template>
    <section ref="formRoot" class="remote-connection" :aria-busy="loading">
        <p class="remote-connection__hint">{{ t("hostDialog.remoteMessage") }}</p>

        <el-form label-position="top" @submit.prevent>
            <el-form-item v-if="recent.length" :label="t('hostDialog.recent')">
                <div class="remote-connection__recent">
                    <el-select
                        :model-value="form.recentId"
                        :disabled="blocked"
                        :class="{ 'operation-lock': preserveAppearance }"
                        :aria-label="t('hostDialog.recent')"
                        @change="selectRecent"
                    >
                        <el-option value="" :label="t('hostDialog.newRemote')" />
                        <el-option
                            v-for="item in recent"
                            :key="item.id"
                            :value="item.id"
                            :label="`${item.server} · ${item.database} · ${item.authenticationMode === 'windows' ? item.userName : t('hostDialog.current')}`"
                        />
                    </el-select>
                    <el-button
                        :disabled="blocked || !context?.formId || !selectedRecent"
                        :class="{ 'operation-lock': preserveAppearance && !!selectedRecent }"
                        @click="mutate('delete')"
                        >{{ t("hostDialog.delete") }}</el-button
                    >
                    <el-button
                        :disabled="blocked || !context?.formId || !selectedRecent?.hasSavedPassword"
                        :class="{ 'operation-lock': preserveAppearance && !!selectedRecent?.hasSavedPassword }"
                        @click="mutate('clearPassword')"
                        >{{ t("hostDialog.clearPassword") }}</el-button
                    >
                </div>
            </el-form-item>
            <div class="remote-connection__target">
                <el-form-item :label="t('hostDialog.server')"
                    ><el-input
                        v-model="form.server"
                        :maxlength="1024"
                        :disabled="blocked"
                        :class="{ 'operation-lock': preserveAppearance }"
                        :aria-label="t('hostDialog.server')"
                /></el-form-item>
                <el-form-item :label="t('hostDialog.database')"
                    ><el-input
                        v-model="form.database"
                        :maxlength="200"
                        :disabled="blocked"
                        :class="{ 'operation-lock': preserveAppearance }"
                        :aria-label="t('hostDialog.database')"
                /></el-form-item>
            </div>
            <el-form-item :label="t('hostDialog.authenticationMode')">
                <el-select
                    v-model="form.authenticationMode"
                    :disabled="xmla || blocked"
                    :class="{ 'operation-lock': preserveAppearance && !xmla }"
                    :aria-label="t('hostDialog.authenticationMode')"
                >
                    <el-option value="current" :label="t('hostDialog.current')" /><el-option value="windows" :label="t('hostDialog.windows')" />
                </el-select>
            </el-form-item>
            <div class="remote-connection__target">
                <el-form-item :label="t('hostDialog.userName')"
                    ><el-input
                        v-model="form.userName"
                        :maxlength="256"
                        :disabled="!explicitWindows || blocked"
                        :class="{ 'operation-lock': preserveAppearance && explicitWindows }"
                        :aria-label="t('hostDialog.userName')"
                        :placeholder="t('hostDialog.userPlaceholder')"
                /></el-form-item>
                <el-form-item :label="t('hostDialog.password')"
                    ><el-input
                        v-model="form.password"
                        type="password"
                        :maxlength="1024"
                        autocomplete="new-password"
                        :disabled="!explicitWindows || blocked"
                        :class="{ 'operation-lock': preserveAppearance && explicitWindows }"
                        :aria-label="t('hostDialog.password')"
                        :placeholder="t(selectedRecent?.hasSavedPassword ? 'hostDialog.savedPassword' : 'hostDialog.passwordPlaceholder')"
                /></el-form-item>
            </div>
            <div class="remote-connection__checks">
                <el-checkbox
                    :model-value="form.remember"
                    :disabled="blocked"
                    :class="{ 'operation-lock': preserveAppearance }"
                    @update:model-value="form.remember = $event === true"
                    >{{ t("hostDialog.remember") }}</el-checkbox
                >
                <el-checkbox
                    :model-value="form.rememberPassword"
                    :disabled="!explicitWindows || !form.remember || blocked"
                    :class="{ 'operation-lock': preserveAppearance && explicitWindows && form.remember }"
                    @update:model-value="form.rememberPassword = $event === true"
                    >{{ t("hostDialog.rememberPassword") }}</el-checkbox
                >
            </div>
            <p class="remote-connection__hint">{{ t("hostDialog.passwordHint") }}</p>
            <div class="remote-connection__actions">
                <el-button v-if="!context?.formId && !connecting" :disabled="blocked" @click="loadForm">{{ t("connection.retryRemote") }}</el-button>
                <el-button
                    type="primary"
                    :loading="connecting"
                    :disabled="blocked || !canSubmit"
                    :class="{ 'operation-lock': preserveAppearance && (validValues || connecting) }"
                    @click="connect"
                    >{{ t("hostDialog.connect") }}</el-button
                >
            </div>
        </el-form>
    </section>
</template>
<style scoped lang="scss">
.remote-connection__target {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    gap: 20px;
}
.remote-connection__recent {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
}
.remote-connection__recent .el-select {
    flex: 1;
    min-width: 0;
}
.remote-connection__checks {
    display: flex;
    flex-wrap: wrap;
    gap: 8px 24px;
}
.remote-connection__hint {
    color: var(--muted);
    font-size: 12px;
    line-height: 1.7;
    margin: 0 0 16px;
}
.remote-connection__actions {
    display: flex;
    justify-content: flex-start;
    gap: 12px;
    margin-top: 20px;
}
.remote-connection__actions .el-button + .el-button {
    margin-left: 0;
}
@media (max-width: 640px) {
    .remote-connection__target {
        grid-template-columns: 1fr;
        gap: 0;
    }
    .remote-connection__recent {
        flex-wrap: wrap;
    }
    .remote-connection__recent .el-select {
        flex-basis: 100%;
    }
}
</style>
