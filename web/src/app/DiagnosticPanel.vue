<!--
 * FilePath    : pbi-utils\web\src\app\DiagnosticPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 打开运行环境分区时自动检测, 按界面语言和容器宽度展示共享服务结果.
-->
<script setup lang="ts">
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { useOperationMessage } from "@/composables/ui/useFeedback"
import { useI18n } from "@/composables/i18n/useI18n"

import { computed, onMounted, onUnmounted, ref } from "vue"
import type { BridgeClient, Methods } from "../bridge/client"
const props = defineProps<{ bridge: BridgeClient }>()
const { t } = useI18n()
const result = ref<Methods["doctor"]["result"]>()
const busy = ref(true)
const error = ref("")
const content = ref<HTMLElement>()
const columns = ref(3)
let requestId = ""
let mounted = true
let observer: ResizeObserver | undefined
let resizeFrame: number | undefined
let pendingWidth = 0
useOperationMessage(() => (busy.value ? { message: t("diagnostic.loading") } : undefined))
/** 按当前界面语言展示已知失败提示, 保留宿主提供的其他安全错误信息. */
const errorMessage = computed(() => {
    if (error.value === "运行环境暂时无法读取") return t("diagnostic.temporarilyUnavailable")
    if (error.value === "运行环境检测失败") return t("diagnostic.failed")
    return error.value
})
/** 仅翻译标签与能力状态, 原始系统信息和真实版本按宿主结果展示. */
const rows = computed(() => {
    if (!result.value) return []
    const data = result.value.diagnostic.environment
    return [
        { label: t("diagnostic.version"), value: result.value.diagnostic.version, note: t("diagnostic.versionNote") },
        { label: t("diagnostic.os"), value: result.value.isSimulation ? t("diagnostic.browserSimulation") : data.os, note: t("diagnostic.osNote") },
        {
            label: t("diagnostic.architecture"),
            value: result.value.isSimulation ? t("diagnostic.notDetected") : data.architecture,
            note: t("diagnostic.architectureNote"),
        },
        { label: t("diagnostic.runtime"), value: result.value.isSimulation ? t("diagnostic.notDetected") : data.runtime, note: t("diagnostic.runtimeNote") },
        {
            label: t("diagnostic.desktop"),
            value: t(
                data.desktop === "available-platform"
                    ? "diagnostic.desktopAvailable"
                    : data.desktop === "simulation"
                      ? "diagnostic.browserPreview"
                      : "diagnostic.desktopUnavailable",
            ),
            note: t("diagnostic.desktopNote"),
        },
        { label: "WebView2", value: result.value.isSimulation ? t("diagnostic.notDetected") : result.value.webView2, note: t("diagnostic.webViewNote") },
        {
            label: "Power BI",
            value: t(
                data.powerBi === "read-only-discovery"
                    ? "diagnostic.discoveryAvailable"
                    : data.powerBi === "simulation"
                      ? "diagnostic.simulation"
                      : "diagnostic.discoveryUnavailable",
            ),
            note: t("diagnostic.powerBiNote"),
        },
    ]
})
/** 根据卡片可用宽度调整列数, 零宽度测量保留默认三列; 不依赖整页视口断点. */
function updateColumns(width: number) {
    if (width > 0) columns.value = width >= 840 ? 3 : width >= 560 ? 2 : 1
}
/** 同帧宽度通知只排队一次, 在下一帧应用最新宽度, 避免列高变化回写 ResizeObserver 交付阶段. */
function scheduleColumns(width: number) {
    if (!mounted) return
    pendingWidth = width
    if (resizeFrame !== undefined) return
    resizeFrame = requestAnimationFrame(() => {
        resizeFrame = undefined
        if (mounted) updateColumns(pendingWidth)
    })
}
/** 自动读取共享诊断, 不发送模型请求; 离开页面后不更新显示. */
async function load() {
    try {
        const request = props.bridge.request("doctor", {})
        requestId = request.id
        const response = await request.promise
        if (mounted) result.value = response
    } catch (cause) {
        if (mounted) error.value = cause instanceof Error ? cause.message : "运行环境检测失败"
    } finally {
        requestId = ""
        if (mounted) busy.value = false
    }
}
/** 监听关于卡片的实际宽度并启动一次诊断, 页面缩放或侧栏变化会重新计算列数. */
onMounted(() => {
    if (content.value) {
        updateColumns(content.value.getBoundingClientRect().width)
        if (typeof ResizeObserver !== "undefined") {
            observer = new ResizeObserver((entries) => {
                const entry = entries[0]
                if (entry) scheduleColumns(entry.contentRect.width)
            })
            observer.observe(content.value)
        }
    }
    void load()
})
/** 离开设置页时断开宽度监听并取消在途诊断, 不改变其他页面设置. */
onUnmounted(() => {
    mounted = false
    observer?.disconnect()
    if (resizeFrame !== undefined) cancelAnimationFrame(resizeFrame)
    resizeFrame = undefined
    if (requestId) void props.bridge.request("request.cancel", { requestId }).promise.catch(() => undefined)
})
useMessageNotices("diagnostic-panel", () => [
    !!error.value && { key: "error", message: errorMessage.value, type: "error" },
    !error.value && !!result.value?.isSimulation && { key: "simulation", once: true, message: t("diagnostic.simulationNotice"), type: "info" },
])
</script>
<template>
    <UiPanel class="environment-panel" aria-labelledby="environment-heading" :aria-busy="busy">
        <div ref="content" class="environment-content">
            <div class="panel-heading">
                <div>
                    <h2 id="environment-heading">{{ t("diagnostic.heading") }}</h2>
                </div>
                <el-tag v-if="result" class="environment-badge" :type="result.isSimulation ? 'info' : 'success'">{{
                    t(result.isSimulation ? "diagnostic.browserSimulation" : "diagnostic.completed")
                }}</el-tag>
            </div>
            <el-skeleton v-if="busy && !result" :rows="5" animated />

            <template v-else-if="result && !error">
                <el-descriptions class="environment-descriptions" direction="vertical" :column="columns" border
                    ><el-descriptions-item v-for="row in rows" :key="row.label" :label="row.label"
                        ><strong>{{ row.value }}</strong
                        ><small class="description-note">{{ row.note }}</small></el-descriptions-item
                    ></el-descriptions
                >
            </template>
        </div>
    </UiPanel>
</template>
