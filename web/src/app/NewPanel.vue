<!--
 * FilePath    : pbi-utils\web\src\app\NewPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 独立新建工作区, 组装五步脚手架并传递当前模型的连接保护.
-->
<script setup lang="ts">
import { useI18n } from "@/composables/i18n/useI18n"
const { t } = useI18n()
import type { BridgeClient, ProjectSnapshot } from "@/bridge/client"
import ScaffoldPanel from "@/features/scaffold/ScaffoldPanel.vue"
/** 展示独立新建流程; canConnect 保护全局待保存模型, connected 仅上报宿主已成功连接的快照, 失败由脚手架展示. */
withDefaults(defineProps<{ bridge: BridgeClient; canConnect?: boolean }>(), { canConnect: true })
const emit = defineEmits<{ busy: [value: boolean]; connected: [value: ProjectSnapshot] }>()
</script>
<template>
    <section class="new-panel" :aria-label="t('app.newWorkflow')">
        <ScaffoldPanel :bridge="bridge" :can-connect="canConnect" @busy="emit('busy', $event)" @connected="emit('connected', $event)" />
    </section>
</template>
