<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfSourceManager.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : DAXLib 更新偏好、自定义 UDF 来源登记与精确基线删除确认弹窗.
-->
<script setup lang="ts">
import UiDialog from "@/ui/UiDialog.vue"
import UiIcon from "@/ui/UiIcon.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfState } from "@/composables/features/scaffold/useUdfState"
import type { UdfSources } from "@/composables/features/scaffold/useUdfSources"
/** 模态展示登记来源和 DAXLib 更新偏好, 持久化交给共享请求编排. */
const props = defineProps<{
    state: Pick<UdfState, "sourceManager" | "busy" | "sources" | "gitHubOnly" | "sourceDeleteTarget" | "sourceName" | "sourceUrl" | "sourceGitRef">
    actions: UdfSources
}>()
const { sourceManager, busy, sources, gitHubOnly, sourceDeleteTarget, sourceName, sourceUrl, sourceGitRef } = props.state
const { sourceLabel, saveSource, removeSource, saveDaxLibPolicy } = props.actions
const { t } = useI18n()
const daxLibMirrorUrl = "https://gitee.com/jiaopengzi/daxlib-mirror"
</script>
<template>
    <UiDialog v-model="sourceManager" :title="t('scaffold.sourceManager')" :busy="busy">
        <p class="udf-protocol">{{ t("scaffold.sourceProtocol") }}</p>
        <div v-for="item in sources" :key="item.id" class="scaffold-selection udf-source-row">
            <span
                >{{ sourceLabel(item.id) }}<br /><span v-if="item.baseUrl" class="muted"
                    >{{ item.id === "daxlib" && gitHubOnly === false ? daxLibMirrorUrl : item.baseUrl
                    }}<template v-if="item.gitRef"> @ {{ item.gitRef }}</template></span
                ></span
            >
            <el-button v-if="item.kind === 'custom'" :class="{ 'operation-lock': busy }" :disabled="busy" @click="sourceDeleteTarget = item"
                ><UiIcon name="trash-2" monochrome />{{ t("scaffold.delete") }}</el-button
            >
            <el-switch
                v-if="item.id === 'daxlib'"
                class="udf-source-policy"
                data-test="udf-daxlib-github-only"
                :model-value="gitHubOnly !== false"
                :aria-label="t('scaffold.daxLibGitHubOnly')"
                :class="{ 'operation-lock': busy }"
                :disabled="busy || gitHubOnly === undefined"
                :width="66"
                inline-prompt
                active-text="GitHub"
                inactive-text="Gitee"
                @change="saveDaxLibPolicy($event === true)"
            />
        </div>
        <form class="scaffold-form udf-source-form" @submit.prevent.stop>
            <label
                >{{ t("scaffold.sourceName")
                }}<el-input
                    v-model="sourceName"
                    data-test="udf-source-name"
                    :aria-label="t('scaffold.sourceName')"
                    :class="{ 'operation-lock': busy }"
                    :disabled="busy"
                    :maxlength="80"
            /></label>
            <label
                >{{ t("scaffold.sourceUrl")
                }}<el-input
                    v-model="sourceUrl"
                    data-test="udf-source-url"
                    :aria-label="t('scaffold.sourceUrl')"
                    :class="{ 'operation-lock': busy }"
                    :disabled="busy"
                    :maxlength="2048"
                    :placeholder="t('scaffold.sourceUrlPlaceholder')"
            /></label>
            <label
                >{{ t("scaffold.sourceGitRef")
                }}<el-input
                    v-model="sourceGitRef"
                    :class="{ 'operation-lock': busy }"
                    :disabled="busy"
                    :maxlength="256"
                    :aria-label="t('scaffold.sourceGitRef')"
            /></label>
        </form>
        <template #footer
            ><el-button :class="{ 'operation-lock': busy }" :disabled="busy" @click="sourceManager = false">{{ t("scaffold.done") }}</el-button
            ><el-button type="primary" :class="{ 'operation-lock': busy }" :disabled="busy" @click="saveSource"
                ><UiIcon name="plus" monochrome />{{ t("scaffold.addSource") }}</el-button
            ></template
        >
    </UiDialog>
    <UiDialog :model-value="!!sourceDeleteTarget" :title="t('scaffold.sourceDeleteTitle')" :busy="busy" @update:model-value="sourceDeleteTarget = undefined">
        <p>{{ t("scaffold.sourceDeleteConfirm", { name: sourceDeleteTarget?.name ?? "" }) }}</p>
        <template #footer
            ><el-button :class="{ 'operation-lock': busy }" :disabled="busy" @click="sourceDeleteTarget = undefined">{{ t("scaffold.cancel") }}</el-button
            ><el-button type="danger" :class="{ 'operation-lock': busy }" :disabled="busy" @click="removeSource">{{
                t("scaffold.confirmDelete")
            }}</el-button></template
        >
    </UiDialog>
</template>
