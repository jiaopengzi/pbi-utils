<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\CalendarStep.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 日期与时间智能业务页签及跨页签校验路由.
-->
<script setup lang="ts">
import { computed, inject, ref, shallowRef, useId, watch } from "vue"
import UiTabs from "@/ui/UiTabs.vue"
import CalendarTableFields from "./CalendarTableFields.vue"
import PeriodsFields from "./PeriodsFields.vue"
import { scaffoldValidationKey } from "./validationContext"
import { useI18n } from "@/composables/i18n/useI18n"
import type { BridgeClient } from "@/bridge/client"
import type {
    CalendarPreviewContext,
    ScaffoldCalendarOptions,
    ScaffoldPeriodOptions,
    ScaffoldPeriodTemplate,
    TemplateDetails,
    TemplateMeasureDefinition,
} from "./contracts"
/** 日期与时间智能使用独立业务页签, 校验路由不创建或丢弃子表单. */
const calendar = defineModel<ScaffoldCalendarOptions>("calendar", { required: true })
const periods = defineModel<ScaffoldPeriodOptions>("periods", { required: true })
const calendarEnabled = defineModel<boolean>("calendarEnabled", { required: true })
const periodsEnabled = defineModel<boolean>("periodsEnabled", { required: true })
const props = withDefaults(
    defineProps<{
        disabled: boolean
        operationLocked?: boolean
        definitions: ScaffoldPeriodTemplate[]
        details?: TemplateDetails
        measures: TemplateMeasureDefinition[]
        bridge?: BridgeClient
        previewContext?: CalendarPreviewContext
        previewInvalidationKey?: string
        /** 现有模型分段应用时仅使用权威目录, 不把另一段未提交草稿作为依赖. */
        independent?: boolean
        /** 新建保留可选段开关; 模型由各自应用按钮提交时隐藏, 输入启用状态仍由呈现器管理. */
        showEnableSwitch?: boolean
    }>(),
    { showEnableSwitch: true },
)
const emit = defineEmits<{ dialog: [value: boolean] }>()
const { t } = useI18n()
const tab = ref("calendar")
const tabPrefix = `calendar-${useId()}`
const tabs = computed(() => [
    [`${tabPrefix}-calendar`, t("scaffold.calendarTab")],
    [`${tabPrefix}-periods`, t("scaffold.periodsTab")],
])
/** 页签切换只改变展示, 在途及弹窗锁下保留当前业务页. */
function changeTab(value: string): void {
    if (!props.disabled && tabs.value.some(([id]) => id === value)) tab.value = value === `${tabPrefix}-calendar` ? "calendar" : "periods"
}
const validation = inject(scaffoldValidationKey, shallowRef(undefined))
watch(
    () => validation.value,
    (value) => {
        if (value?.step === "calendar") tab.value = value.field.startsWith("period") ? "periods" : "calendar"
    },
    { flush: "sync" },
)
</script>
<template>
    <UiTabs :model-value="`${tabPrefix}-${tab}`" :items="tabs" :disabled="disabled" @update:model-value="changeTab" />
    <section v-show="tab === 'calendar'" data-calendar-panel="calendar" role="region" :aria-labelledby="`tab-${tabPrefix}-calendar`">
        <div v-if="$slots['calendar-actions']" class="scaffold-toolbar"><slot name="calendar-actions" /></div>
        <CalendarTableFields
            v-model:calendar="calendar"
            v-model:periods="periods"
            v-model:calendar-enabled="calendarEnabled"
            :disabled="disabled"
            :operation-locked="operationLocked"
            :details="details"
            :bridge="bridge"
            :preview-context="previewContext"
            :preview-invalidation-key="previewInvalidationKey"
            :independent="independent"
            :show-enable-switch="showEnableSwitch"
            @dialog="emit('dialog', $event)"
        />
    </section>
    <section v-show="tab === 'periods'" data-calendar-panel="periods" role="region" :aria-labelledby="`tab-${tabPrefix}-periods`">
        <div v-if="$slots['periods-actions']" class="scaffold-toolbar"><slot name="periods-actions" /></div>
        <PeriodsFields
            v-model:calendar="calendar"
            v-model:periods="periods"
            v-model:calendar-enabled="calendarEnabled"
            v-model:periods-enabled="periodsEnabled"
            :disabled="disabled"
            :definitions="definitions"
            :details="details"
            :measures="independent ? [] : measures"
            :independent="independent"
            :show-enable-switch="showEnableSwitch"
        />
    </section>
</template>
