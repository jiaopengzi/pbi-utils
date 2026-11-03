<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\CalendarTableFields.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 日期表基本字段、记忆方案及只读代码预览.
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue"
import ScaffoldField from "./ScaffoldField.vue"
import CalendarFieldPicker from "./CalendarFieldPicker.vue"
import CalendarCodePreview from "./CalendarCodePreview.vue"
import WorkdayOverrides from "./WorkdayOverrides.vue"
import { calendarDateColumn, calendarNeedsLunar, effectiveFields, recommendedFields, selectedCalendarFields } from "./calendarFields"
import { calendarPreferenceDraftState, calendarPreferences } from "./calendarPreferences"
import { vNumberAccessibility } from "@/ui/numberAccessibility"
import { useI18n } from "@/composables/i18n/useI18n"
import type { BridgeClient } from "@/bridge/client"
import type { CalendarPreviewContext, ScaffoldCalendarOptions, ScaffoldPeriodOptions, TemplateDetails } from "./contracts"
/** 编辑日期表及用户字段方案, 迟到偏好不覆盖已改草稿或现有目标事实. */
const calendar = defineModel<ScaffoldCalendarOptions>("calendar", { required: true })
const periods = defineModel<ScaffoldPeriodOptions>("periods", { required: true })
const calendarEnabled = defineModel<boolean>("calendarEnabled", { required: true })
const props = withDefaults(
    defineProps<{
        disabled: boolean
        operationLocked?: boolean
        details?: TemplateDetails
        bridge?: BridgeClient
        previewContext?: CalendarPreviewContext
        previewInvalidationKey?: string
        independent?: boolean
        /** 默认显示新建可选开关, 模型动作模式传 false 隐藏, 不改变字段和预览的锁. */
        showEnableSwitch?: boolean
    }>(),
    { showEnableSwitch: true },
)
const emit = defineEmits<{ dialog: [value: boolean] }>()
const { t } = useI18n()
const preferences = props.bridge ? calendarPreferences(props.bridge) : undefined
const preferenceFields = ref([...recommendedFields])
const preferenceWarning = computed(() =>
    preferences?.warning.value === "日期字段方案无法读取, 已使用推荐方案; 原文件已保留且未覆盖。"
        ? t("scaffold.calendarPreferenceReadWarning")
        : t("scaffold.calendarPreferenceWarning"),
)
const capability = computed(() => props.details?.calendarCapabilities?.find((item) => item.tableName === calendar.value.tableName))
const lunar = computed(() => calendarNeedsLunar(selectedCalendarFields(calendar.value)))
let disposed = false,
    preferenceGeneration = 0
