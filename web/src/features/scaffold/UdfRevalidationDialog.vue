<!--
 * FilePath    : pbi-utils\web\src\features\scaffold\UdfRevalidationDialog.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 更新后保留原选择并展示新旧版本、缺失函数、依赖和许可供明确确认.
-->
<script setup lang="ts">
import UdfFunctionList from "./UdfFunctionList.vue"
import UiDialog from "@/ui/UiDialog.vue"
import { useI18n } from "@/composables/i18n/useI18n"
import type { UdfCache } from "@/composables/features/scaffold/useUdfCache"
/** 只审阅重新核验结果; 确认前不改变外部模型草稿. */
const props = defineProps<{ actions: UdfCache }>()
const { reviewing, entries, canConfirm, confirm } = props.actions
const { t } = useI18n()
</script>
<template>
    <UiDialog v-model="reviewing" :title="t('scaffold.revalidate')">
        <p>{{ t("scaffold.revalidateHint") }}</p>
        <section v-for="(entry, index) in entries" :key="`${entry.next.source}/${entry.next.packageId}`" class="udf-revalidation-entry">
            <h3>{{ entry.next.packageId }}: {{ entry.previousVersion ?? t("scaffold.pendingVerification") }} → {{ entry.package.version }}</h3>
            <p>{{ t("scaffold.contentBaseline") }}: {{ entry.previous.baseline ?? entry.previousPackage?.hash ?? "-" }} → {{ entry.package.hash }}</p>
            <h4>{{ t("scaffold.functions") }}</h4>
            <UdfFunctionList
                :functions="entry.next.functions"
                :start="1 + entries.slice(0, index).reduce((total, item) => total + item.next.functions.length, 0)"
            />
            <section v-if="entry.missing.length" role="alert">
                <h4>{{ t("scaffold.missingFunctions") }}</h4>
                <UdfFunctionList :functions="entry.missing" />
            </section>
            <p>{{ t("scaffold.declaredDependencies") }}: {{ entry.package.dependencies.map((item) => `${item.id}@${item.version}`).join(", ") || "-" }}</p>
            <details v-if="entry.previousPackage">
                <summary>{{ t("scaffold.previousFacts") }}</summary>
                <p>
                    {{ t("scaffold.declaredDependencies") }}:
                    {{ entry.previousPackage.dependencies.map((item) => `${item.id}@${item.version}`).join(", ") || "-" }}
                </p>
                <section v-for="(text, name) in entry.previousPackage.licenseFiles" :key="name">
                    <h4>{{ name }}</h4>
                    <pre>{{ text }}</pre>
                </section>
            </details>
            <p v-else class="muted">{{ t("scaffold.previousFactsUnknown") }}</p>
            <details open>
                <summary>{{ t("scaffold.licenses") }}</summary>
                <section v-for="(text, name) in entry.package.licenseFiles" :key="name">
                    <h4>{{ name }}</h4>
                    <pre>{{ text }}</pre>
                </section>
            </details>
            <section v-if="entry.dependencies.length" class="udf-dependency-preview">
                <h4>{{ t("scaffold.dependencyFacts") }}</h4>
                <p class="muted">{{ t("scaffold.previousClosureUnknown") }}</p>
                <section v-for="dependency in entry.dependencies" :key="`${dependency.id}@${dependency.version}`">
                    <h4>{{ dependency.id }} @ {{ dependency.version }}</h4>
                    <p>{{ t("scaffold.contentBaseline") }}: {{ dependency.hash }}</p>
                    <h4>{{ t("scaffold.functions") }}</h4>
                    <UdfFunctionList :functions="dependency.functions.map((item) => item.name)" />
                    <p>{{ t("scaffold.declaredDependencies") }}: {{ dependency.dependencies.map((item) => `${item.id}@${item.version}`).join(", ") || "-" }}</p>
                    <details open>
                        <summary>{{ t("scaffold.licenses") }}</summary>
                        <section v-for="(text, name) in dependency.licenseFiles" :key="name">
                            <h4>{{ name }}</h4>
                            <pre>{{ text }}</pre>
                        </section>
                    </details>
                </section>
            </section>
        </section>
        <template #footer
            ><el-button @click="reviewing = false">{{ t("scaffold.cancel") }}</el-button
            ><el-button type="primary" :disabled="!canConfirm" @click="confirm">{{ t("scaffold.confirmRevalidate") }}</el-button></template
        >
    </UiDialog>
</template>
