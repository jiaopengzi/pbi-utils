<!--
 * FilePath    : pbi-utils\web\src\features\configuration\ConfigurationPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 当前模型配置呈现器, 按宿主目标隔离草稿并在保存后回读, 不使用创建接口.
-->
<script setup lang="ts">
import { useTemplateRef } from "vue"
import type { ProjectSnapshot } from "@/bridge/client"
import UiPanel from "@/ui/UiPanel.vue"
import ConfigurationRecovery from "./ConfigurationRecovery.vue"
import ConfigurationFields from "./ConfigurationFields.vue"
import "../scaffold/scaffold.scss"
import { useConfiguration, type ConfigurationProps } from "@/composables/features/configuration/useConfiguration"
const root = useTemplateRef<HTMLElement>("root")
const props = defineProps<ConfigurationProps>()
const emit = defineEmits<{ busy: [value: boolean]; updated: [value: ProjectSnapshot]; capability: [value: boolean] }>()
const {
    context,
    draft,
    definitions,
    visited,
    prefix,
    formLocked,
    locked,
    canWrite,
    previewContext,
    canApply,
    load,
    apply,
    reloadMeasures,
    selectMeasureMode,
    finish,
    t,
    childBusy,
    udfValid,
    dialog,
} = useConfiguration(props, emit, root)
</script>
<template>
    <section ref="root" class="scaffold-panel configuration-panel" :aria-busy="locked">
        <UiPanel
            ><el-button v-if="!context" :disabled="locked" @click="load">{{ t("configuration.reloadModel") }}</el-button>
            <ConfigurationRecovery
                :context="context"
                :locked="locked"
                :active="active"
                @complete="finish()"
                @discard="finish(true)"
                @dialog="dialog = $event"
            />
            <form class="scaffold-form" @submit.prevent.stop>
                <ConfigurationFields
                    :draft="draft"
                    :bridge="bridge"
                    :step="section"
                    :active="active"
                    :visited="visited"
                    :id-prefix="prefix"
                    :labelled-by="
                        section === 'measures' ? 'tab-templates' : section === 'calendar' ? 'tab-time' : section === 'udfs' ? 'tab-udf' : 'tab-sample'
                    "
                    :disabled="formLocked || !canWrite"
                    :locked="locked || !canWrite"
                    :operation-locked="locked && canWrite"
                    :definitions="definitions"
                    :details="context?.details"
                    :preview-context="previewContext"
                    :can-generate-sample="context?.canGenerateSample === true"
                    :can-use-measure-config="context?.canUseMeasureConfig === true"
                    :measure-config-unavailable-reason="context?.measureConfigUnavailableReason"
                    model-configuration
                    @busy="childBusy = $event"
                    @udf-validity="udfValid = $event"
                    @dialog="dialog = $event"
                    @measure-mode="selectMeasureMode"
                >
                    <template #actions="{ operation, canSubmit }">
                        <el-button
                            type="primary"
                            :disabled="!canApply(operation) || canSubmit === false"
                            :class="{ 'operation-lock': locked && canWrite }"
                            @click="apply(operation)"
                            >{{ t(`configuration.apply${operation[0]!.toUpperCase()}${operation.slice(1)}`) }}</el-button
                        >
                        <el-button
                            v-if="operation === 'measures'"
                            :disabled="locked || !context"
                            :class="{ 'operation-lock': locked && !!context }"
                            :title="t('configuration.reloadMeasuresHint')"
                            :aria-label="`${t('configuration.reloadMeasures')}. ${t('configuration.reloadMeasuresHint')}`"
                            @click="reloadMeasures"
                            >{{ t("configuration.reloadMeasures") }}</el-button
                        >
                    </template>
                </ConfigurationFields>
            </form></UiPanel
        >
    </section>
</template>
