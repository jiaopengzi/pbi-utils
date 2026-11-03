<!--
 * FilePath    : pbi-utils\web\src\ui\OperationMessage.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 在全局浮动消息内展示持续操作、权威进度和取消入口, 不占业务页面空间.
-->
<script setup lang="ts">
import { computed, onUnmounted, ref, type ComputedRef } from "vue"
import { hostText } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import { showMessage } from "./message"
import type { OperationMessageState } from "@/composables/ui/useFeedback"

const props = defineProps<{ source: ComputedRef<OperationMessageState | undefined> }>()
const { t } = useI18n()
const state = computed(() => props.source.value)
const cancelling = ref(false)
let disposed = false
onUnmounted(() => {
    disposed = true
})
const progress = computed(() => {
    const value = state.value?.progress
    return value === undefined || !Number.isFinite(value) ? undefined : Math.max(0, Math.min(100, value))
})

/** 取消只调用所属操作的回调, 不把确认回执当作终态; 失败浮动提示并允许业务重试. */
async function cancel(): Promise<void> {
    const action = state.value?.cancel
    if (!action || state.value?.cancelDisabled || cancelling.value) return
    cancelling.value = true
    try {
        await action()
    } catch (cause) {
        if (!disposed && state.value) showMessage(cause instanceof Error ? cause.message : String(cause), "error")
    } finally {
        cancelling.value = false
    }
}
</script>

<template>
    <div v-if="state" class="operation-message" role="status">
        <div class="operation-feedback">
            <div class="operation-feedback__row">
                <span class="operation-feedback__message">{{ hostText(state.message) }}</span>
                <el-button v-if="state.cancel" class="operation-feedback__cancel" :disabled="state.cancelDisabled || cancelling" @click="cancel">{{
                    state.cancelLabel || t("connection.cancel")
                }}</el-button>
            </div>
            <el-progress v-if="progress !== undefined" :percentage="progress" :status="progress === 100 ? 'success' : undefined" />
        </div>
    </div>
</template>

<style scoped lang="scss">
.operation-message.operation-message {
    margin: 0;
    pointer-events: auto;
    flex-shrink: 0;
    box-shadow: var(--el-box-shadow-light);
    padding: 12px 16px;
    border: 1px solid var(--el-color-info-light-8);
    border-radius: var(--el-border-radius-base);
    background: var(--el-color-info-light-9);
    color: var(--el-color-info);
}
.operation-feedback {
    min-width: 240px;
    max-width: 100%;
}
.operation-feedback__row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
}
.operation-feedback__message {
    overflow-wrap: anywhere;
    line-height: 1.6;
}
.operation-feedback__cancel {
    flex-shrink: 0;
}
.el-progress {
    margin-top: 8px;
}
</style>
