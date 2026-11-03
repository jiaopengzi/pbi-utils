<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfFunctionPageActions.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 远端和本地函数列表共用的当前页选择与分页操作栏.
-->
<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
/** 仅发出当前页操作意图, 父级负责冻结候选并完整验证; 禁用时拒绝迟到事件. */
const props = defineProps<{
    page: number
    pageSize: number
    total: number
    hasItems: boolean
    disabled: boolean
    operationLocked?: boolean
    packageMode?: boolean
}>()
const emit = defineEmits<{ selectPage: []; changePage: [page: number]; changeSize: [size: number] }>()
const { t } = useI18n()
/** 仅当前页非空且可操作时发起选择, 不推断全来源候选. */
function selectPage(): void {
    if (!props.disabled && props.hasItems) emit("selectPage")
}
/** 保留原分页范围与尺寸白名单, 父级仍执行自己的请求和状态保护. */
function changePage(value: number): void {
    if (!props.disabled && Number.isInteger(value) && value > 0 && value <= Math.max(1, Math.ceil(props.total / props.pageSize))) emit("changePage", value)
}
/** 只透传受支持的每页数量, 由父级重置当前页并重新读取. */
function changeSize(value: number): void {
    if (!props.disabled && [50, 100, 200, 500].includes(value)) emit("changeSize", value)
}
</script>
<template>
    <div class="udf-page-actions">
        <el-button :disabled="disabled || !hasItems" :class="{ 'operation-lock': operationLocked }" @click="selectPage">{{
            t(packageMode ? "scaffold.selectPackagePage" : "scaffold.selectPage")
        }}</el-button>
        <el-pagination
            :current-page="page"
            :page-size="pageSize"
            :page-sizes="[50, 100, 200, 500]"
            :total="total"
            layout="sizes, prev, pager, next, total"
            :disabled="disabled"
            :aria-label="t(packageMode ? 'scaffold.packagePages' : 'scaffold.functionPages')"
            @update:current-page="changePage"
            @update:page-size="changeSize"
        />
    </div>
</template>
