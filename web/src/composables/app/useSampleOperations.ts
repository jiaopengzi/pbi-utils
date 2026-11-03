/**
 * FilePath    : pbi-utils\web\src\composables\app\useSampleOperations.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 示例参数与宿主目录的生成、统计、提交进度及取消生命周期.
 */
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, shallowRef } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import { sampleText } from "@/i18n/sampleMessages"
import type { OperationEvent, BridgeClient, BridgePhase, SampleDataState } from "@/bridge/client"
import { BridgeError } from "@/bridge/client"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"
import { defaultOptions, followingDate, formatSize, optionsError } from "@/features/sample/presentation"

export type SampleOperationsProps = { bridge: BridgeClient }
export type SampleOperationsEmit = { (event: "busy", ...args: [value: boolean]): void }

/** 限制日期选择器为合法四位年份, date 为候选日历日期. */
function disabledDate(date: Date): boolean {
    return date.getFullYear() < 1 || date.getFullYear() > 9999
}

/** 管理示例参数与宿主目录的生成、统计、提交进度及取消生命周期; props 为当前响应式输入, emit 转发权威结果, 返回视图绑定与操作; 请求失败保留原错误和输入. */
export function useSampleOperations(props: SampleOperationsProps, emit: SampleOperationsEmit) {
    const { locale, hostText } = useI18n()
    const { showFeedback } = useFeedback()
    const options = reactive(defaultOptions())
    const text = computed(() => sampleText[locale.value])
    const state = shallowRef<SampleDataState>()
    const busy = ref(false)
    const active = ref("")
    const error = ref("")
    const percent = ref(0)
    const stage = ref("")
    const progressVisible = ref(false)
    const committed = ref(false)
    const cancelling = ref(false)
    const nextDate = computed(() => (state.value?.lastFactDate ? followingDate(state.value.lastFactDate) : ""))
    const parameterError = computed(() => optionsError(options))
    const canGenerate = computed(() => !busy.value && !!state.value?.outputPath && !parameterError.value)
    const stageText = computed(() => text.value[stage.value as keyof typeof text.value] ?? text.value.committing)
    const shortcuts = computed(() => [
        { text: text.value.today, value: () => new Date() },
        { text: text.value.yearStart, value: () => new Date(new Date().getFullYear(), 0, 1) },
        { text: text.value.monthStart, value: () => new Date(new Date().getFullYear(), new Date().getMonth(), 1) },
        { text: text.value.monthEnd, value: () => new Date(new Date().getFullYear(), new Date().getMonth() + 1, 0) },
        { text: text.value.yearEnd, value: () => new Date(new Date().getFullYear(), 11, 31) },
    ])
    const requestId = ref("")
    let disposed = false

    // 在独立浮层更新阶段与百分比, 取消入口随宿主提交边界收敛, 不在正文插入进度节点.
    useOperationMessage(() =>
        progressVisible.value
            ? {
                  message: stageText.value,
                  progress: Math.round(percent.value),
                  cancel: busy.value && requestId.value ? cancel : undefined,
                  cancelLabel: text.value.cancel,
                  cancelDisabled: cancelling.value || committed.value,
              }
            : busy.value && active.value === "inspect"
              ? {
                    message: text.value.loadingExisting,
                    cancel: requestId.value ? cancel : undefined,
                    cancelLabel: text.value.cancel,
                    cancelDisabled: cancelling.value,
                }
              : busy.value && ["choose", "clear", "open"].includes(active.value)
                ? { message: text.value[active.value as "choose" | "clear" | "open"] ?? text.value.stageSummary }
                : undefined,
    )

    /** 设置单次操作状态, value 为操作名称, 空值表示结束, 通知父组件防止操作期间切换页面. */
    function setBusy(value: string): void {
        if (value) {
            progressVisible.value = false
            committed.value = false
            cancelling.value = false
        }
        active.value = value
        busy.value = !!value
        emit("busy", busy.value)
    }
    /** 展示边界错误, cause 为宿主或传输异常; 用户取消不显示操作失败. */
    function fail(cause: unknown): void {
        if (!(cause instanceof BridgeError && cause.code === "CANCELLED")) error.value = cause instanceof Error ? cause.message : text.value.operationError
    }
    /** 读取当前原生绑定目录的统计, choose 表示先弹原生目录选择, 失败保留上一份快照. */
    async function inspect(choose = false): Promise<void> {
        if (busy.value) return
        setBusy(choose ? "choose" : "inspect")
        error.value = ""
        try {
            const request = props.bridge.request(choose ? "sample.chooseDirectory" : "sample.status", {})
            requestId.value = request.id
            const result = await request.promise
            if (!disposed) state.value = result
            if (choose && !disposed) {
                // 先保留原生选择的目录, 再读取统计; 损坏 CSV 不应阻止清空或重新生成.
                active.value = "inspect"
                const statistics = props.bridge.request("sample.status", {})
                requestId.value = statistics.id
                const snapshot = await statistics.promise
                if (!disposed) state.value = snapshot
            }
        } catch (cause) {
            fail(cause)
        } finally {
            requestId.value = ""
            setBusy("")
        }
    }
    /** 按请求 ID 接收有序进度, item 不可信时忽略; 成功响应渲染前最多展示 99%, 不接受事件提前宣布完成. */
    function onProgress(item: OperationEvent): void {
        if (disposed || item.type !== "sampleProgress" || !item.payload || typeof item.payload !== "object") return
        const payload = item.payload as { percent?: unknown; stage?: unknown }
        if (payload.stage === "stageDone") return
        if (typeof payload.percent === "number" && Number.isFinite(payload.percent))
            percent.value = Math.max(percent.value, Math.min(99, Math.max(0, payload.percent)))
        if (
            typeof payload.stage === "string" &&
            ["stageStart", "stageDimensions", "stageProducts", "stageStores", "stageCustomers", "stageOrders", "stagePbip", "stageSummary"].includes(
                payload.stage,
            )
        )
            stage.value = payload.stage
    }
    /** 仅提交阶段禁用取消, phase 来自宿主固定阶段, 不凭进度百分比推断可取消性. */
    function onPhase(phase: BridgePhase): void {
        if (phase === "committing") committed.value = true
    }
    /** 提交全量或增量参数, mode 表示业务模式; 权威结果渲染后结束唯一进度并显示最终提示, 失败后回读真实产物状态. */
    async function generate(mode: "full" | "incremental"): Promise<void> {
        if (!canGenerate.value) return
        if (mode === "incremental" && !state.value?.hasData) {
            error.value = text.value.noBase
            return
        }
        if (mode === "incremental" && nextDate.value && options.startDate !== nextDate.value) {
            error.value = `${text.value.nextDate} ${nextDate.value}`
            return
        }
        setBusy(mode)
        error.value = ""
        percent.value = 0
        stage.value = "stageStart"
        progressVisible.value = true
        committed.value = false
        cancelling.value = false
        try {
            const request = props.bridge.request("sample.generate", { ...options, locale: locale.value, mode }, onProgress, onPhase)
            requestId.value = request.id
            const result = await request.promise
            if (disposed) return
            state.value = result.state
            await nextTick()
            if (disposed) return
            percent.value = 100
            stage.value = "stageDone"
            // 同一进度只在权威结果渲染后到达 100%, 关闭后再展示最终纯文本, 不定时重复留存完成态.
            await nextTick()
            progressVisible.value = false
            await nextTick()
            if (disposed) return
            showFeedback({
                message: state.value?.isSimulation ? text.value.simulation : text.value[mode === "full" ? "generated" : "updated"],
                type: state.value?.isSimulation ? "info" : "success",
            })
        } catch (cause) {
            progressVisible.value = false
            fail(cause)
            try {
                const result = await props.bridge.request("sample.status", {}).promise
                if (!disposed) state.value = result
            } catch {
                /* 保留首次错误, 不以统计失败覆盖生成失败的事实. */
            }
        } finally {
            requestId.value = ""
            committed.value = false
            cancelling.value = false
            setBusy("")
        }
    }
    /** 清空当前宿主绑定目录的受管产物, 独立确认被取消时保留统计和目录. */
    async function clear(): Promise<void> {
        if (busy.value || !state.value?.hasData) return
        setBusy("clear")
        error.value = ""
        try {
            const request = props.bridge.request("sample.clear", {}, undefined, onPhase)
            requestId.value = request.id
            const result = await request.promise
            if (!disposed) {
                state.value = result
                showFeedback({
                    message: result.isSimulation ? text.value.simulation : text.value.cleared,
                    type: result.isSimulation ? "info" : "success",
                })
                progressVisible.value = false
            }
        } catch (cause) {
            fail(cause)
        } finally {
            requestId.value = ""
            committed.value = false
            setBusy("")
        }
    }
    /** 打开原生绑定的存放目录, 不向宿主提交路径; 模拟预览返回 opened=false. */
    async function openDirectory(): Promise<void> {
        if (busy.value || !state.value?.outputPath) return
        setBusy("open")
        error.value = ""
        try {
            await props.bridge.request("sample.openDirectory", {}).promise
        } catch (cause) {
            fail(cause)
        } finally {
            setBusy("")
        }
    }
    /** 请求取消提交前的生成, accepted=false 时继续等待真实终态, 不擅自标记取消成功. */
    async function cancel(): Promise<void> {
        if (!requestId.value || committed.value || cancelling.value) return
        cancelling.value = true
        try {
            const result = await props.bridge.request("request.cancel", { requestId: requestId.value }).promise
            if (!result.accepted) cancelling.value = false
        } catch (cause) {
            cancelling.value = false
            fail(cause)
        }
    }
    /** 根据宿主截止日填入增量起点, 不改结束日期; 若当前结束日期不足, 表单继续提示用户修正. */
    function useNextDate(): void {
        if (!busy.value && nextDate.value) {
            options.startDate = nextDate.value
            error.value = ""
        }
    }
    onMounted(() => {
        void inspect()
    })
    onUnmounted(() => {
        disposed = true
        void cancel()
    })
    return {
        options,
        text,
        state,
        busy,
        active,
        error,
        nextDate,
        parameterError,
        canGenerate,
        shortcuts,
        inspect,
        generate,
        clear,
        openDirectory,
        useNextDate,
        disabledDate,
        locale,
        hostText,
        formatSize,
    }
}
