<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\ScaffoldPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 自由往返的五步新项目配置, 组合当前挂载的业务编排并保留部分成功事实.
-->
<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount } from "vue"
import type { ComponentPublicInstance } from "vue"
import type { BridgeClient, ProjectSnapshot } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import UiPanel from "@/ui/UiPanel.vue"
import UiSteps from "@/ui/UiSteps.vue"
import TemplateManagerDialog from "./TemplateManagerDialog.vue"
import TemplateQueries from "./TemplateQueries.vue"
import ConfigurationFields from "../configuration/ConfigurationFields.vue"
import { formatSize } from "@/features/sample/presentation"
import ScaffoldField from "./ScaffoldField.vue"
import { scaffoldWarning } from "./warnings"
import type { PbipTemplateInfo } from "./contracts"
import { useScaffoldState } from "@/composables/features/scaffold/useScaffoldState"
import { useScaffoldOperations } from "@/composables/features/scaffold/useScaffoldOperations"
import { useScaffoldValidationFocus } from "@/composables/features/scaffold/useScaffoldValidationFocus"
import { useScaffoldCatalog } from "@/composables/features/scaffold/useScaffoldCatalog"
import { useScaffoldCreation } from "@/composables/features/scaffold/useScaffoldCreation"
import "./scaffold.scss"
/** 五步展示复用共享字段; canConnect 阻止覆盖待保存的当前模型, 仅连接成功时向外发布权威快照, 失败留页. */
const props = withDefaults(defineProps<{ bridge: BridgeClient; canConnect?: boolean }>(), { canConnect: true })
const emit = defineEmits<{ busy: [value: boolean]; connected: [value: ProjectSnapshot] }>()
const { t } = useI18n()
const state = useScaffoldState(props.bridge)
const operations = useScaffoldOperations(state, (value) => emit("busy", value))
const validation = useScaffoldValidationFocus(state)
const { showIssue } = validation
const { load, changeTemplate, choose, openTemplates, openCreated, startAnother } = useScaffoldCatalog(
    state,
    operations,
    (value) => emit("connected", value),
    () => props.canConnect !== false,
)
const { create } = useScaffoldCreation(state, operations, validation)
const {
    step,
    projectName,
    templateId,
    templateSelectionInvalid,
    git,
    output,
    visited,
    templates,
    templateQueries,
    selectedTemplateQueries,
    templateQueryGroups,
    definitions,
    templateDirectory,
    draft,
    details,
    result,
    gitCapability,
    resultGitRequired,
    templateManager,
    udfValid,
    childBusy,
    simulation,
    visibleError,
    panelRoot,
    toolbarTop,
    measureDialog,
    formLocked,
    locked,
    template,
    previewContext,
    steps,
    currentStep,
    baseIssue,
    createReady,
    canCreate,
    gitStatus,
    canStartAnother,
    createdOpenReady,
    customTemplates,
    templateCounts,
} = state
const desktopOpenReady = computed(() => state.desktopOpenReady.value && props.canConnect !== false)
useMessageNotices("scaffold.panel", () => [
    templateSelectionInvalid.value && !result.value?.projectCreated && { key: "templateChanged", message: t("configuration.templateChanged") },
    simulation.value && { key: "simulation", message: t("scaffold.simulation"), type: "info", once: true },
    { key: "error", message: visibleError.value, type: "error" },
    ...templates.value
        .filter((item) => item.warnings.includes("TEMPLATE_NAME_CONFLICT"))
        .map((item) => ({
            key: `template-conflict:${item.id}`,
            message: `${item.name}: ${scaffoldWarning("TEMPLATE_NAME_CONFLICT")}`,
            identity: `${item.name}:TEMPLATE_NAME_CONFLICT`,
        })),
])
let headerObserver: ResizeObserver | undefined,
    headerFrame = 0
