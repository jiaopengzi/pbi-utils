<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\MeasuresEditor.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 平铺六字段模板度量值编辑表格, 长值复用专业编辑器.
-->
<script setup lang="ts">
import { ref, watch } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import UiDialog from "@/ui/UiDialog.vue"
import ScaffoldField from "./ScaffoldField.vue"
import CodeEditor from "@/features/code/CodeEditor.vue"
import type { TemplateMeasureDefinition } from "./contracts"
import { templateMeasureCategories } from "./dataCategories"
const model = defineModel<TemplateMeasureDefinition[]>({ required: true })
defineProps<{ disabled: boolean; operationLocked?: boolean }>()
const emit = defineEmits<{ dialog: [value: boolean] }>()
const { t } = useI18n()
const editing = ref<number>()
const longValue = ref("")
watch(editing, (value) => emit("dialog", value !== undefined), { flush: "sync" })
/** 新增完整六字段记录, 不默认覆盖现有记录. */
function add(): void {
    model.value.push({ name: "", value: "", dataCategory: "Uncategorized", enabled: true, description: "", displayFolder: "" })
}
/** 把目标长值复制到编辑缓冲, 保存前不更改草稿. */
function edit(index: number): void {
    editing.value = index
    longValue.value = model.value[index]!.value
}
/** 显式保存编辑缓冲, 目标行不存在时忽略. */
function save(): void {
    if (editing.value !== undefined && model.value[editing.value]) model.value[editing.value]!.value = longValue.value
    editing.value = undefined
}
</script>
<template>
    <div class="scaffold-table-scroll">
        <table class="scaffold-measures scaffold-records scaffold-table">
            <thead>
                <tr>
                    <th v-for="field in ['name', 'value', 'dataCategory', 'enabled', 'description', 'folder']" :key="field" scope="col">
                        {{ t(`scaffold.${field}`) }}
                    </th>
                    <th scope="col">{{ t("scaffold.remove") }}</th>
                </tr>
            </thead>
            <tbody>
                <tr v-for="(item, index) in model" :key="index">
                    <ScaffoldField tag="td" field="name" :row="index + 1" :data-label="t('scaffold.name')">
                        <el-input
                            v-model="item.name"
                            :class="{ 'operation-lock': operationLocked }"
                            :disabled="disabled"
                            :aria-label="`${t('scaffold.name')} ${index + 1}`"
                            maxlength="200"
                        />
                    </ScaffoldField>
                    <ScaffoldField tag="td" field="value" :row="index + 1" :data-label="t('scaffold.value')">
                        <div class="scaffold-value">
                            <el-input
                                v-model="item.value"
                                :class="{ 'operation-lock': operationLocked }"
                                :disabled="disabled"
                                :aria-label="`${t('scaffold.value')} ${index + 1}`"
                            /><el-button :class="{ 'operation-lock': operationLocked }" :disabled="disabled" @click="edit(index)">{{
                                t("scaffold.edit")
                            }}</el-button>
                        </div>
                    </ScaffoldField>
                    <ScaffoldField tag="td" field="dataCategory" :row="index + 1" :data-label="t('scaffold.dataCategory')">
                        <el-select
                            v-model="item.dataCategory"
                            :class="{ 'operation-lock': operationLocked }"
                            :disabled="disabled"
                            :aria-label="`${t('scaffold.dataCategory')} ${index + 1}`"
                            ><el-option
                                v-for="category in templateMeasureCategories"
                                :key="category"
                                :value="category"
                                :label="t(`scaffold.category${category}`)"
                        /></el-select>
                    </ScaffoldField>
                    <td :data-label="t('scaffold.enabled')">
                        <el-switch
                            :model-value="item.enabled"
                            @update:model-value="item.enabled = $event === true"
                            :class="{ 'operation-lock': operationLocked }"
                            :disabled="disabled"
                            :aria-label="`${t('scaffold.enabled')} ${index + 1}`"
                        />
                    </td>
                    <ScaffoldField tag="td" field="description" :row="index + 1" :data-label="t('scaffold.description')">
                        <el-input
                            v-model="item.description"
                            :class="{ 'operation-lock': operationLocked }"
                            :disabled="disabled"
                            :aria-label="`${t('scaffold.description')} ${index + 1}`"
                        />
                    </ScaffoldField>
                    <ScaffoldField tag="td" field="displayFolder" :row="index + 1" :data-label="t('scaffold.folder')">
                        <el-input
                            v-model="item.displayFolder"
                            :class="{ 'operation-lock': operationLocked }"
                            :disabled="disabled"
                            :aria-label="`${t('scaffold.folder')} ${index + 1}`"
                        />
                    </ScaffoldField>
                    <td :data-label="t('scaffold.remove')">
                        <el-button
                            :class="{ 'operation-lock': operationLocked }"
                            :disabled="disabled"
                            :aria-label="`${t('scaffold.remove')} ${index + 1}`"
                            @click="model.splice(index, 1)"
                            >{{ t("scaffold.remove") }}</el-button
                        >
                    </td>
                </tr>
            </tbody>
        </table>
    </div>
    <el-button :class="{ 'operation-lock': operationLocked }" :disabled="disabled" @click="add">{{ t("scaffold.add") }}</el-button>
    <UiDialog :model-value="editing !== undefined" :title="t('scaffold.value')" :busy="disabled" @update:model-value="editing = undefined">
        <CodeEditor v-model="longValue" language="dax" :label="t('scaffold.value')" :maxlength="32768" :disabled="disabled" />
        <template #footer
            ><el-button :class="{ 'operation-lock': operationLocked }" :disabled="disabled" @click="editing = undefined">{{ t("scaffold.cancel") }}</el-button
            ><el-button type="primary" :class="{ 'operation-lock': operationLocked }" :disabled="disabled" @click="save">{{
                t("scaffold.save")
            }}</el-button></template
        >
    </UiDialog>
</template>
