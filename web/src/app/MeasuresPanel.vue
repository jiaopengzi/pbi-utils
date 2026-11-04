<!--
 * FilePath    : pbi-utils\web\src\app\MeasuresPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 度量值单选回填、多选批量增删改和本地 DAX 格式化预览.
-->
<script setup lang="ts">
import type { ProjectSnapshot } from "@/bridge/client"
import CodeBlock from "@/features/code/CodeBlock.vue"
import UiPanel from "@/ui/UiPanel.vue"
import UiDialog from "@/ui/UiDialog.vue"
import { measureExpressionForEditor } from "@/features/code/measureExpression"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import MeasureFields from "./MeasureFields.vue"
import MeasureHierarchy from "./MeasureHierarchy.vue"
import { useMeasuresEditor, type MeasuresEditorProps } from "@/composables/app/useMeasuresEditor"
import { useMeasureListResize } from "@/composables/app/useMeasureListResize"
const props = defineProps<MeasuresEditorProps>()
const emit = defineEmits<{ busy: [value: boolean]; updated: [value: ProjectSnapshot] }>()
const {
    readOnly,
    query,
    selected,
    busy,
    activeOperation,
    error,
    enabled,
    fields,
    formatted,
    confirming,
    savingDraft,
    resolveDraft,
    displayFolders,
    creating,
    drafts,
    blocked,
    chosen,
    current,
    fieldsChanged,
    keyOf,
    selectOne,
    requestSelection,
    selectVisible,
    addDraft,
    startCreate,
    backToEdit,
    save,
    saveEdits,
    saveCreates,
    deleteSelected,
    format,
    t,
    hostText,
} = useMeasuresEditor(props, emit)
const listResize = useMeasureListResize(() => busy.value)
const { width, resizing, minWidth, maxWidth, startResize, autoFit, onKeydown } = listResize
useMessageNotices("measures-panel", () => [
    readOnly.value && !!current.value && !current.value.expression && { key: "dax-unavailable", message: t("model.daxUnavailable") },
    !creating.value &&
        !readOnly.value &&
        !!chosen.value.length &&
        !current.value && { key: "batch", once: true, message: t("model.batchNotice"), type: "info" },
    !!error.value && { key: "error", message: error.value, type: "error" },
    !!(props.project.hasPendingChanges || props.project.saveResult?.errorCode) && { key: "pending", message: t("model.pendingSave") },
])
</script>
<template>
    <UiPanel class="measures-panel">
        <div class="panel-heading">
            <h2>{{ t("model.measures") }}</h2>
            <small>{{ t("model.measureCount", { count: project.model.measures.length, format: hostText(project.modelFormat) }) }}</small>
        </div>
        <div class="measure-toolbar">
            <el-input v-model="query" :disabled="busy" clearable :aria-label="t('model.searchLabel')" :placeholder="t('model.searchPlaceholder')" />
            <el-button v-if="!readOnly" :disabled="blocked || !project.model.tables.length" @click="startCreate">{{ t("model.newMeasure") }}</el-button>
        </div>
        <div
            :ref="listResize.layoutRef"
            class="object-layout measure-layout"
            :class="{ resizing }"
            :style="{ gridTemplateColumns: `${width}px 12px minmax(0, 1fr)` }"
        >
            <div id="measure-list" :ref="listResize.contentRef" class="measure-list">
                <MeasureHierarchy
                    :selected="selected"
                    :measures="project.model.measures"
                    :query="query"
                    :selected-count="chosen.length"
                    :disabled="busy || confirming || locked"
                    :current-key="current ? keyOf(current) : undefined"
                    @select="selectOne"
                    @select-all="selectVisible"
                    @update:selected="requestSelection"
                />
            </div>
            <div
                class="measure-list-resizer"
                role="separator"
                tabindex="0"
                aria-orientation="vertical"
                aria-controls="measure-list"
                :aria-label="t('model.measureListWidth')"
                :aria-valuemin="minWidth"
                :aria-valuemax="maxWidth"
                :aria-valuenow="Math.round(width)"
                :aria-disabled="busy"
                :title="t('model.measureListResizeHelp')"
                @pointerdown="startResize"
                @dblclick="autoFit"
                @keydown="onKeydown"
            />
            <div class="object-detail measure-editor" :class="{ 'measure-editor-empty': !creating && !chosen.length }">
                <template v-if="creating && !readOnly">
                    <h3>{{ t("model.createCount", { count: drafts.length }) }}</h3>
                    <div v-for="(draft, index) in drafts" :key="draft.id" class="measure-draft">
                        <div class="panel-heading">
                            <strong>{{ t("model.itemNumber", { count: index + 1 }) }}</strong
                            ><el-button :disabled="blocked || drafts.length === 1" text type="danger" @click="drafts.splice(index, 1)">{{
                                t("model.removeItem")
                            }}</el-button>
                        </div>
                        <el-form label-position="top" :disabled="blocked" @submit.prevent
                            ><div class="form-row">
                                <el-form-item :label="t('model.table')"
                                    ><el-select v-model="draft.table" :aria-label="t('model.table')"
                                        ><el-option v-for="t in project.model.tables" :key="t.name" :value="t.name" :label="t.name" /></el-select
                                ></el-form-item>
                                <el-form-item :label="t('model.name')"
                                    ><el-input v-model="draft.name" :aria-label="t('model.measureName')" maxlength="200"
                                /></el-form-item></div
                        ></el-form>
                        <MeasureFields v-model="draft.fields" :display-folders="displayFolders" :disabled="blocked" />
                    </div>
                    <div class="actions">
                        <el-button :disabled="blocked || drafts.length >= 128" @click="addDraft">{{ t("model.addOne") }}</el-button
                        ><el-button :disabled="blocked" :loading="busy && activeOperation === 'format'" @click="format">{{ t("model.formatLocal") }}</el-button
                        ><el-button type="primary" :disabled="blocked" :loading="busy && activeOperation === 'create'" @click="saveCreates">{{
                            t("model.createSave")
                        }}</el-button
                        ><el-button :disabled="busy || locked" @click="backToEdit">{{ t("model.backEdit") }}</el-button>
                    </div>
                </template>
                <template v-else-if="readOnly && current">
                    <h3>{{ current.name }}</h3>
                    <p>{{ current.description || t("model.noDescription") }}</p>
                    <CodeBlock v-if="current.expression" :code="current.expression" language="dax" />
                    <el-descriptions :column="1" border
                        ><el-descriptions-item :label="t('model.format')">{{
                            current.formatString || (project.connection?.isLimited ? t("model.notProvided") : t("model.notSet"))
                        }}</el-descriptions-item
                        ><el-descriptions-item :label="t('model.displayFolder')">{{
                            current.displayFolder || (project.connection?.isLimited ? t("model.notProvided") : t("model.notSet"))
                        }}</el-descriptions-item
                        ><el-descriptions-item :label="t('model.hidden')">{{
                            project.connection?.isLimited ? t("model.notProvided") : current.isHidden ? t("model.yes") : t("model.no")
                        }}</el-descriptions-item></el-descriptions
                    >
                </template>
                <template v-else-if="!readOnly && chosen.length">
                    <h3>{{ current ? `${current.table} · ${current.name}` : t("model.batchEditCount", { count: chosen.length }) }}</h3>

                    <MeasureFields
                        :key="JSON.stringify([project.projectId, selected])"
                        v-model="fields"
                        v-model:enabled="enabled"
                        :batch="!current"
                        :display-folders="displayFolders"
                        :disabled="blocked || formatted.length > 0"
                    />
                    <div class="actions">
                        <el-button
                            :disabled="blocked || !fieldsChanged || formatted.length > 0"
                            :loading="busy && activeOperation === 'save'"
                            type="primary"
                            @click="saveEdits"
                            >{{ t("model.saveEdits") }}</el-button
                        ><el-button :disabled="blocked" :loading="busy && activeOperation === 'format'" @click="format">{{
                            current ? t("model.formatLocal") : t("model.formatBatch")
                        }}</el-button
                        ><el-button type="danger" plain :disabled="blocked" :loading="busy && activeOperation === 'delete'" @click="deleteSelected">{{
                            t("model.deleteCount", { count: chosen.length })
                        }}</el-button>
                    </div>

                    <div v-if="formatted.length" class="format-preview">
                        <h3>{{ t("model.formattedCount", { count: formatted.length }) }}</h3>
                        <el-collapse
                            ><el-collapse-item v-for="item in formatted" :key="keyOf(item)" :title="`${item.table} · ${item.name}`" :name="keyOf(item)"
                                ><CodeBlock :code="measureExpressionForEditor(item.expression!)" language="dax" /></el-collapse-item
                        ></el-collapse>
                        <div class="actions">
                            <el-button
                                :disabled="blocked"
                                :loading="busy && activeOperation === 'formatted'"
                                type="primary"
                                @click="save(formatted, 'formatted')"
                                >{{ t("model.saveFormatted") }}</el-button
                            ><el-button :disabled="busy || locked" @click="formatted = []">{{ t("model.discardFormatted") }}</el-button>
                        </div>
                    </div>
                </template>
                <el-empty v-else :description="readOnly ? t('model.chooseSingle') : t('model.chooseOrCreate')" :image-size="96" />
            </div>
        </div>
        <UiDialog
            :model-value="confirming"
            :title="t('model.unsavedTitle')"
            :busy="busy || savingDraft"
            @update:model-value="!$event && resolveDraft('cancel')"
        >
            <p>{{ t("model.unsavedMessage") }}</p>
            <template #footer>
                <el-button :disabled="busy || savingDraft" @click="resolveDraft('cancel')">{{ t("model.cancel") }}</el-button>
                <el-button :disabled="busy || savingDraft" @click="resolveDraft('discard')">{{ t("model.discardDraft") }}</el-button>
                <el-button type="primary" :disabled="blocked || savingDraft" :loading="savingDraft" @click="resolveDraft('save')">{{
                    t("model.saveEdits")
                }}</el-button>
            </template>
        </UiDialog>
    </UiPanel>
</template>
<style scoped lang="scss">
.measure-toolbar {
    display: flex;
    gap: 12px;
    margin-bottom: 16px;
    :deep(.el-input) {
        flex: 1;
        min-width: 0;
    }
    :deep(.el-button) {
        flex-shrink: 0;
    }
}
.measure-list {
    min-width: 0;
    max-height: 660px;
    overflow: auto;
    padding-right: 12px;
}
.measure-layout {
    gap: 0;
}
.measure-list-resizer {
    cursor: col-resize;
    touch-action: none;
    border-radius: 4px;
    margin-right: 4px;
    border-left: 1px solid var(--el-border-color);
    &:hover,
    &:focus-visible,
    .resizing & {
        border-left-color: var(--el-color-primary);
        background: var(--el-color-primary-light-9);
        outline: none;
    }
    &[aria-disabled="true"] {
        cursor: default;
    }
}
.measure-editor {
    min-width: 0;
}
.measure-editor-empty {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 420px;
    :deep(.el-empty__description p) {
        font-size: 16px;
    }
}
.measure-draft {
    border-bottom: 1px solid var(--el-border-color);
    margin-bottom: 16px;
}
.format-preview {
    margin-top: 20px;
}
</style>
