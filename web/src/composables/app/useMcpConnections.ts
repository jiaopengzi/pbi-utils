/**
 * FilePath    : pbi-utils\web\src\composables\app\useMcpConnections.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 管理 MCP 连接快照、静默刷新及桌面管理意图, 不推断进程退出.
 */
import { computed, onMounted, onUnmounted, ref, shallowRef } from "vue"
import type { BridgeClient, McpConnectionsSnapshot } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback, useOperationMessage } from "@/composables/ui/useFeedback"

/** bridge 读取权威快照并受理管理请求; 失败保留旧数据且禁止动作, 卸载取消并忽略迟到结果. */
export function useMcpConnections(bridge: BridgeClient) {
    const { t } = useI18n()
    const { showFeedback } = useFeedback()
    const snapshot = shallowRef<McpConnectionsSnapshot>()
    const fresh = ref(false)
    const refreshing = ref(false)
    const busy = ref(false)
    const disabled = computed(() => !fresh.value || refreshing.value || busy.value)
    let mounted = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const requests = new Set<string>()
    useOperationMessage(() => (busy.value ? { message: t("mcp.connectionsWorking") } : undefined))

    /** 保存按 id 的稳定列表; 相等条目复用对象, 不按客户端自报名称关联身份. */
    function accept(value: McpConnectionsSnapshot): void {
        const old = new Map(snapshot.value?.connections.map((item) => [item.id, item]))
        snapshot.value = {
            acceptingConnections: value.acceptingConnections,
            connections: value.connections.map((item) => {
                const previous = old.get(item.id)
                return previous && JSON.stringify(previous) === JSON.stringify(item) ? previous : item
            }),
        }
        fresh.value = true
    }

    /** 读取最新事实; silent 控制手动反馈, 读取失败不把空列表当成功, 轮询保持串行. */
    async function refresh(silent = true): Promise<void> {
        if (!mounted || refreshing.value || busy.value) return
        refreshing.value = true
        const request = bridge.request("mcp.connections", {})
        requests.add(request.id)
        try {
            const value = await request.promise
            if (!mounted) return
            accept(value)
            if (!silent) showFeedback({ message: t("mcp.connectionsRefreshed") })
        } catch (cause) {
            if (!mounted) return
            fresh.value = false
            if (!silent) showFeedback({ message: cause instanceof Error ? cause.message : t("mcp.connectionsFailed"), type: "error" })
        } finally {
            requests.delete(request.id)
            if (mounted) refreshing.value = false
        }
    }

    /** 发送明确管理意图; connectionId 指定连接, 缺省时切换接入; 受理不表示实际退出, 随后读取事实. */
    async function manage(connectionId?: string): Promise<void> {
        if (disabled.value || !snapshot.value) return
        busy.value = true
        const request = connectionId
            ? bridge.request("mcp.disconnect", { connectionId })
            : bridge.request("mcp.setAdmission", { acceptingConnections: !snapshot.value.acceptingConnections })
        requests.add(request.id)
        try {
            const value = await request.promise
            if (!mounted) return
            if ("connections" in value) accept(value)
            showFeedback({
                message: t(connectionId ? ("accepted" in value && value.accepted ? "mcp.disconnectAccepted" : "mcp.connectionGone") : "mcp.admissionUpdated"),
                type: "info",
            })
        } catch (cause) {
            if (!mounted) return
            fresh.value = false
            showFeedback({ message: cause instanceof Error ? cause.message : t("mcp.connectionsFailed"), type: "error" })
        } finally {
            requests.delete(request.id)
            if (mounted) busy.value = false
        }
        if (mounted) await refresh()
    }

    /** 页面可见时串行刷新, 隐藏时停止请求; 卸载清理唯一计时器. */
    async function poll(): Promise<void> {
        if (document.visibilityState !== "hidden") await refresh()
        if (mounted) timer = setTimeout(poll, 2000)
    }
    onMounted(poll)
    onUnmounted(() => {
        mounted = false
        clearTimeout(timer)
        for (const requestId of requests) void bridge.request("request.cancel", { requestId }).promise.catch(() => undefined)
    })
    return { snapshot, fresh, refreshing, busy, disabled, refresh, manage }
}
