<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\CalendarCodePreview.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 只读展示宿主生成的日期函数与调用, 独立取消排空并拒绝过期结果.
-->

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue"
import { BridgeError, type BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback } from "@/composables/ui/useFeedback"
import CodeEditor from "../code/CodeEditor.vue"
import { validateConfiguration } from "./validation"
import type { CalendarCodeProgram, CalendarPreviewContext, ScaffoldCalendarOptions, ScaffoldMethods } from "./contracts"

const props = defineProps<{
    bridge: BridgeClient
    context?: CalendarPreviewContext
    calendar: ScaffoldCalendarOptions
    enabled: boolean
    disabled: boolean
    previewInvalidationKey?: string
}>()
const { t, locale, hostText } = useI18n()
const { showFeedback } = useFeedback()
const program = shallowRef<CalendarCodeProgram>()
const preparing = ref(false)
const error = ref("")
const problem = computed(() => validateConfiguration({ calendar: props.calendar, periods: null, sample: null, measures: null, udfs: null }))
const source = computed(() =>
    JSON.stringify({ context: props.context, calendar: props.calendar, locale: locale.value, disabled: props.disabled, sample: props.previewInvalidationKey }),
)
const message = computed(() => {
    if (!props.context) return t("scaffold.calendarCodeContextMissing")
    if (problem.value) return t("scaffold.calendarCodeInvalid", { message: t(problem.value.key, problem.value.params) })
    if (props.disabled) return t("scaffold.calendarCodePaused")
    if (error.value) return hostText(error.value)
    return preparing.value ? t("scaffold.calendarCodePreparing") : ""
})
type Payload = ScaffoldMethods["calendar.preview"]["params"]
type Pending = { generation: number; source: string; payload: Payload }
type Flight = Pending & { id: string; cancelRequested: boolean }
let disposed = false,
    generation = 0,
    timer: ReturnType<typeof setTimeout> | undefined,
    queued: Pending | undefined,
    active: Flight | undefined

/** 取消仅指向本组件的预览 ID, 接受后仍等待原请求终态, 不影响创建或应用. */
function cancelActive(): void {
    if (!active || active.cancelRequested) return
    active.cancelRequested = true
    void props.bridge.request("request.cancel", { requestId: active.id }).promise.catch(() => undefined)
}

/** 严格核验当前基线及完整原文上限, 超额结果拒绝展示而不截断. */
function validProgram(value: CalendarCodeProgram, flight: Flight): boolean {
    if (!value || typeof value !== "object") return false
    const baseline = flight.payload.context.kind === "template" ? flight.payload.context.templateBaseline : flight.payload.context.baselineId
    if (
        typeof value.functionName !== "string" ||
        !value.functionName ||
        value.functionName.length > 128 ||
        typeof value.functionExpression !== "string" ||
        !value.functionExpression ||
        typeof value.invocationExpression !== "string" ||
        !value.invocationExpression ||
        typeof value.inputDigest !== "string" ||
        !/^[a-f0-9]{64}$/i.test(value.inputDigest) ||
        value.contextBaseline !== baseline ||
        typeof value.generatorVersion !== "string" ||
        !value.generatorVersion ||
        value.generatorVersion.length > 128
    )
        return false
    const encoder = new TextEncoder()
    // 完整响应的实际 id/代际/转义字节由 BridgeClient 校验; 此处只核两段业务原文的合计预算.
    return encoder.encode(value.functionExpression).length + encoder.encode(value.invocationExpression).length <= 3 * 1024 * 1024
}

/** 仅发送最新待处理输入, 前一个请求排空前不发第二个预览. */
async function pump(): Promise<void> {
    if (disposed || active || timer !== undefined || !queued) return
    const next = queued
    queued = undefined
    let flight: Flight | undefined
    try {
        const pending = props.bridge.request("calendar.preview", next.payload)
        flight = { ...next, id: pending.id, cancelRequested: false }
        active = flight
        const value = await pending.promise
        if (disposed || next.generation !== generation || next.source !== source.value) return
        if (!validProgram(value, flight)) throw new Error(t("scaffold.calendarCodeInvalidResponse"))
        program.value = value
    } catch (cause) {
        if (!disposed && next.generation === generation && next.source === source.value && !(cause instanceof BridgeError && cause.code === "CANCELLED"))
            error.value = cause instanceof Error ? cause.message : t("scaffold.calendarCodeFailed")
    } finally {
        if (active === flight) active = undefined
        if (!disposed && next.generation === generation) preparing.value = false
        if (!disposed && queued && timer === undefined) void pump()
    }
}

/** 输入变化立即撤销复制资格, 防抖只延后计算而不延后失效. */
function refresh(): void {
    generation++
    program.value = undefined
    error.value = ""
    preparing.value = false
    queued = undefined
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
    cancelActive()
    if (disposed || !props.context || props.disabled || problem.value) return
    const payload = JSON.parse(JSON.stringify({ context: props.context, calendar: props.calendar, locale: locale.value })) as Payload
    queued = { generation, source: source.value, payload }
    preparing.value = true
    timer = setTimeout(() => {
        timer = undefined
        void pump()
    }, 250)
}

/** 只复制当前有效的宿主原文, 复制失败保留可手动选择的只读编辑器. */
async function copy(kind: "function" | "invocation"): Promise<void> {
    const current = program.value
    if (!current || disposed || props.disabled || problem.value || !props.context) return
    try {
        await navigator.clipboard.writeText(kind === "function" ? current.functionExpression : current.invocationExpression)
        if (!disposed && current === program.value) showFeedback({ message: t("model.copied") })
    } catch {
        if (!disposed && current === program.value) showFeedback({ message: t("model.copyFailed"), type: "error" })
    }
}
watch(source, refresh, { immediate: true, flush: "sync" })
onBeforeUnmount(() => {
    disposed = true
    generation++
    queued = undefined
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
    cancelActive()
    program.value = undefined
})
</script>

<template>
    <section class="calendar-code-preview">
        <h3>{{ t("scaffold.calendarCode") }}</h3>
        <p class="muted">{{ t("scaffold.calendarCodeHint") }}</p>
        <p v-if="!enabled" role="status" class="muted">{{ t("scaffold.calendarCodeDisabled") }}</p>
        <p v-if="message" role="status" aria-live="polite">{{ message }}</p>
        <el-button v-if="error && context && !problem && !disabled" @click="refresh">{{ t("scaffold.calendarCodeRetry") }}</el-button>
        <template v-if="program">
            <p v-if="program.isSimulation" role="status" class="muted">{{ t("scaffold.simulation") }}</p>
            <div class="scaffold-toolbar">
                <h4>{{ t("scaffold.calendarFunctionCode") }} · {{ program.functionName }}</h4>
                <el-button data-calendar-copy="function" @click="copy('function')">{{ t("model.copy") }}</el-button>
            </div>
            <CodeEditor :model-value="program.functionExpression" language="m" :label="t('scaffold.calendarFunctionCode')" readonly :rows="14" />
            <div class="scaffold-toolbar">
                <h4>{{ t("scaffold.calendarInvocationCode") }}</h4>
                <el-button data-calendar-copy="invocation" @click="copy('invocation')">{{ t("model.copy") }}</el-button>
            </div>
            <CodeEditor :model-value="program.invocationExpression" language="m" :label="t('scaffold.calendarInvocationCode')" readonly :rows="7" />
        </template>
    </section>
</template>

<style scoped lang="scss">
.calendar-code-preview {
    min-width: 0;
    margin-block: 24px;
    h4 {
        margin: 0;
        overflow-wrap: anywhere;
    }
}
</style>