onBeforeUnmount(() => {
    disposed = true
    emit("dialog", false)
})
watch(
    [() => calendar.value, () => capability.value, () => props.disabled],
    async ([model]) => {
        const own = ++preferenceGeneration
        if (props.disabled) return
        const state = calendarPreferenceDraftState(model)
        const initial = JSON.stringify(model)
        const saved = await preferences?.read()
        if (disposed || own !== preferenceGeneration || model !== calendar.value || !saved) return
        preferenceFields.value = [...saved.fields]
        if (
            !state.initialized &&
            capability.value?.mode !== "existing" &&
            state.baseline === initial &&
            initial === JSON.stringify(model) &&
            model.fields?.join() === effectiveFields(recommendedFields, capability.value?.lockedFields).join()
        ) {
            state.initialized = true
            model.fields = effectiveFields(saved.fields, capability.value?.lockedFields)
            model.numberedColumns = saved.numberedColumns
        }
    },
    { immediate: true },
)
watch(
    () => calendarDateColumn(calendar.value, capability.value),
    (value, previous) => {
        if (!props.independent && periods.value.dateTable === calendar.value.tableName && periods.value.dateColumn === previous)
            periods.value.dateColumn = value
    },
)
/** 只有用户主动字段或编号变更才保存通用方案; 宿主锁定与初始化不触发写入. */
function changeFields(fields: string[], preference: string[], numbered: boolean): void {
    if (props.disabled || !calendarEnabled.value) return
    calendarPreferenceDraftState(calendar.value).initialized = true
    preferenceFields.value = [...preference]
    calendar.value.fields = fields
    calendar.value.numberedColumns = numbered
    void preferences?.save(preference, numbered)
}
</script>
<template>
    <label v-if="showEnableSwitch !== false" class="scaffold-switch"
        ><el-switch
            :model-value="calendarEnabled"
            @update:model-value="calendarEnabled = $event === true"
            :class="{ 'operation-lock': disabled }"
            :disabled="disabled"
            :aria-label="t('scaffold.calendarEnable')"
            data-optional
        />{{ t("scaffold.calendarEnable") }}</label
    >
    <div class="scaffold-grid">
        <ScaffoldField field="tableName"
            >{{ t("scaffold.tableName")
            }}<el-input v-model="calendar.tableName" :class="{ 'operation-lock': disabled && calendarEnabled }" :disabled="disabled || !calendarEnabled"
        /></ScaffoldField>
        <ScaffoldField field="startYear"
            >{{ t("scaffold.startYear")
            }}<el-input-number
                v-number-accessibility
                :model-value="calendar.startYear"
                @update:model-value="calendar.startYear = $event ?? 0"
                :min="lunar ? 2000 : 1900"
                :max="lunar ? 2099 : 2100"
                :class="{ 'operation-lock': disabled && calendarEnabled }"
                :disabled="disabled || !calendarEnabled"
        /></ScaffoldField>
        <ScaffoldField field="endYear"
            >{{ t("scaffold.endYear")
            }}<el-input-number
                v-number-accessibility
                :model-value="calendar.endYear"
                @update:model-value="calendar.endYear = $event ?? 0"
                :min="lunar ? 2000 : 1900"
                :max="lunar ? 2099 : 2100"
                :class="{ 'operation-lock': disabled && calendarEnabled }"
                :disabled="disabled || !calendarEnabled"
        /></ScaffoldField>
    </div>
    <ScaffoldField field="fields" tag="div" error-first>
        <CalendarFieldPicker
            :calendar="calendar"
            :fields="details?.calendarFields"
            :capability="capability"
            :disabled="disabled || !calendarEnabled"
            :operation-locked="operationLocked && calendarEnabled"
            :preference-fields="preferenceFields"
            :last="preferences?.remembered.value"
            @change="changeFields"
        />
    </ScaffoldField>
    <p v-if="preferences?.warning.value" role="status" class="muted">{{ preferenceWarning }}</p>
    <p class="muted">{{ t("scaffold.workdayHint") }}</p>
    <ScaffoldField field="workdays" tag="div">
        <h3>{{ t("scaffold.weeklyWorkdays") }}</h3>
        <el-checkbox-group
            :model-value="calendar.workdays ?? [0, 1, 2, 3, 4]"
            :disabled="disabled || !calendarEnabled"
            :class="{ 'operation-lock': disabled && calendarEnabled }"
            @update:model-value="calendar.workdays = $event.map(Number)"
        >
            <el-checkbox-button v-for="day in 7" :key="day" :value="day - 1">{{ t(`scaffold.weekday${day - 1}`) }}</el-checkbox-button>
        </el-checkbox-group>
    </ScaffoldField>
    <WorkdayOverrides v-model:calendar="calendar" :disabled="disabled" :calendar-enabled="calendarEnabled" :bridge="bridge" @dialog="emit('dialog', $event)" />
    <CalendarCodePreview
        v-if="bridge"
        :bridge="bridge"
        :context="previewContext"
        :preview-invalidation-key="previewInvalidationKey"
        :calendar="calendar"
        :enabled="calendarEnabled"
        :disabled="disabled"
    />
</template>
