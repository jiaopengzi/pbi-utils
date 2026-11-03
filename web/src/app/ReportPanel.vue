<!--
 * FilePath    : pbi-utils\web\src\app\ReportPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 展示报表页面和视觉对象定义, 复用内存编辑与显式保存流程.
-->
<script setup lang="ts">
import { computed, ref } from "vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { BridgeClient, ProjectSnapshot } from "@/bridge/client"
import UiPanel from "@/ui/UiPanel.vue"
import { useMessageNotices } from "@/composables/ui/useMessageNotices"
import ModelEditPanel from "./ModelEditPanel.vue"
const props = defineProps<{ project: ProjectSnapshot; bridge: BridgeClient; locked?: boolean }>()
const emit = defineEmits<{ busy: [value: boolean]; updated: [value: ProjectSnapshot] }>()
const { t } = useI18n()
const selected = ref("")
const current = computed(() => props.project.pages.find((p) => p.id === selected.value) ?? props.project.pages[0])
useMessageNotices("report-panel", () => [!!current.value && { key: "definition", once: true, message: t("report.definitionHint"), type: "info" }])
</script>
<template>
    <UiPanel>
        <div class="panel-heading">
            <h2>{{ t("report.pageCount", { count: project.pages.length }) }}</h2>
            <small>{{ project.reportFormat }} · {{ t("report.readOnly") }}</small>
        </div>
        <el-empty v-if="!project.pages.length" :description="t('report.noPages')" :image-size="64" />
        <div v-else class="object-layout">
            <div class="object-list" :aria-label="t('report.pages')">
                <el-button
                    v-for="item in project.pages"
                    :key="item.id"
                    :class="{ active: current === item }"
                    :aria-pressed="current === item"
                    @click="selected = item.id"
                    ><strong>{{ item.displayName }}</strong
                    ><small>{{ t("report.visualCount", { count: item.visuals.length }) }}</small></el-button
                >
            </div>
            <div v-if="current" class="object-detail">
                <h2>{{ current.displayName }}</h2>
                <p>{{ current.width }} × {{ current.height }} · {{ current.id }}</p>

                <el-table :data="current.visuals" row-key="id" stripe :aria-label="t('report.visualDefinitions')" :empty-text="t('report.noVisuals')">
                    <el-table-column prop="id" :label="t('report.visualId')" show-overflow-tooltip />
                    <el-table-column prop="type" :label="t('report.type')" />
                    <el-table-column :label="t('report.position')"
                        ><template #default="{ row }">{{ row.x }}, {{ row.y }}</template></el-table-column
                    >
                    <el-table-column :label="t('report.size')"
                        ><template #default="{ row }">{{ row.width }} × {{ row.height }}</template></el-table-column
                    >
                </el-table>
            </div>
        </div>
    </UiPanel>
    <ModelEditPanel
        v-if="!project.projectPath.startsWith('model://')"
        :project="project"
        :bridge="bridge"
        :locked="locked"
        report
        @busy="emit('busy', $event)"
        @updated="emit('updated', $event)"
    />
</template>
