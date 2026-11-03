<!--
 * FilePath    : pbi-utils\web\src\app\DataResetPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 提供独立的本地数据重置入口, 两次确认及实际清理由宿主裁决.
-->
<script setup lang="ts">
import { onUnmounted, ref, watch } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import UiPanel from "@/ui/UiPanel.vue"
/** bridge 只调用固定无参宿主用例, disabled 表示其他设置操作在途; 不读取凭据或接收任意磁盘路径. */
const props = defineProps<{ bridge: BridgeClient; disabled?: boolean }>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const { t } = useI18n()
const { showFeedback } = useFeedback()
const busy = ref(false)
const error = ref("")
let requestId = ""
let mounted = true
watch(busy, (value) => emit("busy", value), { immediate: true })
useOperationMessage(() => (busy.value ? { message: t("dataReset.waiting") } : undefined))
useMessageNotices("data-reset", () => [!!error.value && { key: "error", message: error.value, type: "error" }])
/** 请求宿主依次展示两次风险确认; 取消不清理, scheduled 仅表示待关闭后执行, 不把排队回执当作已删除成功. */
async function resetData(): Promise<void> {
    if (busy.value || props.disabled) return
    busy.value = true
    error.value = ""
    try {
        const request = props.bridge.request("app.resetUserData", {})
        requestId = request.id
        const result = await request.promise
        if (mounted && result.scheduled) showFeedback({ message: t(result.isSimulation ? "dataReset.simulated" : "dataReset.restarting"), type: "info" })
    } catch (cause) {
        if (mounted) error.value = cause instanceof Error ? cause.message : t("dataReset.failed")
    } finally {
        requestId = ""
        if (mounted) busy.value = false
    }
}
onUnmounted(() => {
    mounted = false
    if (requestId) void props.bridge.request("request.cancel", { requestId }).promise.catch(() => undefined)
})
</script>
<template>
    <UiPanel class="data-reset-panel" aria-labelledby="data-reset-title" :aria-busy="busy">
        <h2 id="data-reset-title">{{ t("dataReset.title") }}</h2>
        <p>{{ t("dataReset.description") }}</p>
        <el-button class="data-reset-button" type="danger" :loading="busy" :disabled="busy || disabled" @click="resetData">
            {{ t("dataReset.button") }}
        </el-button>
    </UiPanel>
</template>
<style scoped lang="scss">
.data-reset-panel p {
    color: var(--muted);
    margin-block: 0 16px;
}
</style>
