<!--
 * FilePath    : pbi-utils\web\src\app\EnvironmentPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 设置中的运行环境分区, 通过诊断面板展示宿主环境, 不包含产品介绍.
-->
<script setup lang="ts">
import type { BridgeClient } from "@/bridge/client"
import DiagnosticPanel from "./DiagnosticPanel.vue"

/** bridge 提供只读宿主诊断; 加载、错误反馈及卸载取消由 DiagnosticPanel 管理. */
defineProps<{ bridge: BridgeClient }>()
</script>
<template>
    <div class="environment-settings-panel">
        <DiagnosticPanel :bridge="bridge" />
    </div>
</template>
<style scoped lang="scss">
.environment-settings-panel {
    display: grid;
    min-width: 0;
}
</style>
