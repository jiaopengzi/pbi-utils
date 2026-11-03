<!--
 * FilePath    : pbi-utils\web\src\ui\OperationMessages.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 聚合独立操作的全局消息栈, 固定定位且不拦截业务键盘或改变正文布局.
-->
<script setup lang="ts">
import { computed, type ShallowRef } from "vue"
import OperationMessage from "./OperationMessage.vue"
import type { OperationMessageEntry } from "@/composables/ui/useFeedback"
const props = defineProps<{ entries: ShallowRef<OperationMessageEntry[]> }>()
const items = computed(() => props.entries.value)
</script>
<template>
    <div class="operation-messages">
        <OperationMessage v-for="item in items" :key="item.id" :source="item.source" />
    </div>
</template>
<style scoped lang="scss">
.operation-messages {
    position: fixed;
    top: 20px;
    left: 50%;
    transform: translateX(-50%);
    // 在途通道持续保留在应用弹窗遮罩之下, 不遮挡标题、表单和确认入口.
    z-index: 1900;
    width: max-content;
    max-width: min(640px, calc(100vw - 48px));
    max-height: calc(100vh - 40px);
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 12px;
    pointer-events: none;
}
</style>
