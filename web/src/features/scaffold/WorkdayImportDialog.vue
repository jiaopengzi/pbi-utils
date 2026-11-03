<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\WorkdayImportDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 内容限定的工作日导入弹窗, 宿主整批解析和冲突预览后仅修改草稿.
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import UiDialog from "@/ui/UiDialog.vue"
import type { WorkdayOverride } from "./contracts"
import { mergeWorkdays, previewWorkdays, workdayImportMessage, workdaySourceLines } from "./workdayImport"
const open = defineModel<boolean>({ required: true })
const props = defineProps<{ bridge: BridgeClient; overrides: WorkdayOverride[]; startYear: number; endYear: number }>()
const emit = defineEmits<{ apply: [items: WorkdayOverride[]] }>()
const { t, hostText } = useI18n()
const text = ref(""),
    delimiter = ref<"csv" | "tsv">("csv"),
    replace = ref(false),
    busy = ref(false),
    error = ref(""),
    parsed = ref<WorkdayOverride[]>()
const preview = computed(() => (parsed.value ? previewWorkdays(props.overrides, parsed.value) : []))
const sourceLines = computed(() => workdaySourceLines(text.value, delimiter.value))
const budgetError = computed(() => {
    if (!parsed.value) return ""
    try {
        mergeWorkdays(props.overrides, parsed.value, replace.value)
        return ""
    } catch (cause) {
        return workdayImportMessage(cause instanceof Error ? cause.message : t("scaffold.importFailed"), t)
    }
})
const summary = computed(() => ({
    added: preview.value.filter((item) => item.state === "added").length,
    same: preview.value.filter((item) => item.state === "same").length,
    conflict: preview.value.filter((item) => item.state === "conflict").length,
}))
let generation = 0,
    disposed = false
watch([text, delimiter, () => props.startYear, () => props.endYear], () => {
    parsed.value = undefined
    error.value = ""
    generation++
})
watch(open, (value) => {
    if (value) {
        text.value = ""
        parsed.value = undefined
        error.value = ""
        replace.value = false
    } else generation++
})
onBeforeUnmount(() => {
    disposed = true
    generation++
})
/** UTF-8 只读所选文件, 不保留或向宿主发送路径; 非法编码和超限整批拒绝. */
async function chooseFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement
    const file = input.files?.[0]
    input.value = ""
    if (!file) return
    parsed.value = undefined
    error.value = ""
    generation++
    if (file.size > 2 * 1024 * 1024) {
        error.value = t("scaffold.importTextBudget")
        return
    }
    const current = ++generation
    try {
        const content = new TextDecoder("utf-8", { fatal: true }).decode(await file.arrayBuffer())
        if (disposed || current !== generation) return
        text.value = content
        delimiter.value = /\.tsv$/i.test(file.name) ? "tsv" : "csv"
    } catch {
        if (!disposed && current === generation) error.value = t("scaffold.importEncoding")
    }
}
/** 预览必须消费宿主权威解析, 任一非法行不允许部分导入. */
async function parse(): Promise<void> {
    parsed.value = undefined
    error.value = ""
    if (new TextEncoder().encode(text.value).length > 2 * 1024 * 1024) {
        error.value = t("scaffold.importTextBudget")
        return
    }
    const current = ++generation
    busy.value = true
    try {
        const result = await props.bridge.request("calendar.parseWorkdays", {
            text: text.value,
            delimiter: delimiter.value,
            startYear: props.startYear,
            endYear: props.endYear,
        }).promise
        if (disposed || current !== generation || !open.value) return
        parsed.value = result.overrides
    } catch (cause) {
        if (!disposed && current === generation)
            error.value = workdayImportMessage(hostText(cause instanceof Error ? cause.message : t("scaffold.importFailed")), t)
    } finally {
        if (!disposed) busy.value = false
    }
}
/** 确认时重新与当前草稿比较预算, 取消或校验失败不触碰原数组. */
function apply(): void {
    if (!parsed.value || busy.value) return
    try {
        emit("apply", mergeWorkdays(props.overrides, parsed.value, replace.value))
        open.value = false
    } catch (cause) {
        error.value = workdayImportMessage(cause instanceof Error ? cause.message : t("scaffold.importFailed"), t)
    }
}
</script>
<template>
    <UiDialog v-model="open" :title="t('scaffold.importWorkdays')" :busy="busy">
        <p>{{ t("scaffold.importWorkdayHint") }}</p>
        <label class="workday-import__file"
            >{{ t("scaffold.importChooseFile") }}<input type="file" accept=".csv,.tsv,text/csv,text/tab-separated-values" :disabled="busy" @change="chooseFile"
        /></label>
        <label class="workday-import__field"
            >{{ t("scaffold.importDelimiter")
            }}<el-select v-model="delimiter" :disabled="busy"><el-option value="csv" label="CSV" /><el-option value="tsv" label="TSV" /></el-select
        ></label>
        <label class="workday-import__field"
            >{{ t("scaffold.importPaste")
            }}<el-input v-model="text" type="textarea" :rows="7" :disabled="busy" :placeholder="'date,isWorkday,name\n2026-01-01,false,'"
        /></label>
        <p v-if="error || budgetError" role="alert" class="scaffold-field__error">{{ error || budgetError }}</p>
        <el-button :disabled="busy || !text" @click="parse">{{ t("scaffold.importPreview") }}</el-button>
        <template v-if="parsed">
            <p role="status">{{ t("scaffold.importSummary", summary) }}</p>
            <el-checkbox :model-value="replace" @update:model-value="replace = $event === true" :disabled="busy">{{ t("scaffold.importReplace") }}</el-checkbox>
            <div class="scaffold-table-scroll workday-import__preview">
                <table class="scaffold-table">
                    <thead>
                        <tr>
                            <th>{{ t("scaffold.importSourceLine") }}</th>
                            <th>{{ t("scaffold.date") }}</th>
                            <th>{{ t("scaffold.isWorkday") }}</th>
                            <th>{{ t("scaffold.holidayName") }}</th>
                            <th>{{ t("scaffold.state") }}</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-for="row in preview" :key="row.item.date">
                            <td>{{ sourceLines.get(row.item.date) ?? "-" }}</td>
                            <td>{{ row.item.date }}</td>
                            <td>{{ t(row.item.isWorkday ? "scaffold.workdayYes" : "scaffold.workdayNo") }}</td>
                            <td>
                                {{ row.item.name
                                }}<span v-if="row.state === 'conflict'">
                                    ({{ t("scaffold.importExisting") }}: {{ row.existing?.name }},
                                    {{ t(row.existing?.isWorkday ? "scaffold.workdayYes" : "scaffold.workdayNo") }})</span
                                >
                            </td>
                            <td>{{ t(`scaffold.importState${row.state}`) }}</td>
                        </tr>
                    </tbody>
                </table>
            </div>
        </template>
        <template #footer
            ><el-button :disabled="busy" @click="open = false">{{ t("scaffold.cancel") }}</el-button
            ><el-button type="primary" :disabled="busy || !parsed || !!budgetError" @click="apply">{{ t("scaffold.importApply") }}</el-button></template
        >
    </UiDialog>
</template>
<style scoped lang="scss">
.workday-import__field,
.workday-import__file {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-block: 12px;
}
.workday-import__preview {
    max-height: 260px;
    margin-top: 12px;
}
</style>
