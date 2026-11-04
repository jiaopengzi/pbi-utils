<!--
 * FilePath    : pbi-utils\web\src\app\MeasureHierarchy.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 度量值目录、搜索展开与选择工具栏, 折叠状态独立于编辑草稿.
-->
<script setup lang="ts">
import { computed, shallowRef, watch } from "vue"
import { buildMeasureHierarchy, hierarchyGroupKeys, hierarchyMeasureKeys, type HierarchyMeasure } from "@/features/model/measureHierarchy"
import { useI18n } from "@/composables/i18n/useI18n"
import MeasureHierarchyNode from "./MeasureHierarchyNode.vue"

/** measures 为当前权威定义, query 仅过滤展示, selected 始终使用 table/name 身份; 所有事件不执行模型写入. */
const props = defineProps<{ measures: HierarchyMeasure[]; query: string; selected: string[]; selectedCount: number; disabled: boolean; currentKey?: string }>()
const emit = defineEmits<{ "update:selected": [selected: string[]]; select: [measure: HierarchyMeasure]; selectAll: [] }>()
const { t } = useI18n()
const collapsed = shallowRef<ReadonlySet<string>>(new Set())
const searchCollapsed = shallowRef<ReadonlySet<string>>(new Set())
const hierarchy = computed(() => buildMeasureHierarchy(props.measures, props.query))
const groups = computed(() => hierarchyGroupKeys(buildMeasureHierarchy(props.measures, "")))
const searching = computed(() => !!props.query.trim())
const visibleCollapsed = computed(() => (searching.value ? searchCollapsed.value : collapsed.value))
const visibleGroups = computed(() => hierarchyGroupKeys(hierarchy.value))
const allExpanded = computed(() => visibleGroups.value.length > 0 && visibleGroups.value.every((key) => !visibleCollapsed.value.has(key)))
// 每次新查询先展开匹配路径, 搜索中的手动折叠只影响本次结果, 清除查询恢复原目录状态.
watch(
    () => props.query,
    () => {
        searchCollapsed.value = new Set()
    },
)
// 权威回读后移除已消失目录, 新目录默认展开; 语言切换不重建目录身份或选择.
watch(groups, (keys) => {
    const existing = new Set(keys)
    collapsed.value = new Set([...collapsed.value].filter((key) => existing.has(key)))
})
/** 切换准确目录; 搜索中的折叠不覆盖搜索前的目录偏好. */
function toggle(key: string): void {
    if (props.disabled) return
    const target = searching.value ? searchCollapsed : collapsed
    const next = new Set(target.value)
    if (next.has(key)) next.delete(key)
    else next.add(key)
    target.value = next
}
/** 全量折叠/展开只作用于当前结果目录, 不清空选择或改动表单. */
function expandAll(expand: boolean): void {
    if (props.disabled) return
    const target = searching.value ? searchCollapsed : collapsed
    target.value = new Set(expand ? [] : visibleGroups.value)
}
/** 一次合并或移除本搜索子树的真实度量值身份, 保留目录外选择, 去重且拒绝禁用期间的合成事件. */
function selectGroup(keys: string[], checked: boolean): void {
    if (props.disabled) return
    const available = new Set(hierarchyMeasureKeys(hierarchy.value))
    const requested = new Set(keys.filter((key) => available.has(key)))
    if (!requested.size) return
    const next = checked ? new Set([...props.selected, ...requested]) : new Set(props.selected.filter((key) => !requested.has(key)))
    if (next.size === props.selected.length && props.selected.every((key) => next.has(key))) return
    emit("update:selected", [...next])
}
</script>
<template>
    <div class="measure-hierarchy">
        <div class="measure-selection-actions">
            <el-button :disabled="disabled || !hierarchy.length" @click="!disabled && emit('selectAll')">{{ t("model.selectAll") }}</el-button>
            <el-button :disabled="disabled || !selected.length" @click="!disabled && emit('update:selected', [])">{{ t("model.clearSelection") }}</el-button>
            <el-switch
                class="measure-expand-switch"
                :model-value="allExpanded"
                :disabled="disabled || !visibleGroups.length"
                inline-prompt
                :active-text="t('model.expandFolders')"
                :inactive-text="t('model.collapseFolders')"
                :aria-label="t('model.toggleMeasureFolders')"
                @update:model-value="expandAll($event === true)"
            />
        </div>
        <small>{{ t("model.selectedCount", { count: selectedCount }) }}{{ selectedCount > 128 ? t("model.batchLimit") : "" }}</small>
        <div role="group" :aria-label="t('model.selectMeasures')">
            <ul class="measure-tree">
                <MeasureHierarchyNode
                    v-for="node in hierarchy"
                    :key="node.key"
                    :node="node"
                    :query="query"
                    :collapsed="visibleCollapsed"
                    :selected="selected"
                    :disabled="disabled"
                    :current-key="currentKey"
                    @select="!disabled && emit('select', $event)"
                    @toggle="toggle"
                    @select-group="selectGroup"
                />
            </ul>
        </div>
        <el-empty v-if="!hierarchy.length" :description="t('model.measureEmpty')" :image-size="64" />
    </div>
</template>
<style scoped lang="scss">
.measure-selection-actions {
    display: flex;
    gap: 12px;
    flex-wrap: wrap;
    align-items: center;
    margin-bottom: 12px;
    :deep(.el-button + .el-button) {
        margin-left: 0;
    }
}
.measure-expand-switch {
    margin-left: auto;
}
.measure-tree {
    margin: 8px 0 0;
    padding: 0;
}
</style>
