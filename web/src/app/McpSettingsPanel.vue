<!--
 * FilePath    : pbi-utils\web\src\app\McpSettingsPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 提供带真实安装路径与允许修改配置的 AI 接入提示词, 不启动服务或修改第三方配置.
-->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue"
import type { BridgeClient, McpConfiguration } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { useFeedback } from "@/composables/ui/useFeedback"
import McpConnectionsPanel from "./McpConnectionsPanel.vue"
import UiPanel from "@/ui/UiPanel.vue"

/** bridge 只读取宿主生成的安装路径与命令; 失败不显示可复制的旧配置, 卸载后忽略迟到结果. */
const props = defineProps<{ bridge: BridgeClient }>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const { t } = useI18n()
const { showFeedback } = useFeedback()
const expanded = ref(false)
const configuration = ref<McpConfiguration>()
const busy = ref(false)
const connectionsBusy = ref(false)
const combinedBusy = computed(() => busy.value || connectionsBusy.value)
const error = ref("")
let mounted = true
let requestId = ""
const json = computed(() => {
    if (!configuration.value) return ""
    const launch = configuration.value.readWrite
    return JSON.stringify({ mcpServers: { "pbi-utils": launch } }, null, 2)
})
/** 按当前语言生成允许修改的可交给第三方 AI 的接入步骤; 路径和参数来自宿主, 不执行安装或授权. */
const prompt = computed(() =>
    configuration.value
        ? t("mcp.setupPrompt", {
              configuration: json.value,
          })
        : "",
)
watch(combinedBusy, (value) => emit("busy", value))
useMessageNotices("mcp-settings", () => [!!error.value && { key: "error", message: error.value, type: "error" }])

/** 无论正文是否展开都复制当前语言的完整提示词; 剪贴板失败通过共享反馈报告. */
async function copyPrompt(): Promise<void> {
    if (!configuration.value) return
    try {
        await navigator.clipboard.writeText(prompt.value)
        showFeedback({ message: t("model.copied") })
    } catch {
        showFeedback({ message: t("model.copyFailed"), type: "error" })
    }
}

/** 读取本次安装的固定启动命令; 不创建 MCP 进程或访问第三方 AI, 出错清空配置并允许重新加载. */
async function load(): Promise<void> {
    if (busy.value) return
    busy.value = true
    error.value = ""
    configuration.value = undefined
    try {
        const request = props.bridge.request("mcp.configuration", {})
        requestId = request.id
        const result = await request.promise
        if (mounted) configuration.value = result
    } catch (cause) {
        if (mounted) error.value = cause instanceof Error ? cause.message : t("mcp.loadFailed")
    } finally {
        requestId = ""
        if (mounted) busy.value = false
    }
}
onMounted(load)
onUnmounted(() => {
    mounted = false
    if (requestId) void props.bridge.request("request.cancel", { requestId }).promise.catch(() => undefined)
})
</script>
<template>
    <UiPanel class="mcp-settings" :aria-busy="busy">
        <h2>{{ t("mcp.title") }}</h2>
        <p>{{ t("mcp.description") }}</p>
        <el-skeleton v-if="busy" :rows="3" animated />
        <template v-if="configuration">
            <p v-if="configuration.isSimulation" class="mcp-simulation" role="status">{{ t("mcp.simulation") }}</p>
            <div class="mcp-prompt">
                <div class="mcp-prompt-actions">
                    <el-button type="primary" @click="copyPrompt">{{ t("mcp.copyPrompt") }}</el-button>
                    <el-button :aria-expanded="expanded" aria-controls="mcp-prompt-content" @click="expanded = !expanded">{{
                        t(expanded ? "mcp.hidePrompt" : "mcp.showPrompt")
                    }}</el-button>
                </div>
                <pre v-show="expanded" id="mcp-prompt-content"><code>{{ prompt }}</code></pre>
            </div>
        </template>
        <el-button v-else-if="!busy" @click="load">{{ t("mcp.reload") }}</el-button>
        <p class="mcp-boundary">{{ t("mcp.desktop") }}</p>
        <p class="mcp-boundary">{{ t("mcp.permission") }}</p>
        <p class="mcp-boundary">{{ t("mcp.boundary") }}</p>
        <p class="mcp-boundary">{{ t("mcp.privacy") }}</p>
        <McpConnectionsPanel :bridge="bridge" @busy="connectionsBusy = $event" />
    </UiPanel>
</template>
<style scoped lang="scss">
.mcp-settings p {
    line-height: 1.7;
}
.mcp-prompt-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 12px;
    margin-block: 16px;
}
.mcp-prompt-actions .el-button + .el-button {
    margin-left: 0;
}
.mcp-prompt pre {
    margin: 0 0 16px;
    padding: 16px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--el-fill-color-light);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 12px;
    line-height: 1.7;
}
.mcp-boundary,
.mcp-simulation {
    color: var(--muted);
}
</style>
