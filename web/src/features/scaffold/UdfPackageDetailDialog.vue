<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfPackageDetailDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 以横向元数据和全宽文档展示精确包及全部函数, 不重复文档内声明.
-->
<script setup lang="ts">
import { ref, watch } from "vue"
import UiDialog from "@/ui/UiDialog.vue"
import CodeBlock from "@/features/code/CodeBlock.vue"
import UdfMarkdown from "./UdfMarkdown.vue"
import UiIcon from "@/ui/UiIcon.vue"
import { formatUdfLocalTime } from "./udfDateTime"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfPackage } from "./contracts"
import "./udf-detail.scss"
/** 展示宿主核验的完整包, README 缺失时使用说明; 只读和复制不改变选择. */
const props = defineProps<{ details?: UdfPackage; busy?: boolean }>()
const iconFailed = ref(false)
watch(
    () => props.details,
    () => {
        iconFailed.value = false
    },
)
const model = defineModel<boolean>({ required: true })
const { t } = useI18n()
</script>
<template>
    <UiDialog
        v-model="model"
        :title="details?.id ?? t('scaffold.packageDetails')"
        :busy="busy"
        :scroll-reset-key="details ? JSON.stringify([details.id, details.version, details.hash]) : undefined"
        width="min(1200px, calc(100vw - 32px))"
        class="udf-detail-dialog"
    >
        <template v-if="model && details">
            <header class="udf-detail-hero">
                <div class="udf-detail-intro">
                    <img
                        v-if="details.metadata?.iconDataUrl && !iconFailed"
                        :src="details.metadata.iconDataUrl"
                        class="udf-detail-icon"
                        alt=""
                        @error="iconFailed = true"
                    />
                    <UiIcon v-else name="square-function" class="udf-detail-icon" />
                    <div>
                        <UdfMarkdown :source="details.description || '-'" />
                    </div>
                </div>
                <dl class="udf-detail-metadata">
                    <div class="udf-detail-meta-item">
                        <dt>{{ t("scaffold.packageVersion") }}</dt>
                        <dd>
                            <el-tag effect="plain">{{ details.version }}</el-tag
                            ><span class="muted">{{ t("scaffold.packageFunctionCount", { count: details.functions.length }) }}</span>
                        </dd>
                    </div>
                    <div class="udf-detail-meta-item">
                        <dt>{{ t("scaffold.committedAt") }}</dt>
                        <dd>{{ formatUdfLocalTime(details.committedAt) }}</dd>
                    </div>
                    <div class="udf-detail-meta-item">
                        <dt>{{ t("scaffold.authors") }}</dt>
                        <dd>{{ details.metadata?.authors || "-" }}</dd>
                    </div>
                    <div class="udf-detail-meta-item">
                        <dt>{{ t("scaffold.categoryTags") }}</dt>
                        <dd class="udf-detail-tags">
                            <template v-if="details.metadata?.tags.length"
                                ><el-tag v-for="tag in details.metadata.tags" :key="tag" effect="plain">{{ tag }}</el-tag></template
                            ><template v-else>-</template>
                        </dd>
                    </div>
                </dl>
            </header>
            <div class="udf-detail-library-copy">
                <section class="udf-detail-document" :aria-label="t('scaffold.packageDocumentation')">
                    <UdfMarkdown :source="details.readme?.trim() ? details.readme : details.description || '-'" />
                </section>
                <section class="udf-detail-section udf-detail-code" :aria-label="t('scaffold.packageFunctions')">
                    <h3>
                        {{ t("scaffold.packageFunctions") }} <span class="muted">{{ details.functions.length }}</span>
                    </h3>
                    <details v-for="fn in details.functions" :key="fn.name" class="udf-detail-function" open>
                        <summary>{{ fn.name }}</summary>
                        <UdfMarkdown v-if="fn.description" :source="fn.description" /><CodeBlock :code="fn.expression" language="dax" />
                    </details>
                </section>
            </div>
        </template>
    </UiDialog>
</template>
