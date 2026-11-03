<!--
 * FilePath    : pbi-utils\web\src\app\LoggingPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 设置页日志级别选择及固定目录打开, 所有操作经类型化桥接.
-->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { BridgeClient, LoggingView, LogLevel } from "../bridge/client"
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"

const props = defineProps<{ bridge: BridgeClient }>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const { t, hostText } = useI18n()
const { showFeedback } = useFeedback()
const settings = ref<LoggingView>()
const selected = ref<LogLevel>("Information")
const busy = ref(true)
const operation = ref<"loading" | "saving" | "opening">("loading")
const error = ref("")
let mounted = true
let requestId = ""
useOperationMessage(() => (busy.value ? { message: t("logging." + operation.value) } : undefined))
const levels = computed<{ value: LogLevel; label: string }[]>(() => [
    { value: "Verbose", label: t("logging.verbose") },
    { value: "Debug", label: t("logging.debug") },
    { value: "Information", label: t("logging.information") },
    { value: "Warning", label: t("logging.warning") },
    { value: "Error", label: t("logging.error") },
    { value: "Fatal", label: t("logging.fatal") },
])
watch(busy, (value) => emit("busy", value), { immediate: true })
/** 加载及保存共用生命周期, 保存失败恢复宿主已生效的选项. */
async function update(level?: LogLevel) {
    if (requestId) return
    operation.value = level ? "saving" : "loading"
    busy.value = true
    error.value = ""
    try {
        const request = level ? props.bridge.request("logging.save", { level }) : props.bridge.request("logging.settings", {})
        requestId = request.id
        const value = await request.promise
        if (!mounted) return
        settings.value = value
        selected.value = value.level
        if (level)
            showFeedback({
                message: t(value.isSimulation ? "logging.simulationUpdated" : "logging.updated"),
                type: value.isSimulation ? "info" : "success",
            })
    } catch (cause) {
        if (!mounted) return
        if (settings.value) selected.value = settings.value.level
        error.value = cause instanceof Error ? cause.message : t("logging.updateFailed")
    } finally {
        requestId = ""
        if (mounted) busy.value = false
    }
}
/** 不传路径, 目录由宿主确定; 浏览器不会声称打开了本机目录. */
async function openDirectory() {
    if (busy.value || !settings.value) return
    operation.value = "opening"
    busy.value = true
    error.value = ""
    try {
        const request = props.bridge.request("logging.openDirectory", {})
        requestId = request.id
        const value = await request.promise
        if (mounted)
            showFeedback({
                message: t(value.isSimulation ? "logging.simulationNotOpened" : value.opened ? "logging.opened" : "logging.notOpened"),
                type: value.isSimulation || !value.opened ? "info" : "success",
            })
    } catch (cause) {
        if (mounted) error.value = cause instanceof Error ? cause.message : t("logging.openFailed")
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
useMessageNotices("logging-panel-errors", () => [!!error.value && { key: "error", message: error.value, type: "error" }])
useMessageNotices(
    "logging-panel",
    () => [
        !!settings.value?.warning && { key: "warning", message: settings.value.warning },
        !!settings.value?.isSimulation && { key: "simulation", once: true, message: t("logging.simulationHint"), type: "info" },
    ],
    { ready: () => !!settings.value },
)
</script>
<template>
    <UiPanel class="logging-panel" aria-labelledby="logging-heading" :aria-busy="busy">
        <div class="panel-heading">
            <div>
                <h2 id="logging-heading">{{ t("logging.title") }}</h2>
            </div>
            <el-tag v-if="settings?.isSimulation" type="info">{{ t("logging.simulation") }}</el-tag>
        </div>

        <el-form label-position="top" @submit.prevent>
            <el-form-item :label="t('logging.level')" for="logging-level">
                <el-select
                    id="logging-level"
                    v-model="selected"
                    :aria-label="t('logging.level')"
                    :disabled="busy || !settings"
                    :loading="busy && operation !== 'opening'"
                    @change="update"
                >
                    <el-option v-for="level in levels" :key="level.value" :value="level.value" :label="level.label" />
                </el-select>
            </el-form-item>
        </el-form>
        <p class="hint">{{ t("logging.levelHint") }}</p>
        <div class="log-directory">
            <strong>{{ t("logging.directory") }}</strong
            ><span>{{ settings ? (settings.isSimulation ? hostText(settings.directory) : settings.directory) : "-" }}</span>
        </div>
        <el-button :loading="busy && operation === 'opening'" :disabled="busy || !settings" @click="openDirectory">{{ t("logging.openDirectory") }}</el-button>
        <p class="hint">{{ t("logging.retentionHint") }}</p>
        <el-button v-if="!settings && !busy" @click="update()">{{ t("logging.retry") }}</el-button>
    </UiPanel>
</template>
<style scoped lang="scss">
.logging-panel {
    margin-top: 18px;
}
.el-select {
    width: 100%;
    max-width: 360px;
}
.log-directory {
    display: grid;
    gap: 8px;
    margin: 16px 0;
}
.log-directory span {
    overflow-wrap: anywhere;
    color: var(--muted);
}
</style>
