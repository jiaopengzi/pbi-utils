<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\WorkdayOverrides.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 逐日工作日覆盖的分页编辑及批量导入弹窗.
-->
<script setup lang="ts">
import { computed, inject, ref, shallowRef, watch } from "vue"
import ScaffoldField from "./ScaffoldField.vue"
import WorkdayImportDialog from "./WorkdayImportDialog.vue"
import { scaffoldValidationKey } from "./validationContext"
import { useI18n } from "@/composables/i18n/useI18n"
import type { BridgeClient } from "@/bridge/client"
import type { ScaffoldCalendarOptions } from "./contracts"
/** 工作日覆盖编辑保留绝对行号与50行分页, 批量导入仅确认后合并. */
const calendar = defineModel<ScaffoldCalendarOptions>("calendar", { required: true })
const props = defineProps<{ disabled: boolean; calendarEnabled: boolean; bridge?: BridgeClient }>()
const emit = defineEmits<{ dialog: [value: boolean] }>()
const { t } = useI18n()
const importing = ref(false)
const overridePage = ref(1)
const overridePageSize = 50
const validation = inject(scaffoldValidationKey, shallowRef(undefined))
const visibleOverrides = computed(() =>
    calendar.value.overrides
        .slice((overridePage.value - 1) * overridePageSize, overridePage.value * overridePageSize)
        .map((item, offset) => ({ item, index: (overridePage.value - 1) * overridePageSize + offset })),
)
watch(
    () => validation.value,
    (value) => {
        if (value?.step === "calendar" && value.row && value.field.startsWith("override"))
            overridePage.value = Math.floor((value.row - 1) / overridePageSize) + 1
    },
    { flush: "sync" },
)
watch(
    () => calendar.value.overrides.length,
    (length) => {
        overridePage.value = Math.min(overridePage.value, Math.max(1, Math.ceil(length / overridePageSize)))
    },
)
/** 弹窗期间由外层统一锁导航, 原草稿只在确认合并后更新. */
function setImporting(value: boolean): void {
    importing.value = value
    emit("dialog", value)
}
/** 新增覆盖后显示末页, 行级校验仍使用完整草稿中的绝对位置. */
function addOverride(): void {
    calendar.value.overrides.push({ date: "", isWorkday: true, name: "" })
    overridePage.value = Math.ceil(calendar.value.overrides.length / overridePageSize)
}
</script>
<template>
    <h3>{{ t("scaffold.workdays") }}</h3>
    <el-button v-if="bridge" :disabled="disabled || !calendarEnabled" :class="{ 'operation-lock': disabled && calendarEnabled }" @click="setImporting(true)">{{
        t("scaffold.importWorkdays")
    }}</el-button>
    <WorkdayImportDialog
        v-if="bridge"
        :model-value="importing"
        @update:model-value="setImporting"
        :bridge="bridge"
        :overrides="calendar.overrides"
        :start-year="calendar.startYear"
        :end-year="calendar.endYear"
        @apply="calendar.overrides = $event"
    />
    <ScaffoldField field="overrides" tag="div" error-first>
        <div class="scaffold-table-scroll">
            <table class="scaffold-table scaffold-records">
                <thead>
                    <tr>
                        <th>{{ t("scaffold.date") }}</th>
                        <th>{{ t("scaffold.isWorkday") }}</th>
                        <th>{{ t("scaffold.holidayName") }}</th>
                        <th>{{ t("scaffold.remove") }}</th>
                    </tr>
                </thead>
                <tbody>
                    <tr v-for="{ item, index } in visibleOverrides" :key="index">
                        <ScaffoldField tag="td" field="overrideDate" :row="index + 1" :data-label="t('scaffold.date')">
                            <el-date-picker
                                v-model="item.date"
                                type="date"
                                value-format="YYYY-MM-DD"
                                :class="{ 'operation-lock': disabled && calendarEnabled }"
                                :disabled="disabled || !calendarEnabled"
                                :aria-label="t('scaffold.date')"
                            />
                        </ScaffoldField>
                        <ScaffoldField tag="td" field="overrideIsWorkday" :row="index + 1" :data-label="t('scaffold.isWorkday')">
                            <el-checkbox
                                :model-value="item.isWorkday"
                                @update:model-value="item.isWorkday = $event === true"
                                :class="{ 'operation-lock': disabled && calendarEnabled }"
                                :disabled="disabled || !calendarEnabled"
                                :aria-label="t('scaffold.isWorkday')"
                            />
                        </ScaffoldField>
                        <ScaffoldField tag="td" field="overrideName" :row="index + 1" :data-label="t('scaffold.holidayName')">
                            <el-input
                                v-model="item.name"
                                :class="{ 'operation-lock': disabled && calendarEnabled }"
                                :disabled="disabled || !calendarEnabled"
                                :aria-label="t('scaffold.holidayName')"
                            />
                        </ScaffoldField>
                        <td :data-label="t('scaffold.remove')">
                            <el-button
                                :class="{ 'operation-lock': disabled && calendarEnabled }"
                                :disabled="disabled || !calendarEnabled"
                                @click="calendar.overrides.splice(index, 1)"
                                >{{ t("scaffold.remove") }}</el-button
                            >
                        </td>
                    </tr>
                </tbody>
            </table>
        </div>
        <el-pagination
            v-if="calendar.overrides.length > overridePageSize"
            v-model:current-page="overridePage"
            :page-size="overridePageSize"
            :total="calendar.overrides.length"
            layout="prev, pager, next, total"
            :disabled="disabled || !calendarEnabled"
            :aria-label="t('scaffold.workdayPages')"
        />
        <el-button :class="{ 'operation-lock': disabled && calendarEnabled }" :disabled="disabled || !calendarEnabled" @click="addOverride">{{
            t("scaffold.add")
        }}</el-button>
    </ScaffoldField>
</template>