/** 绑定当前挂载的真实根节点或卸载 null; 仅更新原字段焦点 ref, 不返回值. */
function bindPanelRoot(value: Element | ComponentPublicInstance | null): void {
    panelRoot.value = value instanceof HTMLElement ? value : undefined
}
/** 内置说明使用同一双语字典, 用户说明不参与机器翻译. */
function templateDescription(item?: PbipTemplateInfo): string {
    if (!item) return ""
    const id = item.id.replace(/^builtin:/, "")
    return item.source === "builtin" && id === "blank" ? t("scaffold.blankDescription") : item.description
}
/** 内置来源转换提示为固定双语说明, 其他诊断保留稳定码与宿主原文. */
function warningText(value: string): string {
    return scaffoldWarning(value)
}
/** 操作栏吸附于实际工作区顶栏之下, 高度观察延后一帧回写, 不在 ResizeObserver 交付阶段修改布局. */
function observeHeader(): void {
    const header = document.querySelector<HTMLElement>(".workspace-header")
    if (!header) return
    const update = () => {
        if (!headerFrame)
            headerFrame = requestAnimationFrame(() => {
                headerFrame = 0
                toolbarTop.value = header.getBoundingClientRect().height
            })
    }
    if (typeof ResizeObserver !== "undefined") {
        headerObserver = new ResizeObserver(update)
        headerObserver.observe(header)
    }
    update()
}
onMounted(() => {
    observeHeader()
    void load()
})
onBeforeUnmount(() => {
    headerObserver?.disconnect()
    if (headerFrame) cancelAnimationFrame(headerFrame)
})
</script>
<template>
    <section :ref="bindPanelRoot" class="scaffold-panel" :data-operation-locked="locked" :aria-label="t('scaffold.title')" :aria-busy="locked">
        <UiSteps v-if="!result?.projectCreated" v-model="currentStep" :items="steps" :disabled="locked" :aria-label="t('scaffold.title')" />
        <div class="scaffold-actions" :style="{ top: `${toolbarTop}px` }">
            <template v-if="!result?.projectCreated">
                <ScaffoldField field="output" tag="div" class="scaffold-output">
                    <el-button :class="{ 'operation-lock': locked }" :disabled="locked" @click="choose">{{ t("scaffold.choose") }}</el-button>
                    <span class="scaffold-path" :title="output">{{ output || "-" }}</span>
                </ScaffoldField>
                <el-checkbox
                    :model-value="git"
                    @update:model-value="git = $event === true"
                    :class="{ 'operation-lock': locked && gitCapability?.available }"
                    :disabled="locked || !gitCapability?.available"
                    >{{ t("scaffold.git") }}</el-checkbox
                >
                <span v-if="!gitCapability?.available" class="scaffold-git-status">{{ gitStatus }}</span>
            </template>
            <div v-else class="scaffold-output">
                <el-button
                    :class="{ 'operation-lock': locked && createdOpenReady }"
                    :disabled="locked || !createdOpenReady"
                    @click="openCreated('scaffold.openDirectory')"
                    >{{ t("scaffold.openDirectory") }}</el-button
                >
                <span class="scaffold-path scaffold-created-target" :title="result.projectPath">{{ t("configuration.target") }}: {{ result.projectPath }}</span>
            </div>
            <button v-if="baseIssue" type="button" class="scaffold-create-hint" :disabled="locked" @click="showIssue(baseIssue)">
                {{ t(baseIssue.key, baseIssue.params) }}
            </button>
            <span class="scaffold-action-spacer" />
            <el-button
                v-if="!result?.projectCreated"
                type="primary"
                :class="{ 'operation-lock': locked && createReady }"
                :disabled="!canCreate"
                @click="create"
                >{{ t("scaffold.create") }}</el-button
            >
            <template v-else>
                <el-button :disabled="!canStartAnother" @click="startAnother">{{ t("configuration.another") }}</el-button>
                <el-button
                    type="primary"
                    :class="{ 'operation-lock': locked && desktopOpenReady }"
                    :disabled="locked || !desktopOpenReady"
                    @click="openCreated('scaffold.openDesktop')"
                    >{{ t("scaffold.openDesktop") }}</el-button
                >
            </template>
        </div>
        <UiPanel
            v-if="result?.projectCreated && result.verified && (!resultGitRequired || result.gitInitialized)"
            class="scaffold-completion"
            data-scaffold-completion
            ><h2>{{ t("scaffold.created") }}</h2>
            <p>{{ t("scaffold.manageInModel") }}</p></UiPanel
        >
        <UiPanel v-if="!result?.projectCreated"
            ><form class="scaffold-form" @submit.prevent.stop>
                <section id="section-scaffold-template" v-show="step === 'template'" :aria-labelledby="'step-scaffold-template'">
                    <div class="scaffold-grid">
                        <ScaffoldField field="projectName" data-test="project-name"
                            >{{ t("scaffold.projectName")
                            }}<el-input
                                v-model="projectName"
                                :class="{ 'operation-lock': locked }"
                                :disabled="locked || !!result?.projectCreated"
                                maxlength="100" /></ScaffoldField
                        ><ScaffoldField field="templateId"
                            >{{ t("scaffold.chooseTemplate")
                            }}<el-select
                                v-model="templateId"
                                :class="{ 'operation-lock': locked }"
                                :disabled="locked || !!result?.projectCreated"
                                @change="changeTemplate"
                                ><el-option-group
                                    v-for="source in ['builtin', 'user']"
                                    :key="source"
                                    :label="t(source === 'builtin' ? 'configuration.builtin' : 'configuration.custom')"
                                >
                                    <el-option
                                        v-for="item in templates.filter((item) =>
                                            source === 'builtin' ? item.source === 'builtin' : item.source !== 'builtin',
                                        )"
                                        :key="item.id"
                                        :value="item.id"
                                        :label="item.name"
                                        :disabled="!item.baseline"
                                    /> </el-option-group></el-select
                        ></ScaffoldField>
                    </div>
                    <p>{{ templateDescription(template) }}</p>
                    <ul v-if="template?.warnings.length">
                        <li v-for="warning in template.warnings" :key="warning">{{ warningText(warning) }}</li>
                    </ul>
                    <div class="scaffold-toolbar">
                        <el-button :class="{ 'operation-lock': locked }" :disabled="locked" @click="templateManager = true">{{
                            t("configuration.manageTemplates")
                        }}</el-button>
                        <span class="muted">{{ t("configuration.templateCounts", templateCounts) }}</span>
                    </div>
                    <TemplateQueries v-model="selectedTemplateQueries" v-model:groups="templateQueryGroups" :queries="templateQueries" :disabled="locked" />
                </section>
                <ConfigurationFields
                    :draft="draft"
                    :bridge="bridge"
                    :step="step"
                    :visited="visited"
                    id-prefix="scaffold"
                    :disabled="formLocked"
                    :locked="locked"
                    :operation-locked="locked"
                    :definitions="definitions"
                    :details="details"
                    :preview-context="previewContext"
                    :can-generate-sample="true"
                    @busy="childBusy = $event"
                    @udf-validity="udfValid = $event"
                    @dialog="measureDialog = $event"
                /></form
        ></UiPanel>
        <UiPanel
            v-if="result && (!result.projectCreated || !result.verified || (resultGitRequired && !result.gitInitialized))"
            class="scaffold-result"
            role="status"
            ><h2>
                {{
                    t(
                        result.projectCreated
                            ? result.verified && (!resultGitRequired || result.gitInitialized)
                                ? "scaffold.created"
                                : "scaffold.partial"
                            : "scaffold.notCreated",
                    )
                }}
            </h2>
            <p class="scaffold-path">{{ result.projectPath }}</p>
            <p>
                {{ t(result.verified ? "scaffold.verified" : "scaffold.notVerified")
                }}<span v-if="resultGitRequired"> · {{ t(result.gitInitialized ? "scaffold.gitDone" : "scaffold.gitPending") }}</span>
            </p>
            <p v-if="result.errorCode">{{ result.errorCode }}</p>
            <ul>
                <li v-for="warning in result.warnings" :key="warning">{{ warningText(warning) }}</li>
            </ul>
            <p v-for="table in result.tables" :key="table.name">
                {{ table.name }} · {{ table.rowCount }} {{ t("scaffold.rows") }} · {{ formatSize(table.bytes) }} ·
                {{ t(table.state === "populated" ? "scaffold.populated" : "scaffold.empty") }}
            </p></UiPanel
        >
        <TemplateManagerDialog
            v-model="templateManager"
            :form-locked="formLocked"
            :template-directory="templateDirectory"
            :custom-templates="customTemplates"
            :visible-error="visibleError"
            :template-counts="templateCounts"
            :warning-text="warningText"
            @open="openTemplates"
            @refresh="load"
        />
    </section>
</template>
