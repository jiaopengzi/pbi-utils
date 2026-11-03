<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\MeasuresMode.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 两个入口共用模板度量来源选择, 仅编辑模式并显示宿主 Config 能力和刷新边界.
-->
<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
import ScaffoldField from "./ScaffoldField.vue"
import type { TemplateMeasureMode } from "./contracts"
/** 接收当前来源、交互锁与宿主能力; 返回有效模式选择, 不修改参数或发起模型刷新. */
const model = defineModel<TemplateMeasureMode>({ required: true })
const props = defineProps<{ disabled: boolean; configAvailable: boolean; operationLocked?: boolean; unavailableReason?: "layout" | "invalid" | null }>()
const { t } = useI18n()
/** 只接受可用模式, 对禁用或未知事件不更新草稿, 无返回值或外部副作用. */
function select(value: unknown): void {
    if (!props.disabled && (value === "tmdl" || (value === "config" && props.configAvailable))) model.value = value
}
</script>
<template>
    <ScaffoldField field="measuresMode" tag="div" class="measures-mode">
        <div class="measures-mode__selection">
            <span>{{ t("scaffold.measuresMode") }}</span>
            <el-radio-group
                class="measures-mode__options"
                :model-value="model"
                @update:model-value="select"
                :disabled="disabled"
                :class="{ 'operation-lock': operationLocked }"
                :aria-label="t('scaffold.measuresMode')"
            >
                <el-radio value="tmdl">{{ t("scaffold.measuresModeTmdl") }}</el-radio>
                <el-radio value="config" :disabled="disabled || !configAvailable">{{ t("scaffold.measuresModeConfig") }}</el-radio>
            </el-radio-group>
        </div>
        <p class="muted measures-mode__hint">{{ t(model === "config" ? "scaffold.measuresConfigHint" : "scaffold.measuresTmdlHint") }}</p>
        <p v-if="!configAvailable && unavailableReason" class="muted measures-mode__hint">
            {{ t(unavailableReason === "invalid" ? "scaffold.measuresConfigInvalid" : "scaffold.measuresConfigUnavailable") }}
        </p>
    </ScaffoldField>
</template>
<style scoped lang="scss">
.measures-mode {
    display: grid;
    gap: 8px;
    min-width: 0;
    margin-block: 12px;
}
.measures-mode__selection {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px 16px;
}
.measures-mode__options {
    gap: 8px 24px;
    min-width: 0;
    :deep(.el-radio) {
        margin: 0;
    }
}
.measures-mode__hint {
    margin: 0;
    overflow-wrap: anywhere;
}
</style>
