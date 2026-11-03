<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfFunctionRow.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地逻辑函数包的共享行, tag切换保留精确版本身份并支持浏览与管理动作.
-->
<script setup lang="ts">
import UiIcon from "@/ui/UiIcon.vue"
import UdfHighlight from "./UdfHighlight.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { LocalUdfEntry } from "./contracts"
import type { LocalUdfPackage } from "./udfLocalPackages"
/** 参数包括逻辑包、受控当前tag和禁用状态; 事件只发精确条目, 不访问宿主. */
const props = defineProps<{
    group: LocalUdfPackage
    entry: LocalUdfEntry
    matches?: number[][]
    checked?: boolean
    disabled: boolean
    operationLocked?: boolean
    manage?: boolean
}>()
const emit = defineEmits<{ select: [checked: boolean]; changeVersion: [id: string]; details: []; edit: []; delete: [] }>()
const { t } = useI18n()
/** 依赖核验前不保留原生输入的临时勾选; 父级成功回执再更新受控值. */
function select(value: unknown, event?: Event): void {
    if (!props.disabled) emit("select", value === true)
    if (event?.target instanceof HTMLInputElement) event.target.checked = props.checked === true
}
/** 版本选项只允许当前组的精确宿主GUID, 禁用或未知值不发事件. */
function changeVersion(value: unknown): void {
    if (!props.disabled && typeof value === "string" && props.group.versions.some((item) => item.id === value)) emit("changeVersion", value)
}
</script>
<template>
    <article class="udf-function-row udf-local-package-row" :data-function="entry.definition.name" :data-package="group.key">
        <el-checkbox
            v-if="!manage"
            :model-value="checked"
            :disabled="disabled"
            :aria-label="entry.definition.name"
            :class="{ 'operation-lock': operationLocked }"
            @change="select"
        />
        <UiIcon name="square-function" class="udf-function-icon" />
        <span class="udf-function-name" :title="entry.definition.name"><UdfHighlight :text="entry.definition.name" :matches="matches ?? []" /></span>
        <el-select
            class="udf-local-tag-picker"
            :model-value="entry.id"
            :disabled="disabled"
            :aria-label="t('scaffold.packageTag', { name: group.name })"
            :class="{ 'operation-lock': operationLocked }"
            @update:model-value="changeVersion"
        >
            <el-option v-for="version in group.versions" :key="version.id" :value="version.id" :label="version.definition.version" />
        </el-select>
        <template v-if="manage"
            ><el-button :disabled="disabled" :class="{ 'operation-lock': operationLocked }" @click="!disabled && emit('edit')">{{
                t("scaffold.edit")
            }}</el-button
            ><el-button :disabled="disabled" :class="{ 'operation-lock': operationLocked }" @click="!disabled && emit('delete')">{{
                t("scaffold.delete")
            }}</el-button></template
        >
        <el-button
            v-else
            :disabled="disabled"
            :class="{ 'operation-lock': operationLocked }"
            :aria-label="`${t('scaffold.functionDetails')} ${entry.definition.name}`"
            @click="!disabled && emit('details')"
            >{{ t("scaffold.functionDetails") }}</el-button
        >
    </article>
</template>
<style scoped lang="scss">
.udf-local-tag-picker {
    width: 180px;
    flex: 0 0 180px;
}
.udf-local-package-row {
    min-width: 0;
}
</style>
