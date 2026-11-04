<!--
 * FilePath    : pbi-utils\web\src\app\SoftwareUpdatePanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 紧凑展示软件版本、更新状态与来源设置, 通过固定来源入口访问软件发布页面.
-->
<script setup lang="ts">
import { computed, onUnmounted } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useProductUpdates, type ProductUpdates } from "@/composables/app/useProductUpdates"
import { useI18n } from "@/composables/i18n/useI18n"
import { useOperationMessage } from "@/composables/ui/useFeedback"
import UiPanel from "@/ui/UiPanel.vue"

/** 接收应用级更新通道; 独立挂载时只读取设置, 不重复启动后台检查; 错误由共享反馈与结果展示. */
const props = defineProps<{ bridge: BridgeClient; updates?: ProductUpdates }>()
const updates =
    props.updates ??
    useProductUpdates(
        () => props.bridge,
        () => true,
        false,
    )
const { state, currentVersion, loading, saving, checking, opening, cancellationRequested, error, checkResult, load, save, check, open, cancelCheck } = updates
const { t } = useI18n()
const settingsDisabled = computed(() => loading.value || saving.value || !state.value)
const simulation = computed(() => __MOCK_BRIDGE__ || checkResult.value?.isSimulation === true)
const status = computed(() => (error.value ? "failed" : (checkResult.value?.status ?? "notChecked")))
/** 将宿主绝对时间显示为当前机器的 YYYY-MM-DD HH:mm:ss; 空值或无效时间保持未知值, 不改写保存值. */
const checkedAt = computed(() => {
    const value = checkResult.value?.checkedAtUtc
    if (!value) return "-"
    const date = new Date(value)
    if (!Number.isFinite(date.getTime())) return "-"
    const day = [String(date.getFullYear()).padStart(4, "0"), ...[date.getMonth() + 1, date.getDate()].map((part) => String(part).padStart(2, "0"))].join("-")
    const time = [date.getHours(), date.getMinutes(), date.getSeconds()].map((part) => String(part).padStart(2, "0")).join(":")
    return `${day} ${time}`
})
const updateErrors: Record<string, string> = {
    UPDATE_RATE_LIMITED: "updates.rateLimited",
    UPDATE_HTTP_FAILED: "updates.httpFailed",
    UPDATE_TIMEOUT: "updates.timeout",
    UPDATE_INVALID_RESPONSE: "updates.invalidResponse",
    UPDATE_RESPONSE_TOO_LARGE: "updates.responseTooLarge",
    UPDATE_PAGE_LIMIT: "updates.pageLimit",
    UPDATE_NETWORK_FAILED: "updates.networkFailed",
    UPDATE_CHECK_BUSY: "updates.checkBusy",
}
const resultMessage = computed(
    () => error.value || t(status.value === "failed" ? (updateErrors[checkResult.value?.errorCode ?? ""] ?? "updates.failed") : `updates.${status.value}`),
)
useOperationMessage(() => {
    const key = loading.value ? "loading" : saving.value ? "saving" : checking.value ? "checking" : ""
    return key
        ? {
              message: t(`updates.${key}`),
              cancel: checking.value ? () => cancelCheck() : undefined,
              cancelLabel: t("updates.cancel"),
              cancelDisabled: cancellationRequested.value,
          }
        : undefined
})
useOperationMessage(() => (opening.value ? { message: t("updates.opening") } : undefined))
/** 只接受真实开关值, 持久化成功后才改变控件的权威值. */
function selectAutomatic(value: string | number | boolean): void {
    if (typeof value === "boolean" && state.value) void save({ automaticCheck: value })
}
/** 只保存固定来源; 在途检查会失效并取消, 不跨源拼接旧结果. */
function selectSource(value: unknown): void {
    if ((value === "github" || value === "gitee") && state.value) void save({ source: value })
}
onUnmounted(() => cancelCheck(true))
</script>
<template>
    <UiPanel class="software-update-panel" aria-labelledby="software-updates-heading" :aria-busy="loading || saving || checking || opening">
        <div class="update-heading">
            <h2 id="software-updates-heading">{{ t("updates.title") }}</h2>
            <el-tag v-if="simulation" type="info">{{ t("updates.simulation") }}</el-tag>
        </div>
        <div class="update-result" :data-status="status" :data-simulation="simulation" role="status" aria-live="polite">
            <span class="update-current-version"
                >{{ t("updates.currentVersion") }} <strong>{{ currentVersion }}</strong></span
            >
            <span class="update-release-version"
                >{{ t("updates.latestVersion") }} <strong>{{ checkResult?.latestVersion ?? "-" }}</strong></span
            >
            <span class="update-status">{{ resultMessage }}</span>
        </div>
        <p v-if="simulation" class="hint">{{ t("updates.simulationHint") }}</p>
        <form class="update-controls" @submit.prevent>
            <div class="update-control">
                <label for="updates-automatic">{{ t("updates.automatic") }}</label>
                <el-switch
                    id="updates-automatic"
                    :model-value="state?.settings.automaticCheck ?? true"
                    :aria-label="t('updates.automatic')"
                    :disabled="settingsDisabled"
                    :loading="saving"
                    @change="selectAutomatic"
                />
            </div>
            <div class="update-control update-source">
                <label for="updates-source">{{ t("updates.source") }}</label>
                <el-select
                    id="updates-source"
                    :model-value="state?.settings.source ?? 'github'"
                    :aria-label="t('updates.source')"
                    :disabled="settingsDisabled"
                    @change="selectSource"
                >
                    <el-option value="github" label="GitHub" /><el-option value="gitee" label="Gitee" />
                </el-select>
            </div>
            <div class="update-actions">
                <el-button data-action="check" type="primary" :loading="checking" :disabled="settingsDisabled || checking" @click="check(false)">{{
                    t("updates.check")
                }}</el-button>
                <el-button data-action="release" @click="open()">{{ t("updates.release") }}</el-button>
                <el-button v-if="!state && !loading" @click="load">{{ t("updates.retry") }}</el-button>
            </div>
        </form>
        <dl class="update-details">
            <div>
                <dt>{{ t("updates.checkedAt") }}</dt>
                <dd>
                    <time :datetime="checkedAt === '-' ? undefined : checkResult?.checkedAtUtc">{{ checkedAt }}</time>
                </dd>
            </div>
            <div v-if="checkResult?.errorCode">
                <dt>{{ t("updates.errorCode") }}</dt>
                <dd>{{ checkResult.errorCode }}</dd>
            </div>
        </dl>
    </UiPanel>
