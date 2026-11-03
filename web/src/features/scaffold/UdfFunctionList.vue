<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfFunctionList.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享已选与预览函数有序列表, 完整显示长名称并保留跨包连续编号.
-->
<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
/** 按函数原顺序展示名称; start 设定跨包起始编号, 只读展示不修改选择或发起请求. */
defineProps<{ functions: string[]; start?: number }>()
const { t } = useI18n()
</script>
<template>
    <ol class="udf-function-list" :start="start ?? 1" :aria-label="t('scaffold.packageFunctions')">
        <li v-for="name in functions" :key="name">
            <code>{{ name }}</code>
        </li>
    </ol>
</template>
<style scoped lang="scss">
.udf-function-list {
    margin: 0;
    padding: 0 0 0 44px;
    min-width: 0;
    li {
        padding: 9px 12px 9px 4px;
        line-height: 1.6;
        border-top: 1px solid var(--el-border-color-lighter);
        overflow-wrap: anywhere;
        &::marker {
            color: var(--el-text-color-secondary);
            font-variant-numeric: tabular-nums;
            font-size: 12px;
        }
    }
    code {
        font-size: 13px;
        color: var(--el-text-color-primary);
        white-space: normal;
    }
}
</style>
