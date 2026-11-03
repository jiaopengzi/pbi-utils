<!--
 * FilePath    : pbi-utils\web\src\app\ModelPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 展示真实模型快照和原始表达式, 保留模型顶部二级流程.
-->
<!-- 模型列表与代码展示, 不把高亮视为执行或验证. -->
<script setup lang="ts">
import { hostText } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"

import UiTabs from "@/ui/UiTabs.vue"
import { computed, ref, watch } from "vue"
import type { ProjectSnapshot, BridgeClient } from "../bridge/client"
import { modelEditTargetIdentity, type ModelEditorState, type ModelEditSession } from "@/features/model/modelEditSession"
import CodeBlock from "../features/code/CodeBlock.vue"
import ModelEditPanel from "./ModelEditPanel.vue"
import ModelUdfLint from "./ModelUdfLint.vue"
import MeasuresPanel from "./MeasuresPanel.vue"
import ModelTablesPanel from "./ModelTablesPanel.vue"
import ConfigurationPanel from "@/features/configuration/ConfigurationPanel.vue"
import type { ConfigurationSection } from "@/features/configuration/draft"
import { isReadOnlyProject } from "@/bridge/modelAccess"
const { t } = useI18n()
/** 默认组装内部编辑器; 外部宿主可独立拥有编辑器生命周期, editorBusy 仅参与控件锁, 不回传自身 busy. */
const props = defineProps<{
    project: ProjectSnapshot
    bridge: BridgeClient
    initialTab?: string
    editSession?: ModelEditSession
    externalEditor?: boolean
    editorBusy?: boolean
    externalLocked?: boolean
}>()
const emit = defineEmits<{
    busy: [value: boolean]
    updated: [value: ProjectSnapshot]
    "draft-change": [value: ModelEditSession]
    "editor-state": [value: ModelEditorState]
}>()
const readOnly = computed(() => isReadOnlyProject(props.project))
const canGenerateSample = ref(false)
const configurationSection = computed<ConfigurationSection>(() =>
    tab.value === "templates" ? "measures" : tab.value === "time" ? "calendar" : tab.value === "sample" ? "sample" : "udfs",
)
const tabs = computed(() => [
    ["measures", t("model.measures")],
    ["tables", t("model.tables")],
    ...(!readOnly.value
        ? [["templates", t("model.templates")], ...(canGenerateSample.value ? [["sample", t("scaffold.sample")]] : []), ["time", t("model.time")]]
        : []),
    ["udf", t("model.udf")],
])
const panelBusy = ref(false)
const lintBusy = ref(false)
const exportBusy = ref(false)
const ownBusy = computed(() => panelBusy.value || lintBusy.value || exportBusy.value)
const busy = computed(() => ownBusy.value || !!props.editorBusy || !!props.externalLocked)
/** 同步子面板忙状态并通知父组件, value 为是否存在在途操作, 无返回值. */
function onBusy(value: boolean): void {
    panelBusy.value = value
}
watch(ownBusy, (value) => emit("busy", value), { flush: "sync" })
const tab = ref(tabs.value.some(([key]) => key === props.initialTab) ? props.initialTab! : "measures")
watch(
    () => ({ targetIdentity: modelEditTargetIdentity(props.project), active: !readOnly.value && tab.value === "tables", locked: ownBusy.value }),
    (state) => emit("editor-state", state),
    { immediate: true, flush: "sync" },
)
watch(
    () => props.initialTab,
    (value) => {
        if (value) tab.value = tabs.value.some(([key]) => key === value) ? value : "measures"
    },
)
watch(
    () => props.project.baselineId,
    () => {
        if (!tabs.value.some(([key]) => key === tab.value)) tab.value = "measures"
    },
)
useMessageNotices("model-panel", () => [
    !!props.project.connection?.isLimited && {
        key: "limited",
        identity: JSON.stringify(props.project.warnings),
        // 合并能力说明与原始警告, 各行仍按原有宿主翻译规则处理, 避免进入模型时多条说明同时占屏.
        message: [
            t("model.limitedNotice"),
            ...props.project.warnings.filter((item) => item.includes("度量值") || item.includes("未读取")).map((warning) => hostText(warning)),
        ].join("\n"),
    },
])
</script>
<template>
    <UiTabs v-model="tab" :aria-label="t('model.workflow')" :disabled="busy" :items="tabs" />
    <ConfigurationPanel
        v-if="!readOnly"
        v-show="['templates', 'time', 'udf', 'sample'].includes(tab)"
        :project="project"
        :bridge="bridge"
        :section="configurationSection"
        :active="['templates', 'time', 'udf', 'sample'].includes(tab)"
        :external-locked="lintBusy || editorBusy || externalLocked"
        @busy="onBusy"
        @updated="emit('updated', $event)"
        @capability="canGenerateSample = $event"
    />
    <MeasuresPanel v-if="tab === 'measures'" :project="project" :bridge="bridge" :locked="externalLocked" @busy="onBusy" @updated="emit('updated', $event)" />
    <ModelTablesPanel
        v-else-if="tab === 'tables'"
        :project="project"
        :bridge="bridge"
        :locked="panelBusy || editorBusy || externalLocked"
        @busy="exportBusy = $event"
        @updated="emit('updated', $event)"
    />
    <UiPanel v-else-if="tab === 'udf'"
        ><h2>DAX UDF</h2>
        <el-empty v-if="project.connection?.isLimited" :description="t('model.udfUnavailable')" :image-size="64" />
        <template v-else>
            <p>{{ t("model.functionCount", { level: project.model.compatibilityLevel, count: project.model.functions.length }) }}</p>

            <ModelUdfLint
                v-if="!readOnly"
                :project="project"
                :bridge="bridge"
                :locked="panelBusy || editorBusy || externalLocked"
                @busy="lintBusy = $event"
                @updated="emit('updated', $event)"
            />
            <el-collapse
                ><el-collapse-item v-for="fn in project.model.functions" :key="fn.name" :name="fn.name" :title="fn.name"
                    ><p>{{ fn.description }}</p>
                    <CodeBlock :code="fn.expression" language="dax" /></el-collapse-item></el-collapse
            ><el-empty v-if="!project.model.functions.length" :description="t('model.udfEmpty')" :image-size="64" />
        </template>
    </UiPanel>
    <ModelEditPanel
        v-if="!externalEditor && !readOnly && tab === 'tables'"
        :key="tab + '-editor'"
        initial-operation="table.set"
        :project="project"
        :bridge="bridge"
        :locked="exportBusy || externalLocked"
        :edit-session="editSession"
        @busy="onBusy"
        @updated="emit('updated', $event)"
        @draft-change="emit('draft-change', $event)"
    />
</template>
