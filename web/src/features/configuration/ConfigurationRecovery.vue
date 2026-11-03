<!--
 * FilePath    : pbi-utils\web\src\features\configuration\ConfigurationRecovery.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 连接模型的核验回读与离线保存恢复入口, 不重放未知提交且恢复前独立确认.
-->
<script setup lang="ts">
import { computed, nextTick } from "vue"
import { ElMessageBox } from "element-plus"
import type { ConfigurationContext } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import { configurationRecoveryKey } from "./saveStatus"
import { hasPendingModelUpdate, isConnectedDesktopProject, requiresModelVerification } from "@/bridge/modelAccess"
const props = withDefaults(defineProps<{ context?: ConfigurationContext; locked: boolean; active?: boolean }>(), { active: true })
const emit = defineEmits<{ complete: []; discard: []; dialog: [value: boolean] }>()
const { t, hostText } = useI18n()
const pending = computed(() => hasPendingModelUpdate(props.context?.project))
const unknown = computed(() => requiresModelVerification(props.context?.project))
const connectedDesktop = computed(() => isConnectedDesktopProject(props.context?.project))
useMessageNotices(
    "configuration-recovery",
    () => [
        pending.value && {
            key: `${props.context?.targetId}:recovery`,
            message:
                unknown.value && !props.context?.project.saveResult?.errorCode
                    ? t(configurationRecoveryKey(props.context?.project))
                    : hostText(props.context?.project.saveResult?.message) || t(configurationRecoveryKey(props.context?.project)),
        },
    ],
    { ready: () => props.active !== false },
)
/** 连接 Desktop 仅确认回读且不回滚提交; 离线放弃仍绑定原目标与基线, 不接受迟到确认. */
async function discard(): Promise<void> {
    const current = props.context
    if (props.locked || !pending.value || !current) return
    emit("dialog", true)
    let accepted = false
    try {
        await ElMessageBox.confirm(
            t(connectedDesktop.value ? "app.reloadModelConfirm" : current.project.saveResult?.errorCode ? "app.discardSync" : "app.discardDraft"),
            t(connectedDesktop.value ? "app.reloadModel" : "app.discard"),
            {
                confirmButtonText: t(connectedDesktop.value ? "app.reloadModel" : "app.discard"),
                cancelButtonText: t("app.cancel"),
                type: "warning",
            },
        )
        accepted = true
    } catch {
        accepted = false
    } finally {
        emit("dialog", false)
    }
    await nextTick()
    if (accepted && !props.locked && props.context?.targetId === current.targetId && props.context.project.baselineId === current.project.baselineId)
        emit("discard")
}
</script>
<template>
    <div v-if="pending" class="configuration-recovery">
        <div class="scaffold-toolbar">
            <el-button v-if="connectedDesktop" type="primary" :disabled="locked" @click="discard">{{ t("app.reloadModel") }}</el-button>
            <template v-else>
                <el-button type="primary" :disabled="locked || unknown" @click="emit('complete')">{{ t("configuration.complete") }}</el-button>
                <el-button :disabled="locked || unknown" @click="discard">{{ t("app.discard") }}</el-button>
            </template>
        </div>
    </div>
</template>
