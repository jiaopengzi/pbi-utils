<!--
 * FilePath    : pbi-utils\web\src\app\SettingsWorkspace.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 平铺设置分区并保持各面板挂载, 聚合业务导航锁并支持搜索定位.
-->
<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { scrollToWorkspaceSection } from "@/ui/scrollToWorkspaceSection"
import UiSettingsPanel from "./UiSettingsPanel.vue"
import LoggingPanel from "./LoggingPanel.vue"
import DataResetPanel from "./DataResetPanel.vue"
import McpSettingsPanel from "./McpSettingsPanel.vue"
import EnvironmentPanel from "./EnvironmentPanel.vue"
import SoftwareUpdatePanel from "./SoftwareUpdatePanel.vue"
import type { ProductUpdates } from "@/composables/app/useProductUpdates"

/** 组装全部设置分区; bridge 访问宿主, initialTab 与 navigationId 定位本次搜索目标; 请求失败由所属面板反馈. */
const props = defineProps<{ bridge: BridgeClient; updates?: ProductUpdates; initialTab?: string; navigationId?: number }>()
const emit = defineEmits<{ busy: [value: boolean] }>()
const { t } = useI18n()
const activity = ref<Record<string, boolean>>({})
const busy = computed(() => Object.values(activity.value).some(Boolean))
const sectionKeys = new Set(["general", "updates", "mcp", "environment", "data"])
let pendingNavigation = false
// 重置面板自己的确认锁只向外传播, 不反向禁止它的清空确认操作.
const resetDisabled = computed(() => Object.entries(activity.value).some(([section, active]) => section !== "reset" && active))
const workspace = ref<HTMLElement>()
let mounted = true
watch(busy, (value) => {
    emit("busy", value)
    if (!value && pendingNavigation) void focusSection()
})
watch([() => props.initialTab, () => props.navigationId], focusSection)

/** 合并 section 对应面板的 value 在途状态, 防止一个面板完成时提前解除其他操作的导航限制. */
function updateBusy(section: "language" | "logging" | "mcp" | "reset", value: boolean): void {
    activity.value = { ...activity.value, [section]: value }
}

/** 将搜索目标分区滚到顶栏下; 在途延后执行最新目标, 卸载后不滚动, 不重新挂载面板. */
async function focusSection(): Promise<void> {
    const section = props.initialTab
    if (!section || !sectionKeys.has(section)) {
        pendingNavigation = false
        return
    }
    pendingNavigation = true
    if (busy.value) return
    await nextTick()
    if (!mounted || props.initialTab !== section || busy.value) return
    pendingNavigation = false
    scrollToWorkspaceSection(workspace.value?.querySelector<HTMLElement>(`[data-settings-section="${section}"]`) ?? undefined)
}
onMounted(focusSection)
onUnmounted(() => {
    mounted = false
})
</script>
<template>
    <section ref="workspace" class="settings-workspace" :aria-label="t('app.settings')" :aria-busy="busy">
        <section data-settings-section="general" class="settings-section general-settings" :aria-label="t('settings.general')">
            <UiSettingsPanel :bridge="bridge" @busy="updateBusy('language', $event)" />
            <LoggingPanel :bridge="bridge" @busy="updateBusy('logging', $event)" />
        </section>
        <section data-settings-section="updates" class="settings-section" :aria-label="t('updates.title')">
            <SoftwareUpdatePanel :bridge="bridge" :updates="updates" />
        </section>
        <section data-settings-section="mcp" class="settings-section" :aria-label="t('mcp.title')">
            <McpSettingsPanel :bridge="bridge" @busy="updateBusy('mcp', $event)" />
        </section>
        <section data-settings-section="environment" class="settings-section" :aria-label="t('diagnostic.heading')">
            <EnvironmentPanel :bridge="bridge" />
        </section>
        <section data-settings-section="data" class="settings-section" :aria-label="t('dataReset.title')">
            <DataResetPanel :bridge="bridge" :disabled="resetDisabled" @busy="updateBusy('reset', $event)" />
        </section>
    </section>
</template>
<style scoped lang="scss">
.settings-workspace,
.settings-section {
    display: grid;
    min-width: 0;
    gap: 18px;
}
.settings-section {
    scroll-margin-block-start: 18px;
    :deep(.panel) {
        margin-bottom: 0;
    }
}
.general-settings :deep(.logging-panel) {
    margin-top: 0;
}
</style>
