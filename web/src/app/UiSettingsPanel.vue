<!--
 * FilePath    : pbi-utils\web\src\app\UiSettingsPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 保存统一语言、最近文件和 DAX 格式偏好, 失败时保留宿主已生效的设置.
-->
<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from "vue"
import type { BridgeClient, Methods } from "@/bridge/client"
import { setUiSettings } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"

const props = defineProps<{ bridge: BridgeClient }>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const { locale, showRecentFiles, daxFormatStyle, t } = useI18n()
const { showFeedback } = useFeedback()
const settings = ref<Methods["ui.settings"]["result"]>()
const selected = ref(locale.value)
const selectedRecentFiles = ref(showRecentFiles.value)
const selectedDaxFormatStyle = ref(daxFormatStyle.value)
const busy = ref(true)
const saving = ref(false)
const error = ref("")
let mounted = true
let requestId = ""
useOperationMessage(() => (busy.value ? { message: t(saving.value ? "uiSettings.saving" : "uiSettings.loading") } : undefined))
watch(busy, (value) => emit("busy", value), { immediate: true })

/** 将组件发出的通用开关值收窄为 DAX 布局, 忽略非白名单值, 等待保存成功才更新共享偏好. */
function selectDaxFormatStyle(value: string | number | boolean) {
    if (value === "long" || value === "short") selectedDaxFormatStyle.value = value
}

/** 读取偏好或仅提交本次修改字段, 成功回读完整偏好才更新共享状态; 失败恢复当前已生效值. */
async function update(patch?: Methods["ui.saveSettings"]["params"]) {
    if (requestId) return
    saving.value = !!patch
    busy.value = true
    error.value = ""
    try {
        const request = patch ? props.bridge.request("ui.saveSettings", patch) : props.bridge.request("ui.settings", {})
        requestId = request.id
        const result = await request.promise
        if (!mounted) return
        settings.value = result
        selected.value = result.locale
        selectedRecentFiles.value = result.showRecentFiles
        selectedDaxFormatStyle.value = result.daxFormatStyle
        setUiSettings(result)
        if (patch)
            showFeedback({
                message: t(result.isSimulation ? "uiSettings.simulationUpdated" : "uiSettings.updated"),
                type: result.isSimulation ? "info" : "success",
            })
    } catch (cause) {
        if (!mounted) return
        selected.value = locale.value
        selectedRecentFiles.value = showRecentFiles.value
        selectedDaxFormatStyle.value = daxFormatStyle.value
        error.value = cause instanceof Error ? cause.message : t("uiSettings.failed")
    } finally {
        requestId = ""
        if (mounted) busy.value = false
    }
}
onMounted(() => update())
onUnmounted(() => {
    mounted = false
    if (requestId) void props.bridge.request("request.cancel", { requestId }).promise.catch(() => undefined)
})
useMessageNotices("ui-settings-panel-errors", () => [!!error.value && { key: "error", message: error.value, type: "error" }])
useMessageNotices(
    "ui-settings-panel",
    () => [
        !!settings.value?.warning && { key: "warning", message: settings.value.warning },
        !!settings.value?.isSimulation && { key: "simulation", once: true, message: t("uiSettings.simulationHint"), type: "info" },
    ],
    { ready: () => !!settings.value },
)
</script>
<template>
    <UiPanel class="ui-settings-panel" aria-labelledby="ui-settings-heading" :aria-busy="busy">
        <h2 id="ui-settings-heading">{{ t("uiSettings.title") }}</h2>

        <el-form label-position="top" @submit.prevent>
            <el-form-item :label="t('uiSettings.language')" for="ui-language">
                <el-select
                    id="ui-language"
                    v-model="selected"
                    :aria-label="t('uiSettings.language')"
                    :disabled="busy || !settings"
                    :loading="busy"
                    @change="update({ locale: selected })"
                >
                    <el-option value="zh-cn" label="中文" />
                    <el-option value="en-us" label="English" />
                </el-select>
                <p class="hint">{{ t("uiSettings.hint") }}</p>
            </el-form-item>
            <el-form-item :label="t('uiSettings.recentFiles')" for="ui-recent-files">
                <el-switch
                    id="ui-recent-files"
                    :model-value="selectedRecentFiles"
                    @update:model-value="selectedRecentFiles = $event === true"
                    :aria-label="t('uiSettings.recentFiles')"
                    :disabled="busy || !settings"
                    :loading="busy && saving"
                    @change="update({ showRecentFiles: selectedRecentFiles })"
                />
                <p class="hint">{{ t("uiSettings.recentFilesHint") }}</p>
            </el-form-item>
            <el-form-item :label="t('uiSettings.daxFormatStyle')" for="ui-dax-format-style">
                <el-switch
                    id="ui-dax-format-style"
                    :model-value="selectedDaxFormatStyle"
                    @update:model-value="selectDaxFormatStyle"
                    active-value="long"
                    inactive-value="short"
                    :active-text="t('uiSettings.daxFormatLong')"
                    :inactive-text="t('uiSettings.daxFormatShort')"
                    :aria-label="t('uiSettings.daxFormatStyle')"
                    :title="t('uiSettings.daxFormatHint')"
                    :disabled="busy || !settings"
                    :loading="busy && saving"
                    @change="update({ daxFormatStyle: selectedDaxFormatStyle })"
                />
                <p class="hint">{{ t("uiSettings.daxFormatHint") }}</p>
            </el-form-item>
        </el-form>

        <el-button v-if="!settings && !busy" @click="update()">{{ t("uiSettings.retry") }}</el-button>
    </UiPanel>
</template>
<style scoped lang="scss">
.hint {
    flex-basis: 100%;
    margin: 6px 0 0;
}
.el-select {
    width: 100%;
    max-width: 360px;
}
</style>
