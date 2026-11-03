<!--
 * FilePath    : pbi-utils\web\src\app\HostDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 统一展示宿主一次性业务确认, 保留宿主身份与迟到回复隔离.
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue"
import { BridgeClient, type HostDialogRequest } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import CodeBlock from "@/features/code/CodeBlock.vue"
import UiDialog from "@/ui/UiDialog.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"

const props = defineProps<{ bridge?: BridgeClient }>()
const emit = defineEmits<{ active: [value: boolean] }>()
const { t } = useI18n()
const dialog = shallowRef<HostDialogRequest>()
const submitting = ref(false)
const localError = ref("")
let mounted = true
const data = computed(() => dialog.value?.data ?? {})
const actions = computed(() => dialog.value?.actions ?? ["confirm"])
const messageArgs = computed(() => ({
    ...dialog.value?.messageArgs,
    modelName: String(data.value.modelName ?? dialog.value?.messageArgs?.modelName ?? ""),
}))
const details = computed(() =>
    Object.entries(data.value).filter(([key, value]) => ["modelName", "operation", "connectionKind"].includes(key) && typeof value === "string"),
)
const detailFields = computed(() =>
    typeof data.value.detail === "object" && data.value.detail
        ? Object.entries(data.value.detail).filter(([, value]) => typeof value === "string" || typeof value === "number")
        : [],
)
const changes = computed(() =>
    Array.isArray(data.value.changes) ? (data.value.changes as { objectName: string; property: string; before: string | null; after: string | null }[]) : [],
)

/** 清理关闭或卸载后的表单, 并释放全局导航锁. */
function reset() {
    dialog.value = undefined
    submitting.value = false
    localError.value = ""
    emit("active", false)
}
/** 按宿主新 ID 创建表单, 同 ID 重复事件不重置当前回复状态. */
function open(request: HostDialogRequest) {
    if (dialog.value?.id === request.id) return
    reset()
    dialog.value = request
    emit("active", true)
}
/** 仅处理当前弹窗的权威关闭通知, 忽略旧弹窗迟到的关闭. */
function closed(id: string) {
    if (dialog.value?.id === id) reset()
}
watch(
    () => props.bridge,
    (bridge, _previous, cleanup) => {
        reset()
        if (!bridge) return
        const stopDialog = bridge.onDialog(open)
        const stopClosed = bridge.onDialogClosed(closed)
        cleanup(() => {
            stopDialog()
            stopClosed()
            reset()
        })
    },
    { immediate: true },
)
onBeforeUnmount(() => {
    mounted = false
    reset()
})

/** 只提交当前宿主声明的动作, 不附加表单值; 关闭由宿主结果裁决. */
async function respond(action: string) {
    const current = dialog.value
    if (!current || !props.bridge || submitting.value || (action !== "cancel" && !actions.value.includes(action))) return
    submitting.value = true
    localError.value = ""
    try {
        const request = props.bridge.request("ui.dialogRespond", { dialogId: current.id, action, values: {} })
        const result = await request.promise
        if (mounted && dialog.value?.id === current.id && !result.accepted) {
            reset()
        }
    } catch {
        if (mounted && dialog.value?.id === current.id) localError.value = t("hostDialog.respondFailed")
    } finally {
        if (mounted && dialog.value?.id === current.id && localError.value) submitting.value = false
    }
}
/** 关闭图标与 Escape 统一发送取消, 不能仅在网页隐藏未完成的宿主确认. */
function changeVisible(value: boolean) {
    if (!value) void respond("cancel")
}
/** 翻译已知操作类型, 宿主未识别的业务值保持原文. */
function operationLabel(value: unknown): string {
    const key = `hostDialog.operation.${String(value)}`
    const translated = t(key)
    return translated === key ? String(value) : translated
}
useMessageNotices("host-dialog", () => [
    !!dialog.value && !!data.value.isSimulation && { key: "simulation", once: true, message: t("hostDialog.simulation"), type: "info" },
    !!dialog.value && !!(localError.value || data.value.error) && { key: "error", message: localError.value || String(data.value.error), type: "error" },
    !!dialog.value && !!data.value.notice && { key: "notice", message: t(String(data.value.notice)), type: "success" },
])
</script>
<template>
    <UiDialog
        :model-value="!!dialog"
        :title="dialog ? t(dialog.titleKey, messageArgs) : ''"
        :busy="submitting"
        class="host-dialog"
        @update:model-value="changeVisible"
    >
        <template v-if="dialog">
            <p v-if="dialog.messageKey" class="host-dialog__message">{{ t(dialog.messageKey, messageArgs) }}</p>

            <dl v-if="details.length || detailFields.length" class="host-dialog__details">
                <template v-for="[key, value] in details" :key="key"
                    ><dt>{{ t(`hostDialog.${key}`) }}</dt>
                    <dd>{{ key === "operation" ? operationLabel(value) : value }}</dd></template
                ><template v-for="[key, value] in detailFields" :key="key"
                    ><dt>{{ t(`hostDialog.${key === "deleted" ? "deletedCount" : key}`) }}</dt>
                    <dd>
                        {{ key === "calendarNaming" ? t(`hostDialog.calendarNaming.${value}`) : value }}
                    </dd></template
                >
            </dl>
            <section v-if="dialog.kind === 'tool'" class="host-dialog__changes">
                <article v-for="(change, index) in changes" :key="index" class="host-dialog__change">
                    <h3>
                        {{ change.objectName }} <span>· {{ change.property }}</span>
                    </h3>
                    <h4>{{ t("hostDialog.before") }}</h4>
                    <CodeBlock v-if="change.before !== null" :code="change.before" :language="change.property === 'expression' ? 'dax' : 'text'" />
                    <p v-else>{{ t("hostDialog.null") }}</p>
                    <h4>{{ t("hostDialog.after") }}</h4>
                    <CodeBlock v-if="change.after !== null" :code="change.after" :language="change.property === 'expression' ? 'dax' : 'text'" />
                    <p v-else>{{ t("hostDialog.null") }}</p>
                </article>
            </section>
        </template>
        <template #footer>
            <el-button v-if="dialog?.kind !== 'notice'" :disabled="submitting" @click="respond('cancel')">{{ t("hostDialog.cancel") }}</el-button>
            <el-button v-if="actions.includes('confirm')" :disabled="submitting" :loading="submitting" type="primary" @click="respond('confirm')">{{
                t("hostDialog.confirm")
            }}</el-button>
        </template>
    </UiDialog>
</template>
<style scoped lang="scss">
.host-dialog__message {
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    margin: 0 0 24px;
    line-height: 1.7;
}
.host-dialog__details {
    display: grid;
    grid-template-columns: minmax(0, auto) minmax(0, 1fr);
    gap: 8px 20px;
}
.host-dialog__details dt {
    color: var(--muted);
}
.host-dialog__details dd {
    margin: 0;
    overflow-wrap: anywhere;
}
.host-dialog__changes {
    max-height: 48vh;
    overflow: auto;
    padding-right: 8px;
}
.host-dialog__change {
    border-top: 1px solid var(--border);
    margin-top: 20px;
    padding-top: 12px;
}
.host-dialog__change h3 {
    font-size: 15px;
    overflow-wrap: anywhere;
}
.host-dialog__change h3 span {
    font-weight: 400;
    color: var(--muted);
}
.host-dialog__change h4 {
    font-size: 12px;
    margin: 16px 0 8px;
}
</style>
