/**
 * FilePath    : pbi-utils\web\src\composables\app\useRemoteConnection.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 一次性远程连接上下文、认证目标与秘密清理生命周期.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, shallowRef, watch, type TemplateRef } from "vue"
import { BridgeClient, BridgeError, type Methods, type ProjectSnapshot, type RemoteConnectionForm, type RemoteConnectionValues } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"

export type RemoteConnectionProps = { bridge: BridgeClient; disabled?: boolean; operationLocked?: boolean }
export type RemoteConnectionEmit = { (event: "connected", ...args: [value: ProjectSnapshot]): void; (event: "busy", ...args: [value: boolean]): void }

/** 管理一次性远程连接上下文、认证目标与秘密清理生命周期; props 为当前响应式输入, emit 转发权威结果, formRoot 为视图提供的只读根节点引用, 用于提交及卸载前清空真实密码输入, 返回视图绑定与操作; 请求失败保留原错误和输入. */
export function useRemoteConnection(props: RemoteConnectionProps, emit: RemoteConnectionEmit, formRoot: TemplateRef<HTMLElement>) {
    const { t, hostText } = useI18n()
    const { showFeedback } = useFeedback()
    const context = shallowRef<RemoteConnectionForm>()
    const form = reactive<RemoteConnectionValues>({
        server: "",
        database: "",
        authenticationMode: "current",
        userName: "",
        password: "",
        remember: true,
        rememberPassword: false,
        recentId: "",
    })
    const loading = ref(false)
    const connecting = ref(false)
    const cancelling = ref(false)
    const error = ref("")
    const requestId = ref<string>()
    const recent = computed(() => context.value?.recent ?? [])
    const selectedRecent = computed(() => recent.value.find((item) => item.id === form.recentId))
    const xmla = computed(() => form.server.includes("://"))
    const explicitWindows = computed(() => !xmla.value && form.authenticationMode === "windows")
    const blocked = computed(() => loading.value || props.disabled === true)
    const preserveAppearance = computed(() => (loading.value || props.operationLocked) && (!props.disabled || props.operationLocked))
    const validValues = computed(
        () =>
            !!form.server.trim() &&
            !!form.database.trim() &&
            (!explicitWindows.value || (!!form.userName.trim() && (!!form.password || !!selectedRecent.value?.hasSavedPassword))),
    )
    const canSubmit = computed(() => !!context.value?.formId && validValues.value)
    let filling = false
    let mounted = true

    useOperationMessage(() =>
        connecting.value
            ? {
                  message: t("connection.connectingRemote"),
                  cancel: () => void cancel(),
                  cancelLabel: t("connection.cancel"),
                  cancelDisabled: cancelling.value || !requestId.value,
              }
            : undefined,
    )

    /** 清理状态和实际输入节点; 历史密码始终只在后端读取. */
    function clearSecret() {
        form.password = ""
        for (const input of formRoot.value?.querySelectorAll<HTMLInputElement>('input[type="password"]') ?? []) input.value = ""
    }
    /** XMLA 仅使用当前身份, 不带入指定账号; 关闭记住连接时同步撤销保存密码. */
    function enforceAuthentication() {
        if (xmla.value) form.authenticationMode = "current"
        if (!explicitWindows.value) {
            form.userName = ""
            clearSecret()
        }
        if (!explicitWindows.value || !form.remember) form.rememberPassword = false
    }
    /** 只回填非秘密资料, 每次选择清理新输入密码. */
    function selectRecent(id: string) {
        const item = recent.value.find((entry) => entry.id === id)
        filling = true
        Object.assign(form, {
            recentId: item?.id ?? "",
            server: item?.server ?? "",
            database: item?.database ?? "",
            authenticationMode: item?.authenticationMode ?? "current",
            userName: item?.userName ?? "",
            rememberPassword: !!item?.hasSavedPassword,
        })
        clearSecret()
        filling = false
        enforceAuthentication()
    }
    watch(
        [() => form.server, () => form.database, () => form.authenticationMode, () => form.userName],
        () => {
            if (filling) return
            clearSecret()
            const item = selectedRecent.value
            if (
                item &&
                (form.server.trim().toLowerCase() !== item.server.toLowerCase() ||
                    form.database.trim() !== item.database ||
                    form.authenticationMode !== item.authenticationMode ||
                    form.userName.trim().toLowerCase() !== (item.userName ?? "").toLowerCase())
            ) {
                form.recentId = ""
                form.rememberPassword = false
            }
            enforceAuthentication()
        },
        { flush: "sync" },
    )
    watch(() => form.remember, enforceAuthentication)

    /** 每次请求使用独立标识, 取消通知不会提前解锁或重放原连接. */
    async function call<M extends "remote.form" | "remote.mutate" | "project.connectRemote">(method: M, params: Methods[M]["params"]) {
        const request = props.bridge.request(method, params)
        requestId.value = request.id
        return await request.promise
    }
    /** 仅在真实读写请求期间锁定来源与导航, 空闲编辑不占用桥接请求. */
    function setBusy(value: boolean) {
        loading.value = value
        emit("busy", value)
    }
    /** 替换一次性上下文但保留非秘密输入; 失效历史不再允许使用已保存密码. */
    function applyContext(value: RemoteConnectionForm, initial = false) {
        context.value = value
        clearSecret()
        if (initial && value.recent[0]) selectRecent(value.recent[0].id)
        else if (form.recentId && !selectedRecent.value) {
            form.recentId = ""
            form.rememberPassword = false
        } else if (selectedRecent.value && !selectedRecent.value.hasSavedPassword) form.rememberPassword = false
    }
    /** 初始化或显式重试只读取非秘密元数据, 不触发连接或弹窗. */
    async function loadForm() {
        if (blocked.value) return
        setBusy(true)
        error.value = ""
        try {
            const value = await call("remote.form", {})
            if (mounted) applyContext(value, !context.value)
        } catch (failure) {
            if (mounted) error.value = failure instanceof Error ? failure.message : t("connection.failed")
        } finally {
            requestId.value = undefined
            if (mounted) setBusy(false)
        }
    }
    /** 删除或清密码只能使用本次展示记录的 ID, 不携带账号、目标或秘密. */
    async function mutate(action: "delete" | "clearPassword") {
        const current = context.value
        if (blocked.value || !current?.formId || !selectedRecent.value || (action === "clearPassword" && !selectedRecent.value.hasSavedPassword)) return
        setBusy(true)
        error.value = ""
        clearSecret()
        try {
            const value = await call("remote.mutate", { formId: current.formId, action, recordId: form.recentId })
            if (mounted) {
                applyContext(value)
                showFeedback({ message: t(action === "delete" ? "hostDialog.deleted" : "hostDialog.passwordCleared"), type: "success" })
            }
        } catch (failure) {
            if (mounted) error.value = failure instanceof Error ? failure.message : t("connection.failed")
            // 失败的变更也不能继续复用已经消费的上下文; 只重新读取, 不重复业务动作.
            context.value = { ...current, formId: "" }
        } finally {
            requestId.value = undefined
            if (mounted) setBusy(false)
        }
    }
    /** 连接只提交一次; 立即清秘密, 所有终态后重新取上下文供下一次手工操作. */
    async function connect() {
        if (blocked.value || !canSubmit.value || !context.value) return
        const formId = context.value.formId
        const values = { ...form, server: form.server.trim(), database: form.database.trim(), userName: form.userName.trim() }
        context.value = { ...context.value, formId: "" }
        setBusy(true)
        connecting.value = true
        error.value = ""
        cancelling.value = false
        try {
            const request = call("project.connectRemote", { formId, values })
            clearSecret()
            const snapshot = await request
            if (mounted) emit("connected", snapshot)
        } catch (failure) {
            if (mounted) {
                const message = failure instanceof Error ? failure.message : t("connection.failed")
                if (failure instanceof BridgeError && failure.code === "CANCELLED") showFeedback({ message, type: "info" })
                else error.value = message
            }
        } finally {
            clearSecret()
            connecting.value = false
            cancelling.value = false
            if (mounted) {
                try {
                    const value = await call("remote.form", {})
                    if (mounted) applyContext(value)
                } catch (failure) {
                    if (mounted) error.value = failure instanceof Error ? failure.message : t("connection.failed")
                }
            }
            requestId.value = undefined
            if (mounted) setBusy(false)
        }
    }
    /** 取消仅发送一次并等待原连接终态; 失败允许重试取消, 保留真实状态. */
    async function cancel() {
        const id = requestId.value
        if (!id || cancelling.value) return
        cancelling.value = true
        try {
            await props.bridge.request("request.cancel", { requestId: id }).promise
        } catch (failure) {
            if (mounted && requestId.value === id) {
                cancelling.value = false
                error.value = failure instanceof Error ? failure.message : t("connection.cancelFailed")
            }
        }
    }
    onMounted(() => void loadForm())
    onBeforeUnmount(() => {
        mounted = false
        clearSecret()
        void cancel()
    })
    return {
        context,
        form,
        loading,
        connecting,
        error,
        recent,
        selectedRecent,
        xmla,
        explicitWindows,
        blocked,
        preserveAppearance,
        validValues,
        canSubmit,
        selectRecent,
        loadForm,
        mutate,
        connect,
        clearSecret,
        t,
        hostText,
    }
}
