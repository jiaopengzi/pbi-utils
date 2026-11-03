<!--
 * FilePath    : pbi-utils\web\src\app\LearningPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : Vue 学习页面提供浏览器打开按钮和自适应区域, 固定外站由独立宿主浏览器承载.
-->
<script setup lang="ts">
import { onUnmounted, ref } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback } from "@/composables/ui/useFeedback"
import { useLearningView } from "@/composables/app/useLearningView"

/** 显示固定学习网站; disabled 保护应用操作锁, busy 仅报告显式浏览器打开请求, 失败保留页面. */
const props = defineProps<{ bridge: BridgeClient; disabled?: boolean }>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const { t } = useI18n()
const { showFeedback } = useFeedback()
const viewport = ref<HTMLElement>()
const opening = ref(false)
const { simulation, failed, retry } = useLearningView(props.bridge, viewport, () => props.disabled === true)
let mounted = true

/** 用户显式点击后仅请求宿主固定学习地址; 不接受任意 URL, 失败通过共享反馈展示. */
async function openBrowser(): Promise<void> {
    if (opening.value || props.disabled) return
    opening.value = true
    emit("busy", true)
    try {
        const result = await props.bridge.request("learning.open", {}).promise
        if (mounted && result.isSimulation) showFeedback({ message: t("learning.openSimulation"), type: "info" })
    } catch {
        if (mounted) showFeedback({ message: t("learning.failed"), type: "error" })
    } finally {
        if (mounted) {
            opening.value = false
            emit("busy", false)
        }
    }
}
onUnmounted(() => (mounted = false))
</script>
<template>
    <section class="learning-panel">
        <el-button type="primary" :disabled="disabled || opening" :loading="opening" @click="openBrowser">
            {{ t("learning.openBrowser") }}
        </el-button>
        <div ref="viewport" class="learning-viewport" :aria-label="t('learning.viewport')" tabindex="0">
            <div class="learning-placeholder">
                <p>{{ t(simulation ? "learning.preview" : failed ? "learning.failed" : "learning.loading") }}</p>
                <el-button v-if="failed" :disabled="disabled" @click="retry">{{ t("learning.retry") }}</el-button>
            </div>
        </div>
    </section>
</template>
<style scoped lang="scss">
:global(.content:has(> .learning-panel)) {
    display: flex;
    min-height: 0;
}
.learning-panel {
    display: flex;
    flex: 1;
    flex-direction: column;
    min-width: 0;
    min-height: 0;
    gap: 18px;
    > .el-button {
        align-self: flex-start;
    }
}
.learning-viewport {
    display: grid;
    flex: 1;
    place-items: center;
    min-height: 0;
    overflow: hidden;
    background: var(--surface);
    p {
        max-width: 60ch;
        padding: 24px;
        text-align: center;
        color: var(--muted);
    }
}
.learning-placeholder {
    text-align: center;
}
</style>
