<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfLocalManager.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地UDF列表管理、定义编辑和精确基线删除确认弹窗.
-->
<script setup lang="ts">
import { computed } from "vue"
import UdfFunctionRow from "./UdfFunctionRow.vue"
import { groupLocalUdfPackages, preferredLocalUdfEntry, type LocalUdfPackage } from "./udfLocalPackages"
import UiDialog from "@/ui/UiDialog.vue"
import CodeEditor from "@/features/code/CodeEditor.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
import type { LocalUdfLibrary } from "@/composables/features/scaffold/useLocalUdfLibrary"
import type { UdfSources } from "@/composables/features/scaffold/useUdfSources"
/** 定义与依赖沿用当前草稿, 取消不提交, 保存和删除只使用宿主条目身份. */
const props = defineProps<{
    state: Pick<
        UdfState,
        "manager" | "busy" | "locals" | "deleteTarget" | "editor" | "target" | "definition" | "sources" | "error" | "model" | "localPackageTags"
    >
    actions: LocalUdfLibrary
    sourceLabel: UdfSources["sourceLabel"]
}>()
const { manager, busy, locals, deleteTarget, editor, target, definition, sources } = props.state
const { edit, save, remove } = props.actions
const { t } = useI18n()
const groups = computed(() => groupLocalUdfPackages(locals.value))
/** 管理行与浏览共用当前tag, 精确编辑/删除仍使用宿主GUID和基线. */
function current(group: LocalUdfPackage) {
    const selected = props.state.model.value.find((selection) => selection.source === "local" && group.versions.some((item) => item.id === selection.packageId))
    return preferredLocalUdfEntry(group, props.state.localPackageTags.value[group.key] ?? selected?.packageId)
}
/** 版本选择复用原子依赖核验, 已选包失败时共享受控tag不变. */
function changeVersion(group: LocalUdfPackage, id: string): void {
    if (busy.value) return
    const next = group.versions.find((item) => item.id === id)
    if (next) void props.actions.changeVersion(next, current(group))
}
</script>
<template>
    <UiDialog v-model="manager" :title="t('scaffold.localManager')" :busy="busy"
        ><el-button :class="{ 'operation-lock': busy }" :disabled="busy" @click="edit()">{{ t("scaffold.newFunction") }}</el-button>
        <div class="udf-function-grid udf-local-manager-packages">
            <UdfFunctionRow
                v-for="group in groups"
                :key="group.key"
                :group="group"
                :entry="current(group)"
                :disabled="busy"
                :operation-locked="busy"
                manage
                @change-version="changeVersion(group, $event)"
                @edit="edit(current(group))"
                @delete="deleteTarget = current(group)"
            /></div
    ></UiDialog>
    <UiDialog v-model="editor" :title="target ? t('scaffold.edit') : t('scaffold.newFunction')" :busy="busy"
        ><form class="scaffold-form" @submit.prevent.stop>
            <label>{{ t("scaffold.name") }}<el-input v-model="definition.name" :class="{ 'operation-lock': busy }" :disabled="busy" /></label
            ><label>{{ t("scaffold.version") }}<el-input v-model="definition.version" :class="{ 'operation-lock': busy }" :disabled="busy" /></label
            ><label
                >{{ t("scaffold.description") }}
                <CodeEditor
                    v-model="definition.description"
                    language="markdown"
                    :label="t('scaffold.description')"
                    :disabled="busy"
                    :rows="5"
                    :maxlength="8192" /></label
            ><label
                >{{ t("scaffold.expression") }}
                <CodeEditor v-model="definition.expression" :label="t('scaffold.expression')" :disabled="busy" :maxlength="32768" />
            </label>
            <h3>{{ t("scaffold.declaredDependencies") }}</h3>
            <p>{{ t("scaffold.dependenciesHelp") }}</p>
            <div v-for="(item, index) in definition.dependencies" :key="index" class="scaffold-grid">
                <label
                    >{{ t("scaffold.source")
                    }}<el-select
                        v-model="item.source"
                        @change="item.sourceBaseline = sources.find((entry) => entry.id === item.source)?.baseline"
                        :class="{ 'operation-lock': busy }"
                        :disabled="busy"
                        ><el-option v-for="entry in sources" :key="entry.id" :value="entry.id" :label="sourceLabel(entry.id)" /></el-select></label
                ><label>{{ t("scaffold.packageId") }}<el-input v-model="item.packageId" :class="{ 'operation-lock': busy }" :disabled="busy" /></label
                ><label>{{ t("scaffold.version") }}<el-input v-model="item.version" :class="{ 'operation-lock': busy }" :disabled="busy" /></label
                ><label>{{ t("scaffold.function") }}<el-input v-model="item.function" :class="{ 'operation-lock': busy }" :disabled="busy" /></label
                ><label>{{ t("scaffold.baseline") }}<el-input v-model="item.baseline" :class="{ 'operation-lock': busy }" :disabled="busy" /></label
                ><el-button :class="{ 'operation-lock': busy }" :disabled="busy" @click="definition.dependencies.splice(index, 1)">{{
                    t("scaffold.remove")
                }}</el-button>
            </div>
            <el-button
                :class="{ 'operation-lock': busy }"
                :disabled="busy"
                @click="definition.dependencies.push({ source: 'daxlib', packageId: '', version: '', function: '', baseline: null })"
                >{{ t("scaffold.add") }}</el-button
            >
        </form>
        <template #footer
            ><el-button :class="{ 'operation-lock': busy }" :disabled="busy" @click="editor = false">{{ t("scaffold.cancel") }}</el-button
            ><el-button type="primary" :class="{ 'operation-lock': busy }" :disabled="busy" @click="save">{{ t("scaffold.save") }}</el-button></template
        ></UiDialog
    >
    <UiDialog :model-value="!!deleteTarget" :title="t('scaffold.deleteTitle')" :busy="busy" @update:model-value="deleteTarget = undefined"
        ><p>{{ t("scaffold.deleteConfirm", { name: deleteTarget?.definition.name ?? "", version: deleteTarget?.definition.version ?? "" }) }}</p>
        <template #footer
            ><el-button :class="{ 'operation-lock': busy }" :disabled="busy" @click="deleteTarget = undefined">{{ t("scaffold.cancel") }}</el-button
            ><el-button type="danger" :class="{ 'operation-lock': busy }" :disabled="busy" @click="remove">{{
                t("scaffold.confirmDelete")
            }}</el-button></template
        ></UiDialog
    >
</template>
<style scoped lang="scss">
.udf-local-manager-packages {
    margin-top: 12px;
}
</style>
