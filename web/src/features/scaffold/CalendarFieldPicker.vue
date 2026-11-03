<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\CalendarFieldPicker.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享63字段平铺选择, 固定编号及宿主目标锁定说明, 通用方案独立于锁定列.
-->
<script setup lang="ts">
import { computed } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { CalendarCapability, CalendarFieldDefinition, CalendarPreferences, ScaffoldCalendarOptions } from "./contracts"
import {
    calendarColumnName,
    calendarColumnOutputName,
    calendarFieldDescription,
    calendarFields,
    effectiveFields,
    recommendedFields,
    selectedCalendarFields,
    simpleFields,
} from "./calendarFields"
const props = defineProps<{
    calendar: ScaffoldCalendarOptions
    disabled: boolean
    /** 仅临时操作锁保留可编辑控件外观, 业务关闭和必需字段不使用此样式. */
    operationLocked?: boolean
    fields?: CalendarFieldDefinition[]
    capability?: CalendarCapability
    preferenceFields: string[]
    last?: CalendarPreferences
}>()
const emit = defineEmits<{ change: [fields: string[], preferenceFields: string[], numbered: boolean] }>()
const { t, locale } = useI18n()
const directory = computed(() => (props.fields?.length ? props.fields : calendarFields))
const groups = computed(() => [...new Set(directory.value.map((field) => field.group))])
const locked = computed(() => [{ field: "Dates", reason: t("scaffold.dateKeyRequired") }, ...(props.capability?.lockedFields ?? [])])
const selected = computed(() => effectiveFields(selectedCalendarFields(props.calendar), locked.value))
const numbered = computed(() => props.calendar.numberedColumns !== false)
const capabilityWarning = computed(() =>
    props.capability?.warning === "已有表没有可验证的日期生成归属, 请使用新的日期表名称" ? t("scaffold.calendarOwnershipWarning") : props.capability?.warning,
)
/** 宿主固定原因码使用共享字典, 其他诊断原文保留以免隐藏实际目标限制. */
function lockedReason(reason: string): string {
    return reason === "templateReference"
        ? t("scaffold.calendarTemplateReference")
        : reason === "existingReference"
          ? t("scaffold.calendarExistingReference")
          : reason
}
/** 展示标准列按本次编号覆盖后的名称, 多个非标准人工别名逐项保留, 不合并同语义列. */
function outputName(id: string): string {
    const names =
        props.capability?.mode === "existing"
            ? props.capability.columns.filter((column) => column.field === id).map((column) => calendarColumnOutputName(id, column.name, numbered.value))
            : undefined
    return names?.length ? names.join(" / ") : calendarColumnName(id, numbered.value)
}
/** 只把本次主动增删差异写入通用方案, 目标锁定与回读不参与全局偏好. */
function change(values: (string | number | boolean)[]): void {
    if (props.disabled) return
    const next = effectiveFields(values.map(String), locked.value)
    const preference = new Set(props.preferenceFields)
    for (const id of selected.value) if (!next.includes(id)) preference.delete(id)
    for (const id of next) if (!selected.value.includes(id)) preference.add(id)
    emit("change", next, effectiveFields([...preference]), numbered.value)
}
/** 快捷方案覆盖主动选择, 必需目标列仍保留但不加入方案记忆. */
function preset(fields: string[], numbering = numbered.value): void {
    if (props.disabled) return
    emit("change", effectiveFields(fields, locked.value), effectiveFields(fields), numbering)
}
/** 编号变更遵守真实禁用状态, 迟到组件事件不得改写关闭或只读草稿. */
function changeNumbering(value: string | number | boolean): void {
    if (!props.disabled) emit("change", selected.value, props.preferenceFields, value === true)
}
</script>
<template>
    <div class="calendar-fields">
        <div class="scaffold-toolbar calendar-fields__toolbar">
            <h3>
                {{ t("scaffold.fieldSelection") }} <span aria-live="polite">{{ selected.length }} / {{ directory.length }}</span>
            </h3>
            <el-button :disabled="disabled" :class="{ 'operation-lock': operationLocked }" @click="preset(recommendedFields)">{{
                t("scaffold.recommendedFields")
            }}</el-button>
            <el-button :disabled="disabled" :class="{ 'operation-lock': operationLocked }" @click="preset(simpleFields)">{{
                t("scaffold.simpleFields")
            }}</el-button>
            <el-button :disabled="disabled" :class="{ 'operation-lock': operationLocked }" @click="preset(directory.map((field) => field.id))">{{
                t("scaffold.allFields")
            }}</el-button>
            <el-button :disabled="disabled" :class="{ 'operation-lock': operationLocked }" @click="preset(['Dates'])">{{
                t("scaffold.clearOptionalFields")
            }}</el-button>
            <el-button
                :disabled="disabled || !last"
                :class="{ 'operation-lock': operationLocked && !!last }"
                @click="last && preset(last.fields, last.numberedColumns)"
                >{{ t("scaffold.restoreFields") }}</el-button
            >
        </div>
        <el-checkbox :model-value="numbered" :disabled="disabled" :class="{ 'operation-lock': operationLocked }" @update:model-value="changeNumbering">
            {{ t("scaffold.numberColumns") }}
        </el-checkbox>
        <p v-if="capability?.mode === 'existing'" class="muted">{{ t("scaffold.existingCalendarColumns") }}</p>
        <p v-if="capabilityWarning" role="status" class="muted">{{ capabilityWarning }}</p>
        <section v-for="group in groups" :key="group" class="calendar-fields__group">
            <h4>{{ t(`scaffold.fieldGroup${group}`) === `scaffold.fieldGroup${group}` ? group : t(`scaffold.fieldGroup${group}`) }}</h4>
            <el-checkbox-group :model-value="selected" :disabled="disabled" @update:model-value="change">
                <el-checkbox
                    v-for="field in directory.filter((item) => item.group === group)"
                    :key="field.id"
                    :value="field.id"
                    :disabled="disabled || locked.some((item) => item.field === field.id)"
                    class="calendar-fields__choice"
                    :class="{ 'operation-lock': operationLocked && !locked.some((item) => item.field === field.id) }"
                >
                    <span class="calendar-fields__label"
                        ><strong>{{ outputName(field.id) }}</strong
                        ><span>{{ calendarFieldDescription(field.id, locale === "en-us") }}</span>
                        <span v-for="(item, index) in locked.filter((item) => item.field === field.id)" :key="index" class="calendar-fields__reason"
                            >{{ t("scaffold.fieldLocked") }}: {{ lockedReason(item.reason) }}</span
                        ></span
                    >
                </el-checkbox>
            </el-checkbox-group>
        </section>
    </div>
</template>
<style scoped lang="scss">
.calendar-fields__toolbar {
    flex-wrap: wrap;
    gap: 8px;
}
.calendar-fields__group {
    margin-block: 16px;
}
.calendar-fields__group h4 {
    margin-block: 8px;
}
.calendar-fields__group :deep(.el-checkbox-group) {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(245px, 100%), 1fr));
    gap: 8px 16px;
}
.calendar-fields__choice {
    min-width: 0;
    height: auto;
    min-height: 54px;
    margin: 0;
    align-items: flex-start;
    white-space: normal;
}
.calendar-fields__choice :deep(.el-checkbox__input) {
    margin-top: 4px;
}
.calendar-fields__label {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
    overflow-wrap: anywhere;
}
.calendar-fields__label > span {
    font-size: 12px;
}
.calendar-fields__reason {
    color: var(--el-text-color-secondary);
}
</style>
