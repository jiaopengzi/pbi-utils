<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\TemplateQueries.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 新建模板的辅助表选择、独立分组草稿及只读 M 预览, 编辑分组不改变选择与原文.
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, useId, watch } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import { useFeedback } from "@/composables/ui/useFeedback"
import CodeEditor from "../code/CodeEditor.vue"
import ScaffoldField from "./ScaffoldField.vue"
import type { TemplateQueryDefinition } from "./contracts"
import { templateQueryGroupsIssue } from "./validation"
import { scaffoldValidationKey } from "./validationContext"

/** 目录只读, model 保存新建专用选择, groups 独立保存分组覆盖; disabled 阻止选择、分组编辑、展开和复制. */
const props = defineProps<{ queries: TemplateQueryDefinition[]; disabled: boolean }>()
const selected = defineModel<string[]>({ required: true })
const groups = defineModel<Record<string, string>>("groups", { required: true })
const { t } = useI18n()
const { showFeedback } = useFeedback()
const previews = ref(new Set<string>())
const prefix = useId()
let disposed = false
// 与页面基础校验共用首个已选分组问题, ScaffoldField 立即关联真实输入, 未选错误只保留草稿.
const issue = computed(() =>
    templateQueryGroupsIssue({
        templateQueries: selected.value,
        templateQueryGroups: groups.value,
        templateQueryDefinitions: props.queries,
    }),
)
provide(scaffoldValidationKey, issue)
const expanded = ref(false)
// 折叠仅影响展示, 校验问题出现时先展开, 让原字段关联与父级聚焦保持可达.
watch(
    issue,
    (value) => {
        if (value) expanded.value = true
    },
    { immediate: true, flush: "sync" },
)
/** 操作锁期间保持原状, 有校验问题时保持内容展开以支持工具栏定位; 无问题时原生键盘和鼠标切换均可用. */
function toggleDetails(event: Event): void {
    if (props.disabled) return
    const details = event.target as HTMLDetailsElement
    if (issue.value) details.open = true
    expanded.value = details.open
}

/** 按目录顺序生成显式选择快照, 忽略在途或未知 ID, 不接收代码文本. */
function select(id: string, enabled: boolean): void {
    if (props.disabled || !props.queries.some((query) => query.id === id)) return
    const ids = new Set(selected.value)
    if (enabled) ids.add(id)
    else ids.delete(id)
    selected.value = props.queries.filter((query) => ids.has(query.id)).map((query) => query.id)
}

/** 保存当前查询的原始分组覆盖, 包括显式空串; 锁定或未知ID不发更新, 不改变选择及目录原文. */
function setGroup(id: string, value: string): void {
    if (props.disabled || !props.queries.some((query) => query.id === id)) return
    groups.value = { ...groups.value, [id]: value }
}

/** 独立切换已知查询的只读预览, 默认全部折叠, 操作锁期间保留各项展开状态. */
function togglePreview(id: string): void {
    if (props.disabled || !props.queries.some((query) => query.id === id)) return
    if (previews.value.has(id)) previews.value.delete(id)
    else previews.value.add(id)
}

/** 精确复制当前目录原文, 失败保留可手动选择的编辑器, 卸载后的迟到结果不弹出消息. */
async function copy(query: TemplateQueryDefinition): Promise<void> {
    if (props.disabled || disposed || !props.queries.includes(query)) return
    try {
        await navigator.clipboard.writeText(query.expression)
        if (!disposed) showFeedback({ message: t("model.copied") })
    } catch {
        if (!disposed) showFeedback({ message: t("model.copyFailed"), type: "error" })
    }
}

onBeforeUnmount(() => {
    disposed = true
})
</script>

<template>
    <details class="template-queries" :open="expanded" @toggle="toggleDetails">
        <summary :id="`${prefix}-title`" @click="(disabled || !!issue) && $event.preventDefault()">
            {{ t("scaffold.queryTitle") }}
            <span class="muted">{{ t("scaffold.helperSelection", { count: selected.length }) }}</span>
            <span v-if="issue" class="template-query-issue">{{ t("scaffold.helperFixErrors") }}</span>
        </summary>
        <p class="muted">{{ t("scaffold.queryHint") }}</p>
        <article v-for="(query, index) in queries" :key="query.id" class="template-query">
            <div class="template-query-toolbar">
                <el-checkbox
                    :data-template-query="query.id"
                    :model-value="selected.includes(query.id)"
                    :disabled="disabled"
                    :class="{ 'operation-lock': disabled }"
                    @update:model-value="select(query.id, $event === true)"
                    >{{ query.id }}</el-checkbox
                >
                <ScaffoldField class="template-query-group" :data-template-group="query.id" field="templateQueryGroups" :row="index + 1" tag="div">
                    <label :id="`${prefix}-${query.id}-group-label`" :for="`${prefix}-${query.id}-group`">{{ t("scaffold.queryGroup") }}</label>
                    <el-input
                        :id="`${prefix}-${query.id}-group`"
                        :aria-labelledby="`${prefix}-${query.id}-group-label`"
                        :model-value="groups[query.id] ?? query.queryGroup"
                        :disabled="disabled"
                        :class="{ 'operation-lock': disabled }"
                        maxlength="200"
                        @update:model-value="setGroup(query.id, $event)"
                    />
                </ScaffoldField>
                <span class="template-query-spacer" />
                <el-button
                    :disabled="disabled"
                    :class="{ 'operation-lock': disabled }"
                    :aria-label="t('scaffold.queryPreview', { name: query.id })"
                    :aria-expanded="previews.has(query.id)"
                    :aria-controls="`${prefix}-${query.id}`"
                    @click="togglePreview(query.id)"
                    >{{ t("scaffold.queryCode") }}</el-button
                >
                <el-button
                    :data-template-copy="query.id"
                    :disabled="disabled"
                    :class="{ 'operation-lock': disabled }"
                    :aria-label="t('scaffold.queryCopy', { name: query.id })"
                    @click="copy(query)"
                    >{{ t("model.copy") }}</el-button
                >
            </div>
            <p class="muted">{{ t(`scaffold.query${query.id}`) }}</p>
            <div :id="`${prefix}-${query.id}`" v-show="previews.has(query.id)">
                <CodeEditor
                    v-if="previews.has(query.id)"
                    :model-value="query.expression"
                    language="m"
                    :label="t('scaffold.queryPreview', { name: query.id })"
                    readonly
                    :disabled="disabled"
                    :rows="10"
                />
            </div>
        </article>
    </details>
</template>

<style scoped lang="scss">
.template-queries {
    min-width: 0;
    margin-top: 24px;
    summary {
        cursor: pointer;
        font-weight: 600;
        padding-block: 10px;
        span {
            margin-left: 12px;
            font-weight: 400;
        }
        &:focus-visible {
            outline: 2px solid var(--el-color-primary);
            outline-offset: 4px;
        }
    }
}
.template-query-issue {
    color: var(--el-color-danger);
}
.template-query {
    min-width: 0;
    padding-block: 12px;
    border-top: 1px solid var(--el-border-color-lighter);
    p {
        margin-block: 4px 12px;
    }
}
.template-query-toolbar {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 12px;
    min-width: 0;
    .el-checkbox {
        margin-right: 0;
    }
    .el-button + .el-button {
        margin-left: 0;
    }
}
.template-query-spacer {
    flex: 1;
}
.template-query-group {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
    gap: 8px;
    width: min(280px, 100%);
    .el-input {
        min-width: 0;
    }
    :deep(.scaffold-field__error) {
        grid-column: 1 / -1;
    }
}
</style>
