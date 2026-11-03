<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfLocalBrowser.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地同名逻辑包的名称模糊分页、当前tag选择与按需只读查看, 编辑继续使用独立本地管理器.
-->
<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import UiDialog from "@/ui/UiDialog.vue"
import CodeBlock from "@/features/code/CodeBlock.vue"
import UdfFunctionRow from "./UdfFunctionRow.vue"
import { groupLocalUdfPackages, preferredLocalUdfEntry, type LocalUdfPackage } from "./udfLocalPackages"
import UdfFunctionPageActions from "./UdfFunctionPageActions.vue"
import UdfMarkdown from "./UdfMarkdown.vue"
import { matchUdfName } from "./udfSearch"
import "./udf-detail.scss"
import type { LocalUdfEntry, UdfSelection } from "./contracts"
/** 本地数据和远端快照分离; 过滤/分页不修改稳定ID、定义和隐藏选择. */
const props = defineProps<{
    items: LocalUdfEntry[]
    selections: UdfSelection[]
    query: string
    disabled: boolean
    operationLocked?: boolean
    busy?: boolean
    embedded?: boolean
    activeVersions?: Record<string, string>
}>()
const emit = defineEmits<{
    select: [item: LocalUdfEntry, checked: boolean]
    selectPage: [items: LocalUdfEntry[]]
    edit: [item: LocalUdfEntry]
    changeVersion: [next: LocalUdfEntry, previous: LocalUdfEntry]
}>()
const detailsOpen = defineModel<boolean>("detailsOpen", { default: false })
const { t } = useI18n(),
    page = ref(1),
    size = ref(50),
    detail = shallowRef<LocalUdfEntry>(),
    localTags = ref<Record<string, string>>({})
let disposed = false
onBeforeUnmount(() => {
    disposed = true
})
/** 显式展示tag优先; 没有展示草稿时使用已选GUID和包默认规则, 不由展示变化替换已选项. */
function current(group: LocalUdfPackage): LocalUdfEntry {
    const selected = props.selections.find((selection) => selection.source === "local" && group.versions.some((item) => item.id === selection.packageId))
    return preferredLocalUdfEntry(group, (props.activeVersions ?? localTags.value)[group.key] ?? selected?.packageId)
}
const filtered = computed(() =>
    groupLocalUdfPackages(props.items)
        .map((group) => {
            const item = current(group)
            return { group, item, matches: matchUdfName(item.definition.name, props.query) }
        })
        .filter((entry) => entry.matches !== null)
        .toSorted((first, second) => Number(first.matches!.length > 1) - Number(second.matches!.length > 1)),
)
/** 版本变化只发精确next/previous, 已选包等父级依赖核验成功才改变受控tag; 失败保持原值. */
function changeVersion(group: LocalUdfPackage, id: string): void {
    if (props.disabled) return
    const previous = current(group),
        next = group.versions.find((item) => item.id === id)
    if (!next || next.id === previous.id) return
    const selected = props.selections.some((selection) => selection.source === "local" && group.versions.some((item) => item.id === selection.packageId))
    if (!selected && props.activeVersions === undefined) localTags.value = { ...localTags.value, [group.key]: id }
    emit("changeVersion", next, previous)
}
const visible = computed(() => filtered.value.slice((page.value - 1) * size.value, page.value * size.value))
watch(
    () => props.query,
    () => {
        page.value = 1
    },
)
watch(
    () => props.items,
    () => {
        page.value = Math.min(page.value, Math.max(1, Math.ceil(filtered.value.length / size.value)))
    },
)
/** 禁用后拒绝迟到事件; 详情只显示读取时冻结的原文, 编辑仍重新读取宿主基线. */
function select(item: LocalUdfEntry, checked: boolean): void {
    if (!props.disabled) emit("select", item, checked)
}
/** 冻结本次可见页的精确条目, 不改变搜索隐藏或其他页的选择; 父级原子验证完整候选. */
function selectPage(): void {
    if (!props.disabled && visible.value.length)
        emit(
            "selectPage",
            visible.value.map((entry) => entry.item),
        )
}
function view(item: LocalUdfEntry): void {
    if (!props.disabled) {
        detail.value = item
        detailsOpen.value = true
    }
}
/** 先关闭详情并等待父级释放该模态的锁, 再按当前真实禁用状态交接编辑; 卸载/重复事件不发请求. */
async function edit(): Promise<void> {
    const item = detail.value
    if (props.busy || !detailsOpen.value || !item) return
    detailsOpen.value = false
    await nextTick()
    if (!disposed && !props.disabled && !props.busy && !detailsOpen.value && detail.value === item) emit("edit", item)
}
function changePage(value: number): void {
    if (!props.disabled && Number.isInteger(value) && value > 0) page.value = value
}
function changeSize(value: number): void {
    if (!props.disabled && [50, 100, 200, 500].includes(value)) {
        size.value = value
        page.value = 1
    }
}
</script>
<template>
    <div class="udf-function-grid">
        <UdfFunctionRow
            v-for="entry in visible"
            :key="entry.group.key"
            :group="entry.group"
            :entry="entry.item"
            :matches="entry.matches ?? []"
            :disabled="disabled"
            :operation-locked="operationLocked"
            :checked="
                selections.some(
                    (selection) =>
                        selection.source === 'local' &&
                        selection.packageId === entry.item.id &&
                        selection.version === entry.item.definition.version &&
                        selection.baseline === entry.item.baseline &&
                        selection.functions.includes(entry.item.definition.name),
                )
            "
            @select="select(entry.item, $event)"
            @change-version="changeVersion(entry.group, $event)"
            @details="view(entry.item)"
        />
    </div>
    <p v-if="!filtered.length && !embedded" class="udf-empty">{{ t("scaffold.noFunctions") }}</p>
    <UdfFunctionPageActions
        v-if="!embedded || filtered.length"
        :page="page"
        :page-size="size"
        :total="filtered.length"
        :has-items="visible.length > 0"
        :disabled="disabled"
        :operation-locked="operationLocked"
        @select-page="selectPage"
        @change-page="changePage"
        @change-size="changeSize"
    />
    <UiDialog
        v-model="detailsOpen"
        class="udf-detail-dialog"
        width="min(1200px, calc(100vw - 32px))"
        :title="detail?.definition.name ?? t('scaffold.functionDetailTitle')"
        :busy="busy"
        :scroll-reset-key="detail ? JSON.stringify([detail.id, detail.definition.version, detail.baseline]) : undefined"
    >
        <template v-if="detailsOpen && detail"
            ><p class="muted">{{ detail.definition.version }}</p>
            <UdfMarkdown :source="detail.definition.description" />
            <CodeBlock :code="detail.definition.expression" language="dax" />
        </template>
        <template #footer
            ><el-button :disabled="busy" @click="detailsOpen = false">{{ t("scaffold.closeDetails") }}</el-button
            ><el-button :disabled="busy" @click="edit">{{ t("scaffold.edit") }}</el-button></template
        >
    </UiDialog>
</template>