</template>
<style scoped lang="scss">
.update-heading,
.update-result,
.update-controls,
.update-control,
.update-actions,
.update-details,
.update-details > div {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 12px;
}
.update-heading h2 {
    margin: 0;
}
.update-current-version,
.update-release-version,
.update-details dt {
    color: var(--el-text-color-secondary);
}
.update-current-version strong,
.update-release-version strong {
    margin-left: 4px;
    color: var(--el-text-color-primary);
    font-weight: 600;
}
.update-actions .el-button + .el-button {
    margin-left: 0;
}
.update-controls {
    gap: 12px 28px;
    margin-block: 18px 12px;
}
.update-control {
    gap: 10px;
}
.update-control label {
    color: var(--el-text-color-regular);
}
.update-source .el-select {
    width: 140px;
}
.hint {
    margin: 8px 0 0;
}
.update-result {
    margin-top: 10px;
    gap: 8px 16px;
}
.update-status {
    color: var(--el-text-color-secondary);
}
.update-result[data-status="available"] .update-status {
    color: var(--brand-primary);
}
.update-result[data-status="current"] .update-status {
    color: var(--el-color-success-dark-2);
}
.update-result[data-status="failed"] .update-status {
    color: var(--el-color-danger-dark-2);
}
.update-result[data-simulation="true"] .update-status {
    color: var(--el-text-color-secondary);
}
.update-details {
    gap: 8px 24px;
    margin-block: 0;
    font-size: 13px;
}
.update-details > div {
    gap: 8px;
}
.update-details dd {
    margin: 0;
    overflow-wrap: anywhere;
    font-variant-numeric: tabular-nums;
}
</style>
