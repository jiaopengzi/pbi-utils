<!--
 * FilePath    : pbi-utils\web\src\features\configuration\ConfigurationFields.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 新建与现有模型共用可选配置字段, 仅编辑草稿并保留真实字段校验定位.
-->
<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
import { computed } from "vue"
import MeasuresEditor from "../scaffold/MeasuresEditor.vue"
import MeasuresMode from "../scaffold/MeasuresMode.vue"
import CalendarStep from "../scaffold/CalendarStep.vue"
import UdfStep from "../scaffold/UdfStep.vue"
import ScaffoldField from "../scaffold/ScaffoldField.vue"
import { vNumberAccessibility } from "@/ui/numberAccessibility"
import type { BridgeClient } from "@/bridge/client"
import type { CalendarPreviewContext, ScaffoldPeriodTemplate, ModelConfigurationCatalog, TemplateMeasureMode } from "../scaffold/contracts"
import type { ConfigurationDraft, ConfigurationOperation } from "./draft"
/** 配置展示由呈现器传入目标目录和锁, 本组件不创建或切换模型. */
const props = defineProps<{
    draft: ConfigurationDraft
    bridge: BridgeClient
    step: string
    visited: Set<string>
    idPrefix: string
    labelledBy?: string
    disabled: boolean
    locked: boolean
    operationLocked?: boolean
    definitions: ScaffoldPeriodTemplate[]
    details?: ModelConfigurationCatalog
    canGenerateSample?: boolean
    canUseMeasureConfig?: boolean
    measureConfigUnavailableReason?: "layout" | "invalid" | null
    previewContext?: CalendarPreviewContext
    /** 现有模型通过独立应用按钮提交, 四个配置段始终启用且不展示可选开关; 新建仍保留开关. */
    modelConfiguration?: boolean
}>()
const emit = defineEmits<{ busy: [value: boolean]; dialog: [value: boolean]; udfValidity: [value: boolean]; measureMode: [value: TemplateMeasureMode] }>()
defineSlots<{ actions(props: { operation: ConfigurationOperation; canSubmit?: boolean }): unknown }>()
const { t } = useI18n()
/** 模拟数据编辑只使代码预览失效, 不复制 M 原文或覆盖列表, 不生成数据或写入偏好. */
const previewInvalidationKey = computed(() => JSON.stringify({ enabled: props.draft.sampleEnabled, sample: props.draft.sample }))
/** 新建只切换同一输入的来源, 模型由上层恢复该来源草稿; 真实锁和不可用来源均拒绝事件. */
function selectMeasureMode(value: TemplateMeasureMode): void {
    if (props.disabled || !props.draft.measuresEnabled || (value === "config" && props.modelConfiguration && props.canUseMeasureConfig !== true)) return
    if (props.modelConfiguration) emit("measureMode", value)
    else props.draft.measuresMode = value
}
</script>
<template>
    <section :id="`section-${idPrefix}-sample`" v-show="step === 'sample'" :aria-labelledby="labelledBy ?? `step-${idPrefix}-sample`">
        <div v-if="$slots.actions" class="scaffold-toolbar"><slot name="actions" operation="sample" /></div>
        <label class="scaffold-switch"
            ><el-switch
                :model-value="draft.sampleEnabled"
                @update:model-value="draft.sampleEnabled = $event === true"
                :class="{ 'operation-lock': locked }"
                :disabled="locked || canGenerateSample === false"
                :aria-label="t('scaffold.optional')"
                data-optional
            />{{ t("scaffold.optional") }}</label
        >
        <p v-if="canGenerateSample === false" class="muted">{{ t("configuration.sampleUnavailable") }}</p>
        <p class="muted">{{ t("scaffold.sampleHint") }} {{ t("scaffold.skip") }}</p>
        <div v-if="visited.has('sample')" class="scaffold-grid">
            <ScaffoldField field="productCount"
                >{{ t("scaffold.productCount")
                }}<el-input-number
                    v-number-accessibility
                    :model-value="draft.sample.productCount"
                    @update:model-value="draft.sample.productCount = $event ?? 0"
                    :min="1"
                    :max="10000"
                    :class="{ 'operation-lock': locked && draft.sampleEnabled }"
                    :disabled="locked || !draft.sampleEnabled" /></ScaffoldField
            ><ScaffoldField field="storeCount"
                >{{ t("scaffold.storeCount")
                }}<el-input-number
                    v-number-accessibility
                    :model-value="draft.sample.storeCount"
                    @update:model-value="draft.sample.storeCount = $event ?? 0"
                    :min="1"
                    :max="10000"
                    :class="{ 'operation-lock': locked && draft.sampleEnabled }"
                    :disabled="locked || !draft.sampleEnabled" /></ScaffoldField
            ><ScaffoldField field="inventoryCycle"
                >{{ t("scaffold.inventoryCycle")
                }}<el-input-number
                    v-number-accessibility
                    :model-value="draft.sample.inventoryCycle"
                    @update:model-value="draft.sample.inventoryCycle = $event ?? 0"
                    :min="5"
                    :max="180"
                    :class="{ 'operation-lock': locked && draft.sampleEnabled }"
                    :disabled="locked || !draft.sampleEnabled" /></ScaffoldField
            ><ScaffoldField field="startDate"
                >{{ t("scaffold.startDate")
                }}<el-date-picker
                    v-model="draft.sample.startDate"
                    type="date"
                    value-format="YYYY-MM-DD"
                    :class="{ 'operation-lock': locked && draft.sampleEnabled }"
                    :disabled="locked || !draft.sampleEnabled" /></ScaffoldField
            ><ScaffoldField field="endDate"
                >{{ t("scaffold.endDate")
                }}<el-date-picker
                    v-model="draft.sample.endDate"
                    type="date"
                    value-format="YYYY-MM-DD"
                    :class="{ 'operation-lock': locked && draft.sampleEnabled }"
                    :disabled="locked || !draft.sampleEnabled" /></ScaffoldField
            ><ScaffoldField field="seed"
                >{{ t("scaffold.seed")
                }}<el-input-number
                    v-number-accessibility
                    :model-value="draft.sample.seed"
                    @update:model-value="draft.sample.seed = $event ?? 0"
                    :min="0"
                    :max="2147483647"
                    :class="{ 'operation-lock': locked && draft.sampleEnabled }"
                    :disabled="locked || !draft.sampleEnabled"
            /></ScaffoldField>
        </div>
    </section>
    <section :id="`section-${idPrefix}-measures`" v-show="step === 'measures'" :aria-labelledby="labelledBy ?? `step-${idPrefix}-measures`">
        <div v-if="$slots.actions" class="scaffold-toolbar"><slot name="actions" operation="measures" /></div>
        <label v-if="!modelConfiguration" class="scaffold-switch"
            ><el-switch
                :model-value="draft.measuresEnabled"
                @update:model-value="draft.measuresEnabled = $event === true"
                :class="{ 'operation-lock': locked }"
                :disabled="locked"
                :aria-label="t('scaffold.optional')"
                data-optional
            />{{ t("scaffold.optional") }}</label
        >
        <p v-if="!modelConfiguration" class="muted">{{ t("scaffold.skip") }}</p>
        <MeasuresMode
            :model-value="draft.measuresMode"
            @update:model-value="selectMeasureMode"
            :disabled="disabled || !draft.measuresEnabled"
            :operation-locked="operationLocked && draft.measuresEnabled"
            :config-available="!modelConfiguration || canUseMeasureConfig === true"
            :unavailable-reason="measureConfigUnavailableReason"
        />
        <ScaffoldField field="measures" tag="div" error-first>
            <MeasuresEditor
                v-if="visited.has('measures')"
                :operation-locked="disabled && draft.measuresEnabled"
                v-model="draft.measures"
                :disabled="disabled || !draft.measuresEnabled"
                @dialog="emit('dialog', $event)"
            />
        </ScaffoldField>
    </section>
    <section :id="`section-${idPrefix}-calendar`" v-show="step === 'calendar'" :aria-labelledby="labelledBy ?? `step-${idPrefix}-calendar`">
        <CalendarStep
            v-if="visited.has('calendar')"
            v-model:calendar="draft.calendar"
            v-model:periods="draft.periods"
            v-model:calendar-enabled="draft.calendarEnabled"
            v-model:periods-enabled="draft.periodsEnabled"
            :disabled="locked"
            :operation-locked="operationLocked"
            :bridge="bridge"
            :preview-context="previewContext"
            :preview-invalidation-key="previewInvalidationKey"
            @dialog="emit('dialog', $event)"
            :definitions="definitions"
            :details="details"
            :measures="draft.measuresEnabled ? draft.measures : []"
            :independent="modelConfiguration"
            :show-enable-switch="!modelConfiguration"
        >
            <template v-if="$slots.actions" #calendar-actions><slot name="actions" operation="calendar" /></template>
            <template v-if="$slots.actions" #periods-actions><slot name="actions" operation="periods" /></template>
        </CalendarStep>
    </section>
    <section :id="`section-${idPrefix}-udfs`" v-show="step === 'udfs'" :aria-labelledby="labelledBy ?? `step-${idPrefix}-udfs`">
        <label v-if="!modelConfiguration" class="scaffold-switch"
            ><el-switch
                :model-value="draft.udfsEnabled"
                @update:model-value="draft.udfsEnabled = $event === true"
                :class="{ 'operation-lock': locked }"
                :disabled="locked"
                :aria-label="t('scaffold.optional')"
                data-optional
            />{{ t("scaffold.optional") }}</label
        >
        <p v-if="!modelConfiguration" class="muted">{{ t("scaffold.skip") }}</p>
        <ScaffoldField field="udfs" tag="div" error-first>
            <UdfStep
                v-if="visited.has('udfs')"
                :operation-locked="locked && draft.udfsEnabled"
                v-model="draft.udfs"
                :bridge="bridge"
                :disabled="locked || !draft.udfsEnabled"
                @busy="emit('busy', $event)"
                @validity="emit('udfValidity', $event)"
            >
                <template v-if="$slots.actions" #actions="{ canSubmit }"><slot name="actions" operation="udfs" :can-submit="canSubmit" /></template>
            </UdfStep>
        </ScaffoldField>
    </section>
</template>
