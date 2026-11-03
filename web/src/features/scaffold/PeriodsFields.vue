<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\PeriodsFields.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 时间智能实际绑定、平铺分组选择及声明依赖展示.
-->
<script setup lang="ts">
import { computed } from "vue"
import ScaffoldField from "./ScaffoldField.vue"
import { calendarColumnOutputName, calendarDateColumn, selectedCalendarFields } from "./calendarFields"
import { vNumberAccessibility } from "@/ui/numberAccessibility"
import { useI18n } from "@/composables/i18n/useI18n"
import type { ScaffoldCalendarOptions, ScaffoldPeriodOptions, ScaffoldPeriodTemplate, TemplateDetails, TemplateMeasureDefinition } from "./contracts"
/** 时间智能绑定仅从实际目录和启用配置生成选项, 分组选取保留其他组. */
const calendar = defineModel<ScaffoldCalendarOptions>("calendar", { required: true })
const periods = defineModel<ScaffoldPeriodOptions>("periods", { required: true })
const calendarEnabled = defineModel<boolean>("calendarEnabled", { required: true })
const periodsEnabled = defineModel<boolean>("periodsEnabled", { required: true })
const props = withDefaults(
    defineProps<{
        disabled: boolean
        definitions: ScaffoldPeriodTemplate[]
        details?: TemplateDetails
        measures: TemplateMeasureDefinition[]
        independent?: boolean
        /** 默认显示新建可选开关, 模型动作模式传 false 隐藏, 不改变字段的真实禁用条件. */
        showEnableSwitch?: boolean
    }>(),
    { showEnableSwitch: true },
)
const { t, locale } = useI18n()
const capability = computed(() => props.details?.calendarCapabilities?.find((item) => item.tableName === calendar.value.tableName))
const groups = computed(() => [...new Set(props.definitions.map((item) => item.group))])
const tables = computed(() => props.details?.tables ?? [])
const dateTables = computed(() => [
    ...new Set([
        ...tables.value.filter((table) => table.columns.some((column) => /date/i.test(column.dataType))).map((table) => table.name),
        ...(!props.independent && calendarEnabled.value ? [calendar.value.tableName] : []),
    ]),
])
const dateColumns = computed(() => {
    const generated = !props.independent && periods.value.dateTable === calendar.value.tableName && calendarEnabled.value
    if (generated && capability.value?.mode !== "existing") return [calendarDateColumn(calendar.value, capability.value)]
    return [
        ...new Set([
            ...(tables.value
                .find((table) => table.name === periods.value.dateTable)
                ?.columns.filter((column) => /date/i.test(column.dataType))
                .flatMap((column) => {
                    const mapping = generated ? capability.value?.columns.find((item) => item.name === column.name) : undefined
                    if (!mapping) return [column.name]
                    return selectedCalendarFields(calendar.value).includes(mapping.field)
                        ? [calendarColumnOutputName(mapping.field, column.name, calendar.value.numberedColumns !== false)]
                        : []
                }) ?? []),
            ...(generated ? [calendarDateColumn(calendar.value, capability.value)] : []),
        ]),
    ]
})
const baseMeasures = computed(() => [
    ...new Set([...tables.value.flatMap((table) => table.measures), ...props.measures.filter((item) => item.enabled).map((item) => item.name)]),
])
const dependencies = computed(() => {
    const explicit = new Set(periods.value.templates),
        result = new Set<string>()
    /** 递归显示声明的中间依赖, 遇到已访问节点结束. */
    function visit(id: string): void {
        for (const dependency of props.definitions.find((item) => item.id === id)?.dependencies ?? [])
            if (!result.has(dependency)) {
                result.add(dependency)
                visit(dependency)
            }
    }
    for (const id of explicit) visit(id)
    return [...result].filter((id) => !explicit.has(id))
})
/** 组选或清空只影响显式选择, 保留其他组. */
function selectGroup(group: string, selected: boolean): void {
    const ids = props.definitions.filter((item) => item.group === group).map((item) => item.id)
    periods.value.templates = [...new Set([...periods.value.templates.filter((id) => !ids.includes(id)), ...(selected ? ids : [])])]
}
/** 从主机标签生成英文期间显示, 标识仍保持后端 ID. */
function groupLabel(group: string): string {
    return t(
        `scaffold.${({ Day: "dayGroup", Week: "weekGroup", Month: "monthGroup", Quarter: "quarterGroup", HalfYear: "halfGroup", Year: "yearGroup", 日: "dayGroup", "日/当前值": "dayGroup", 周: "weekGroup", 月: "monthGroup", 季度: "quarterGroup", 半年: "halfGroup", 年度: "yearGroup", 年: "yearGroup" } as Record<string, string>)[group] ?? "functions"}`,
    )
}
</script>
<template>
    <label v-if="showEnableSwitch !== false" class="scaffold-switch"
        ><el-switch
            :model-value="periodsEnabled"
            @update:model-value="periodsEnabled = $event === true"
            :class="{ 'operation-lock': disabled }"
            :disabled="disabled"
            :aria-label="t('scaffold.periodsEnable')"
            data-optional
        />{{ t("scaffold.periodsEnable") }}</label
    >
    <div class="scaffold-grid">
        <ScaffoldField field="periodDateTable"
            >{{ t("scaffold.dateTable")
            }}<el-select
                v-model="periods.dateTable"
                :class="{ 'operation-lock': disabled && periodsEnabled }"
                :disabled="disabled || !periodsEnabled"
                filterable
                @change="periods.dateColumn = ''"
                ><el-option v-for="name in dateTables" :key="name" :value="name" /></el-select
        ></ScaffoldField>
        <ScaffoldField field="periodDateColumn"
            >{{ t("scaffold.dateColumn")
            }}<el-select
                v-model="periods.dateColumn"
                :class="{ 'operation-lock': disabled && periodsEnabled }"
                :disabled="disabled || !periodsEnabled"
                filterable
                ><el-option v-for="name in dateColumns" :key="name" :value="name" /></el-select
        ></ScaffoldField>
        <ScaffoldField field="periodMeasureTable"
            >{{ t("scaffold.measureTable")
            }}<el-select
                v-model="periods.measureTable"
                :class="{ 'operation-lock': disabled && periodsEnabled }"
                :disabled="disabled || !periodsEnabled"
                filterable
                ><el-option v-for="table in tables" :key="table.name" :value="table.name" /></el-select
        ></ScaffoldField>
        <ScaffoldField field="periodBaseMeasures"
            >{{ t("scaffold.baseMeasures")
            }}<el-select
                v-model="periods.baseMeasures"
                :class="{ 'operation-lock': disabled && periodsEnabled }"
                :disabled="disabled || !periodsEnabled"
                multiple
                filterable
                ><el-option v-for="name in baseMeasures" :key="name" :value="name" /></el-select
        ></ScaffoldField>
        <ScaffoldField field="periodFolder"
            >{{ t("scaffold.folder")
            }}<el-input v-model="periods.folder" :class="{ 'operation-lock': disabled && periodsEnabled }" :disabled="disabled || !periodsEnabled"
        /></ScaffoldField>
        <ScaffoldField field="periodNumberDigits"
            >{{ t("scaffold.numberDigits")
            }}<el-input-number
                v-number-accessibility
                :model-value="periods.numberDigits"
                @update:model-value="periods.numberDigits = $event ?? 0"
                :min="0"
                :max="8"
                :class="{ 'operation-lock': disabled && periodsEnabled }"
                :disabled="disabled || !periodsEnabled"
        /></ScaffoldField>
        <ScaffoldField field="periodPercentDigits"
            >{{ t("scaffold.percentDigits")
            }}<el-input-number
                v-number-accessibility
                :model-value="periods.percentDigits"
                @update:model-value="periods.percentDigits = $event ?? 0"
                :min="0"
                :max="8"
                :class="{ 'operation-lock': disabled && periodsEnabled }"
                :disabled="disabled || !periodsEnabled"
        /></ScaffoldField>
    </div>
    <ScaffoldField field="periodTemplates" tag="div" error-first>
        <section v-for="group in groups" :key="group" class="scaffold-period-group">
            <div class="scaffold-toolbar">
                <h3>{{ groupLabel(group) }}</h3>
                <el-button
                    :class="{ 'operation-lock': disabled && periodsEnabled }"
                    :disabled="disabled || !periodsEnabled"
                    @click="selectGroup(group, true)"
                    >{{ t("scaffold.selectGroup") }}</el-button
                ><el-button
                    :class="{ 'operation-lock': disabled && periodsEnabled }"
                    :disabled="disabled || !periodsEnabled"
                    @click="selectGroup(group, false)"
                    >{{ t("scaffold.clearGroup") }}</el-button
                >
            </div>
            <el-checkbox-group
                :model-value="periods.templates"
                @update:model-value="periods.templates = $event.map(String)"
                :class="{ 'operation-lock': disabled && periodsEnabled }"
                :disabled="disabled || !periodsEnabled"
                ><el-checkbox-button
                    v-for="item in definitions.filter((definition) => definition.group === group)"
                    :key="item.id"
                    :value="item.id"
                    :class="{ 'operation-lock': disabled && periodsEnabled }"
                    :title="locale === 'zh-cn' ? item.description : item.id"
                    >{{ locale === "zh-cn" ? item.label : item.id }}</el-checkbox-button
                ></el-checkbox-group
            >
        </section>
        <p v-if="dependencies.length">{{ t("scaffold.dependencyHint") }}: {{ dependencies.join(", ") }}</p>
    </ScaffoldField>
</template>
