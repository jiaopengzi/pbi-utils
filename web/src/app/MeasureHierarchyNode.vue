<!--
 * FilePath    : pbi-utils\web\src\app\MeasureHierarchyNode.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 递归展示度量值目录与精确对象行, 通过明确事件回传选择和折叠意图.
-->
<script setup lang="ts">
import { computed } from "vue"
import UiSearchHighlight from "@/ui/UiSearchHighlight.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import { hierarchyMeasureKeys, measureKey, type HierarchyMeasure, type MeasureHierarchyNode } from "@/features/model/measureHierarchy"

/** node 为已过滤的展示树, selected 为受控对象身份; 目录选择包含折叠后代, 只回传意图, 不执行模型操作. */
const props = defineProps<{
    node: MeasureHierarchyNode
    collapsed: ReadonlySet<string>
    selected: string[]
    disabled: boolean
    currentKey?: string
    query?: string
}>()
const emit = defineEmits<{ select: [measure: HierarchyMeasure]; toggle: [key: string]; selectGroup: [keys: string[], selected: boolean] }>()
const { t } = useI18n()
const nodeKeys = computed(() => hierarchyMeasureKeys([props.node]))
const selection = computed(() => {
    // 叶子沿用单键判断, 避免每个展示位置都为全部已选对象重新分配集合.
    if (props.node.measure) return { checked: props.selected.includes(measureKey(props.node.measure)), indeterminate: false }
    const selected = new Set(props.selected)
    const count = nodeKeys.value.filter((key) => selected.has(key)).length
    return { checked: nodeKeys.value.length > 0 && count === nodeKeys.value.length, indeterminate: count > 0 && count < nodeKeys.value.length }
})

/** 仅转发可交互状态下的布尔选择, 目录和叶子共享准确身份, 拒绝控件的非布尔合成值. */
function selectGroup(keys: string[], selected: string | number | boolean): void {
    if (!props.disabled && typeof selected === "boolean") emit("selectGroup", keys, selected)
}
</script>
<template>
    <li class="measure-node">
        <div v-if="node.measure" class="measure-row" :class="{ active: selected.includes(measureKey(node.measure)) }">
            <el-checkbox
                :model-value="selection.checked"
                :disabled="disabled"
                :aria-label="t('model.selectMeasure', { table: node.measure.table, name: node.measure.name })"
                @change="selectGroup(nodeKeys, $event)"
                ><span class="sr-only">{{ node.measure.name }}</span></el-checkbox
            >
            <el-button
                text
                :disabled="disabled"
                :aria-pressed="currentKey === measureKey(node.measure)"
                :title="`${node.path}\\${node.label}`"
                @click="!disabled && emit('select', node.measure)"
                ><strong class="measure-content-label"><UiSearchHighlight :text="node.label" :query="query ?? ''" /></strong
            ></el-button>
        </div>
        <template v-else>
            <div class="measure-folder-row">
                <el-checkbox
                    class="measure-folder-selection"
                    :model-value="selection.checked"
                    :indeterminate="selection.indeterminate"
                    :disabled="disabled"
                    :aria-label="t('model.selectMeasureFolder', { path: node.path })"
                    @change="selectGroup(nodeKeys, $event)"
                    ><span class="sr-only">{{ node.label }}</span></el-checkbox
                >
                <el-button
                    class="measure-folder"
                    text
                    :disabled="disabled"
                    :aria-expanded="!collapsed.has(node.key)"
                    :title="node.path"
                    @click="!disabled && emit('toggle', node.key)"
                    ><svg class="folder-chevron" :class="{ expanded: !collapsed.has(node.key) }" viewBox="0 0 16 16" aria-hidden="true">
                        <path d="m6 3 5 5-5 5" /></svg
                    ><span class="measure-content-label"><UiSearchHighlight :text="node.label" :query="query ?? ''" /></span
                ></el-button>
            </div>
            <ul v-if="!collapsed.has(node.key)" class="measure-children">
                <MeasureHierarchyNode
                    v-for="child in node.children"
                    :key="child.key"
                    :node="child"
                    :query="query"
                    :collapsed="collapsed"
                    :selected="selected"
                    :disabled="disabled"
                    :current-key="currentKey"
                    @select="!disabled && emit('select', $event)"
                    @toggle="!disabled && emit('toggle', $event)"
                    @select-group="selectGroup"
                />
            </ul>
        </template>
    </li>
</template>
<style scoped lang="scss">
.measure-node {
    list-style: none;
    min-width: 0;
}
.measure-children {
    margin: 0;
    padding-left: 18px;
}
.measure-folder {
    padding: 8px;
    justify-content: flex-start;
    :deep(> span) {
        display: flex;
        align-items: center;
        gap: 6px;
        min-width: 0;
        max-width: 100%;
    }
}
.folder-chevron {
    width: 14px;
    height: 14px;
    flex-shrink: 0;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.5;
    &.expanded {
        transform: rotate(90deg);
    }
}
.measure-content-label {
    display: block;
    width: max-content;
    min-width: 0;
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}
.measure-row,
.measure-folder-row {
    display: flex;
    align-items: center;
    padding: 4px 8px;
    border-radius: 8px;
    margin-top: 4px;
    &.active {
        background: var(--el-color-primary-light-9);
    }
    :deep(.el-checkbox) {
        margin-right: 0;
        flex-shrink: 0;
    }
    :deep(.el-button) {
        flex: 1;
        min-width: 0;
        height: auto;
        justify-content: flex-start;
    }
    :deep(.el-button > span) {
        min-width: 0;
        max-width: 100%;
        line-height: var(--el-font-line-height-primary);
    }
}
.sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
}
</style>
