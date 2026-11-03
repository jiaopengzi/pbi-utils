<!--
 * FilePath    : pbi-utils\web\src\app\SettingsWorkspace.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 纵向组织本地 MCP、常规、软件更新、运行环境与本地数据设置, 聚合业务导航锁并支持搜索定位.
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
// 重置面板自己的确认锁只向外传播, 不反向禁止它的清空确认操作.
const resetDisabled = computed(() => Object.entries(activity.value).some(([section, active]) => section !== "reset" && active))
const general = ref<HTMLElement>()
const mcp = ref<HTMLElement>()
const data = ref<HTMLElement>()
const environment = ref<HTMLElement>()
const updatesSection = ref<HTMLElement>()
let mounted = true
watch(busy, (value) => emit("busy", value))
watch([() => props.initialTab, () => props.navigationId], focusSection)

/** 合并 section 对应面板的 value 在途状态, 防止一个面板完成时提前解除其他操作的导航限制. */
function updateBusy(section: "language" | "logging" | "mcp" | "reset", value: boolean): void {
    activity.value = { ...activity.value, [section]: value }
}

/** 搜索跳转只滚动到已挂载分区, 保留当前设置状态; 卸载后不操作 DOM. */
async function focusSection(): Promise<void> {
    await nextTick()
    if (!mounted) return
    const sections: Record<string, HTMLElement | undefined> = {
        general: general.value,
        mcp: mcp.value,
        updates: updatesSection.value,
        environment: environment.value,
        data: data.value,
    }
    const target = sections[props.initialTab ?? ""]
    scrollToWorkspaceSection(target)
}
onMounted(focusSection)
onUnmounted(() => {
    mounted = false
})
</script>
<template>
    <section class="settings-workspace" :aria-label="t('app.settings')" :aria-busy="busy">
        <section ref="mcp" class="settings-section" :aria-label="t('mcp.title')">
            <McpSettingsPanel :bridge="bridge" @busy="updateBusy('mcp', $event)" />
        </section>
        <section ref="general" class="settings-section general-settings" :aria-label="t('settings.general')">
            <h2 class="settings-section-title">{{ t("settings.general") }}</h2>
            <UiSettingsPanel :bridge="bridge" @busy="updateBusy('language', $event)" />
            <LoggingPanel :bridge="bridge" @busy="updateBusy('logging', $event)" />
        </section>
        <section ref="updatesSection" class="settings-section" :aria-label="t('updates.title')">
            <SoftwareUpdatePanel :bridge="bridge" :updates="updates" />
        </section>
        <section ref="environment" class="settings-section" :aria-label="t('diagnostic.heading')">
            <EnvironmentPanel :bridge="bridge" />
        </section>
        <section ref="data" class="settings-section" :aria-label="t('dataReset.title')">
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
}
.settings-section + .settings-section {
    padding-top: 24px;
    border-top: 1px solid var(--el-border-color-light);
}
.settings-section-title {
    margin: 0;
    color: var(--brand-primary);
    font-size: 20px;
}
.general-settings :deep(.logging-panel) {
    margin-top: 0;
}
</style>
