<!--
 * FilePath    : pbi-utils\web\src\app\McpConnectionsPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 展示本安装 MCP 连接并提供排空断开与持久接入开关.
-->
<script setup lang="ts">
import { watch } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useMcpConnections } from "@/composables/app/useMcpConnections"

/** bridge 仅调用桌面管理白名单; busy 上报手动管理, 静默刷新不抢焦点或移除旧卡片. */
const props = defineProps<{ bridge: BridgeClient }>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const { t, locale } = useI18n()
const { snapshot, fresh, refreshing, busy, disabled, refresh, manage } = useMcpConnections(props.bridge)
watch(busy, (value) => emit("busy", value))

/** 将宿主时间按当前语言和系统本地时区展示; value 无效时返回统一未知值, 不修改原时间身份. */
function formatTime(value: string): string {
    const date = new Date(value)
    return Number.isNaN(date.getTime()) ? "-" : new Intl.DateTimeFormat(locale.value, { dateStyle: "medium", timeStyle: "medium" }).format(date)
}
</script>
<template>
    <section class="mcp-connections" :aria-label="t('mcp.connectionsTitle')" :aria-busy="busy">
        <div class="connections-toolbar">
            <h3>{{ t("mcp.connectionsTitle") }}</h3>
            <el-button :loading="refreshing" :disabled="busy || refreshing" @click="refresh(false)">{{ t("mcp.connectionsRefresh") }}</el-button>
            <el-button :disabled="disabled" @click="manage()">{{
                t(snapshot?.acceptingConnections === false ? "mcp.resumeAdmission" : "mcp.pauseAdmission")
            }}</el-button>
        </div>
        <p>{{ t("mcp.connectionsDescription") }}</p>
        <p role="status">
            {{
                fresh
                    ? t(snapshot?.acceptingConnections ? "mcp.acceptingStatus" : "mcp.pausedStatus", { count: snapshot?.connections.length ?? 0 })
                    : t("mcp.connectionsStale")
            }}
        </p>
        <p v-if="fresh && !snapshot?.connections.length">{{ t("mcp.connectionsEmpty") }}</p>
        <ul v-if="snapshot?.connections.length" class="connections-list">
            <li v-for="connection in snapshot.connections" :key="connection.id" :data-connection-id="connection.id">
                <div class="connection-heading">
                    <strong>{{ connection.clientName || "-" }} · {{ connection.clientVersion || "-" }}</strong>
                    <el-button :disabled="disabled || connection.state !== 'connected'" @click="manage(connection.id)">{{ t("mcp.disconnect") }}</el-button>
                </div>
                <dl>
                    <div>
                        <dt>{{ t("mcp.connectionPid") }}</dt>
                        <dd>{{ connection.processId }}</dd>
                    </div>
                    <div>
                        <dt>{{ t("mcp.processStarted") }}</dt>
                        <dd>
                            <time :datetime="formatTime(connection.processStartedAt) === '-' ? undefined : connection.processStartedAt">{{
                                formatTime(connection.processStartedAt)
                            }}</time>
                        </dd>
                    </div>
                    <div>
                        <dt>{{ t("mcp.connectedAt") }}</dt>
                        <dd>
                            <time :datetime="formatTime(connection.connectedAt) === '-' ? undefined : connection.connectedAt">{{
                                formatTime(connection.connectedAt)
                            }}</time>
                        </dd>
                    </div>
                    <div>
                        <dt>{{ t("mcp.connectionPermission") }}</dt>
                        <dd>{{ t(connection.allowWrite ? "mcp.canWrite" : "mcp.readOnly") }}</dd>
                    </div>
                    <div>
                        <dt>{{ t("mcp.connectionState") }}</dt>
                        <dd>{{ t(`mcp.state.${connection.state}`) }}</dd>
                    </div>
                    <div>
                        <dt>{{ t("mcp.activeRequests") }}</dt>
                        <dd>{{ connection.activeRequests }}</dd>
                    </div>
                </dl>
            </li>
        </ul>
    </section>
</template>
<style scoped lang="scss">
.mcp-connections {
    margin-top: 24px;
    border-top: 1px solid var(--el-border-color-light);
    padding-top: 18px;
}
.connections-toolbar,
.connection-heading {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 12px;
    h3,
    strong {
        margin: 0;
        margin-right: auto;
        min-width: 0;
        overflow-wrap: anywhere;
    }
}
.connections-list {
    display: grid;
    gap: 12px;
    padding: 0;
    list-style: none;
    li {
        padding: 16px;
        border: 1px solid var(--el-border-color-light);
        border-radius: 8px;
    }
}
dl {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 12px;
    margin-bottom: 0;
    div {
        min-width: 0;
    }
    dt {
        color: var(--muted);
    }
    dd {
        margin: 4px 0 0;
        overflow-wrap: anywhere;
    }
}
</style>
