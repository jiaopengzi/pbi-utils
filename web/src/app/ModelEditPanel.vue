<!--
 * FilePath    : pbi-utils\web\src\app\ModelEditPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 提交当前模型修改, 宿主核验目标并直接提交.
-->
<!-- 文件与模型由共享 C# 命令处理, 页面不生成文件内容或批准替代值. -->
<script setup lang="ts">
import { useTemplateRef } from "vue"
import type { FormInstance, InputInstance } from "element-plus"
import type { ProjectSnapshot } from "@/bridge/client"
import type { ModelEditSession } from "@/features/model/modelEditSession"
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import CodeEditor from "@/features/code/CodeEditor.vue"
import { useModelEdit, type ModelEditProps } from "@/composables/app/useModelEdit"
import { hasPendingModelUpdate, isConnectedDesktopProject } from "@/bridge/modelAccess"
const form = useTemplateRef<FormInstance>("form")
const valueInput = useTemplateRef<InputInstance>("valueInput")
/** active 默认为显示状态; 外部工作区隐藏本面板时只保留编辑器 DOM 和原生历史, 禁止业务动作和消息. */
const props = withDefaults(defineProps<ModelEditProps>(), { active: true })
const emit = defineEmits<{ busy: [value: boolean]; updated: [value: ProjectSnapshot]; "draft-change": [value: ModelEditSession] }>()
const {
    operation,
    table,
    objectName,
    property,
    value,
    startYear,
    endYear,
    dateTable,
    dateColumn,
    measureTable,
    templates,
    digitsN,
    digitsP,
    folder,
    formModel,
    years,
    busy,
    error,
    properties,
    names,
    labels,
    targetValid,
    valueLimit,
    fieldIdentity,
    canApply,
    conflict,
    reloadValue,
    save,
    t,
    stringSelection,
} = useModelEdit(props, emit, form, valueInput)
useMessageNotices(
    "model-edit-panel",
    () => [
        props.template === "calendar" && { key: "calendar", once: true, message: t("model.calendarNotice"), type: "info" },
        props.template === "period" && { key: "period", once: true, message: t("model.periodNotice"), type: "info" },
        !props.template && operation.value === "udf.set" && { key: "udf", once: true, message: t("model.udfValidation") },
        !!error.value && { key: "error", message: error.value, type: "error" },
        hasPendingModelUpdate(props.project) && { key: "pending", message: t("model.pendingEdit") },
        conflict.value && { key: "conflict", message: t("model.externalPropertyChanged"), identity: "external-property-changed" },
    ],
    { ready: () => props.active },
)
</script>
<template>
    <UiPanel class="copy-change"
        ><h2>{{ template === "calendar" ? t("model.calendarCreate") : template === "period" ? t("model.periodGenerate") : t("model.editCurrent") }}</h2>
        <p v-if="template">{{ t(isConnectedDesktopProject(project) ? "model.directSaveNotice" : "model.saveNotice") }}</p>
        <el-form
            ref="form"
            :model="formModel"
            label-position="top"
            :disabled="!active || busy || locked || hasPendingModelUpdate(project)"
            @submit.prevent="save"
        >
            <template v-if="template === 'calendar'"
                ><el-form-item
                    :label="t('model.newDateName')"
                    prop="objectName"
                    :rules="{ required: true, whitespace: true, message: t('model.dateNameRequired'), trigger: 'blur' }"
                    ><el-input v-model="objectName" maxlength="200"
                /></el-form-item>
                <div class="form-row">
                    <el-form-item :label="t('model.startYear')" prop="startYear" :rules="years"
                        ><el-input-number
                            :model-value="startYear"
                            :value-on-clear="startYear"
                            @update:model-value="$event !== undefined && (startYear = $event)"
                            :min="1900"
                            :max="2100"
                            :precision="0"
                            controls-position="right" /></el-form-item
                    ><el-form-item :label="t('model.endYear')" prop="endYear" :rules="years"
                        ><el-input-number
                            :model-value="endYear"
                            :value-on-clear="endYear"
                            @update:model-value="$event !== undefined && (endYear = $event)"
                            :min="1900"
                            :max="2100"
                            :precision="0"
                            controls-position="right"
                    /></el-form-item>
                </div>
            </template>
            <template v-else-if="template === 'period'">
                <el-form-item :label="t('model.baseMeasure')" prop="objectName" :rules="{ required: true, message: t('model.baseRequired'), trigger: 'change' }"
                    ><el-select v-model="objectName" :aria-label="t('model.baseMeasure')" :placeholder="t('model.choose')"
                        ><el-option
                            v-for="m in project.model.measures"
                            :key="m.table + '/' + m.name"
                            :value="m.name"
                            :label="m.table + ' · ' + m.name" /></el-select
                ></el-form-item>
                <div class="form-row">
                    <el-form-item
                        :label="t('model.dateTable')"
                        prop="dateTable"
                        :rules="{ required: true, message: t('model.dateTableRequired'), trigger: 'change' }"
                        ><el-select v-model="dateTable" :aria-label="t('model.dateTable')"
                            ><el-option v-for="t in project.model.tables" :key="t.name" :value="t.name" :label="t.name" /></el-select></el-form-item
                    ><el-form-item
                        :label="t('model.dateColumn')"
                        prop="dateColumn"
                        :rules="{ required: true, message: t('model.dateColumnRequired'), trigger: 'change' }"
                        ><el-select v-model="dateColumn" :aria-label="t('model.dateColumn')" :disabled="!dateTable"
                            ><el-option
                                v-for="c in project.model.tables.find((t) => t.name === dateTable)?.columns.filter((c) => c.dataType === 'DateTime')"
                                :key="c.name"
                                :value="c.name"
                                :label="c.name" /></el-select
                    ></el-form-item>
                </div>
                <el-form-item
                    :label="t('model.measureTable')"
                    prop="measureTable"
                    :rules="{ required: true, message: t('model.outputRequired'), trigger: 'change' }"
                    ><el-select v-model="measureTable" :aria-label="t('model.measureTable')"
                        ><el-option v-for="t in project.model.tables" :key="t.name" :value="t.name" :label="t.name" /></el-select
                ></el-form-item>
                <el-form-item
                    :label="t('model.periodTemplates')"
                    prop="templates"
                    :rules="{ type: 'array', required: true, min: 1, message: t('model.templateRequired'), trigger: 'change' }"
                    ><el-checkbox-group :model-value="templates" @update:model-value="templates = stringSelection($event)"
                        ><el-checkbox v-for="id in ['MTD', 'QTD', 'YTD', 'PY', 'YOY']" :key="id" :value="id">{{ id }}</el-checkbox></el-checkbox-group
                    ></el-form-item
                >
                <div class="form-row">
                    <el-form-item :label="t('model.numericDecimals')"
                        ><el-input-number
                            :model-value="digitsN"
                            :value-on-clear="digitsN"
                            @update:model-value="$event !== undefined && (digitsN = $event)"
                            :min="0"
                            :max="8"
                            :precision="0"
                            controls-position="right" /></el-form-item
                    ><el-form-item :label="t('model.percentDecimals')"
                        ><el-input-number
                            :model-value="digitsP"
                            :value-on-clear="digitsP"
                            @update:model-value="$event !== undefined && (digitsP = $event)"
                            :min="0"
                            :max="8"
                            :precision="0"
                            controls-position="right"
                    /></el-form-item>
                </div>
                <el-form-item :label="t('model.displayFolder')"><el-input v-model="folder" maxlength="200" /></el-form-item>
            </template>
            <template v-else>
                <el-form-item v-if="!initialOperation" :label="t('model.objectType')"
                    ><el-select v-model="operation" :aria-label="t('model.objectType')"
                        ><el-option value="measure.set" :label="t('model.measures')" /><el-option
                            value="table.set"
                            :label="t('model.tableDescription')" /><el-option value="udf.set" :label="t('model.udfEdit')" /></el-select
                ></el-form-item>
                <el-form-item
                    v-if="operation === 'measure.set'"
                    :label="t('model.table')"
                    prop="table"
                    :rules="{ required: true, message: t('model.tableRequired'), trigger: 'change' }"
                    ><el-select v-model="table" :aria-label="t('model.table')"
                        ><el-option v-for="t in project.model.tables" :key="t.name" :value="t.name" :label="t.name" /></el-select
                ></el-form-item>
                <el-form-item
                    :label="operation === 'udf.set' ? t('model.functionName') : t('model.targetObject')"
                    prop="objectName"
                    :rules="{ required: true, whitespace: true, message: t('model.targetRequired'), trigger: 'change' }"
                    ><el-input
                        v-if="operation === 'udf.set'"
                        v-model="objectName"
                        :validate-event="active"
                        :aria-label="t('model.functionName')"
                        maxlength="200" /><el-select v-else v-model="objectName" :validate-event="active" :aria-label="t('model.targetObject')"
                        ><el-option v-for="name in names" :key="name" :value="name" :label="name" /></el-select
                ></el-form-item>
                <el-form-item :label="t('model.property')"
                    ><el-select v-model="property" :aria-label="t('model.property')"
                        ><el-option v-for="p in properties" :key="p" :value="p" :label="labels[p]" /></el-select
                ></el-form-item>
                <el-form-item :label="t('model.newValue')"
                    ><CodeEditor
                        v-if="property === 'expression'"
                        :key="'code:' + fieldIdentity"
                        v-model="value"
                        language="dax"
                        :label="t('model.daxExpression')"
                        :rows="4"
                        :disabled="!active || busy || locked || hasPendingModelUpdate(project) || !targetValid" />
                    <el-input
                        v-else
                        :key="'text:' + fieldIdentity"
                        ref="valueInput"
                        v-model="value"
                        :aria-label="t('model.newValue')"
                        type="textarea"
                        :rows="4"
                        :maxlength="valueLimit"
                        :disabled="!active || busy || locked || hasPendingModelUpdate(project) || !targetValid"
                        show-word-limit
                /></el-form-item>
            </template>
            <div class="actions">
                <el-button
                    v-if="conflict"
                    native-type="button"
                    :disabled="!active || busy || locked || hasPendingModelUpdate(project) || !targetValid"
                    @click="reloadValue"
                    >{{ t("model.reloadCurrentValue") }}</el-button
                >
                <el-button
                    type="primary"
                    native-type="submit"
                    :loading="busy"
                    :disabled="!active || busy || locked || hasPendingModelUpdate(project) || !canApply"
                    >{{ t("model.applyEdits") }}</el-button
                >
            </div>
        </el-form>
    </UiPanel>
</template>
