<!--
 * FilePath    : pbi-utils\web\src\app\MeasureFields.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 单个新增、编辑和批量属性修改共用的度量值表单.
-->
<script setup lang="ts">
import { stringSelection } from "@/ui/forms"
import { useI18n } from "@/composables/i18n/useI18n"
import type { MeasureChange } from "@/bridge/client"
import CodeEditor from "@/features/code/CodeEditor.vue"
import { displayFolderOptions, measureFormatPresets } from "@/features/model/measureFieldOptions"
import { computed, ref } from "vue"
const { t } = useI18n()
/** 表单字段由父级拥有; 下拉复用模型文件夹原文, 自定义输入即时回传, 多选只启用明确勾选的属性. */
const props = withDefaults(defineProps<{ batch?: boolean; disabled?: boolean; displayFolders?: readonly string[] }>(), { displayFolders: () => [] })
const fields = defineModel<Required<Omit<MeasureChange, "action" | "table" | "name">>>({ required: true })
const enabled = defineModel<string[]>("enabled", { default: () => [] })
type SuggestedField = "formatString" | "displayFolder"
type FieldSuggestion = { value: string; label: string; labelKey?: string }
const showAllSuggestions = ref<SuggestedField | null>(null)
const folders = computed(() => displayFolderOptions(props.displayFolders).map((value) => ({ value, label: value })))
const formats = computed(() => measureFormatPresets.map((preset) => ({ value: preset.value, labelKey: preset.labelKey, label: t(preset.labelKey) })))

/** 聚焦时列出全部选项, 键入后按原始格式或当前语言标签筛选; 无匹配仍保留任意自定义输入. */
function suggestions(key: SuggestedField, query: string): FieldSuggestion[] {
    const options = key === "formatString" ? formats.value : folders.value
    if (showAllSuggestions.value === key) return options
    const text = query.toLowerCase()
    return options.filter((option) => option.value.toLowerCase().includes(text) || option.label.toLowerCase().includes(text))
}

/** 返回本地格式预设, 不执行表达式, 不把常规转换为额外的格式文本. */
function formatSuggestions(query: string): FieldSuggestion[] {
    return suggestions("formatString", query)
}

/** 返回当前模型已存在的文件夹及祖先路径, 不修改模型或生成新文件夹. */
function folderSuggestions(query: string): FieldSuggestion[] {
    return suggestions("displayFolder", query)
}

/** 接受当前可编辑字段的原始文本, 拒绝锁定或超长的迟到选择, 不截断已有模型值. */
function updateSuggestedField(key: SuggestedField, value: string | number): void {
    if (props.disabled || (props.batch && !enabled.value.includes(key)) || typeof value !== "string" || value.length > 2000) return
    fields.value[key] = value
}
/** 标签随界面语言更新, 字段键与父级草稿保持稳定. */
const labels = computed(() => ({
    expression: t("model.daxExpression"),
    description: t("model.description"),
    formatString: t("model.formatString"),
    displayFolder: t("model.displayFolder"),
    isHidden: t("model.hidden"),
}))
</script>
<template>
    <el-form label-position="top" :disabled="disabled" @submit.prevent>
        <el-form-item v-for="(label, key) in labels" :key="key">
            <template #label>
                <el-checkbox-group v-if="batch" :model-value="enabled" @update:model-value="enabled = stringSelection($event)"
                    ><el-checkbox :value="key">{{ t("model.modifyField", { label }) }}</el-checkbox></el-checkbox-group
                >
                <span v-else>{{ label }}</span>
            </template>
            <el-switch
                v-if="key === 'isHidden'"
                :model-value="fields.isHidden"
                @update:model-value="fields.isHidden = $event === true"
                :aria-label="t('model.hidden')"
                :disabled="disabled || (batch && !enabled.includes(key))"
            />
            <CodeEditor
                v-else-if="key === 'expression'"
                v-model="fields.expression"
                language="dax"
                :label="label"
                :disabled="disabled || (batch && !enabled.includes(key))"
            />
            <el-autocomplete
                v-else-if="key === 'formatString' || key === 'displayFolder'"
                :model-value="fields[key]"
                :fetch-suggestions="key === 'formatString' ? formatSuggestions : folderSuggestions"
                :aria-label="label"
                :placeholder="t(key === 'formatString' ? 'measureField.formatPlaceholder' : 'measureField.folderPlaceholder')"
                :maxlength="2000"
                :debounce="0"
                :disabled="disabled || (batch && !enabled.includes(key))"
                clearable
                fit-input-width
                class="measure-field-complete"
                @focus="showAllSuggestions = key"
                @input="showAllSuggestions = null"
                @update:model-value="updateSuggestedField(key, $event)"
            >
                <template #default="{ item }">
                    <span class="measure-field-option">
                        <span>{{ item.labelKey ? t(item.labelKey) : item.label }}</span>
                        <code v-if="key === 'formatString' && item.value">{{ item.value }}</code>
                    </span>
                </template>
            </el-autocomplete>
            <el-input
                v-else
                v-model="fields[key]"
                :aria-label="label"
                :type="key === 'description' ? 'textarea' : 'text'"
                :rows="2"
                :maxlength="2000"
                :disabled="disabled || (batch && !enabled.includes(key))"
            />
        </el-form-item>
    </el-form>
</template>
<style scoped lang="scss">
.measure-field-complete {
    width: 100%;
}
.measure-field-option {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    code {
        color: var(--el-text-color-secondary);
        font-size: 12px;
    }
}
</style>
