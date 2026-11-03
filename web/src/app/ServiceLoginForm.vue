<!--
 * FilePath    : pbi-utils\web\src\app\ServiceLoginForm.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : Service 账号与云环境登录表单, 显式传递校验实例和用户动作, 不持有连接请求.
-->
<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue"
import type { FormInstance, FormRules } from "element-plus"
import type { Methods, PowerBiServiceStatus } from "@/bridge/client"
import type { ServiceLoginDraft } from "@/composables/app/useServiceAccount"
import { useI18n } from "@/composables/i18n/useI18n"

/** 只呈现非秘密登录草稿及环境候选; busy/disabled 由连接 owner 决定, form 事件保持父级原有校验与请求 guard. */
defineProps<{
    status?: PowerBiServiceStatus
    loginRules: FormRules
    environments: Methods["powerbi.serviceSignIn"]["result"]["environments"]
    blocked: boolean
    preserveAppearance: boolean
    loading: boolean
    pendingMethod?: keyof Methods
}>()
const login = defineModel<ServiceLoginDraft>("login", { required: true })
const recentLoginId = defineModel<string>("recentLoginId", { required: true })
const emit = defineEmits<{ form: [value: FormInstance | undefined]; signIn: []; selectRecent: []; deleteRecent: [] }>()
const { t, hostText } = useI18n()
const form = ref<FormInstance>()
watch(form, (value) => emit("form", value), { flush: "sync" })
onBeforeUnmount(() => emit("form", undefined))
</script>
<template>
    <el-form ref="form" :model="login" :rules="loginRules" label-position="top" @submit.prevent="emit('signIn')">
        <el-form-item v-if="status?.recentLogins.length" :label="t('connection.recentLogin')">
            <div class="connection-recent">
                <el-select
                    v-model="recentLoginId"
                    :class="{ 'operation-lock': preserveAppearance }"
                    :disabled="blocked"
                    :placeholder="t('connection.selectRecent')"
                    :aria-label="t('connection.recentLogin')"
                    @change="emit('selectRecent')"
                >
                    <el-option :label="t('connection.manualEntry')" value="" />
                    <el-option
                        v-for="item in status.recentLogins"
                        :key="item.id"
                        :value="item.id"
                        :label="`${item.email} · ${hostText(item.environmentLabel)}`"
                    />
                </el-select>
                <el-button
                    :class="{ 'operation-lock': preserveAppearance && !!recentLoginId }"
                    :disabled="blocked || !recentLoginId"
                    @click="emit('deleteRecent')"
                    >{{ t("connection.deleteRecent") }}</el-button
                >
            </div>
        </el-form-item>
        <el-form-item :label="t('connection.organizationEmail')" prop="email">
            <el-input
                v-model="login.email"
                :class="{ 'operation-lock': preserveAppearance }"
                type="email"
                :maxlength="254"
                :disabled="blocked"
                :placeholder="t('connection.enterOrganizationEmail')"
                :aria-label="t('connection.organizationEmail')"
            />
        </el-form-item>
        <el-form-item v-if="environments.length > 1" :label="t('connection.environment')">
            <el-select
                v-model="login.environmentName"
                :class="{ 'operation-lock': preserveAppearance }"
                :disabled="blocked"
                :placeholder="t('connection.selectEnvironment')"
                :aria-label="t('connection.environment')"
            >
                <el-option v-for="environment in environments" :key="environment.name" :label="hostText(environment.displayName)" :value="environment.name" />
            </el-select>
        </el-form-item>
        <p v-if="environments.length > 1" class="hint">{{ t("connection.environmentHint") }}</p>
        <div class="connection-account">
            <el-checkbox
                :class="{ 'operation-lock': preserveAppearance }"
                :model-value="login.remember"
                @update:model-value="login.remember = $event === true"
                :disabled="blocked"
                >{{ t("connection.remember") }}</el-checkbox
            >
            <el-button
                type="primary"
                :class="{ 'operation-lock': preserveAppearance && !(environments.length > 1 && !login.environmentName) }"
                :loading="loading && pendingMethod === 'powerbi.serviceSignIn'"
                :disabled="blocked || (environments.length > 1 && !login.environmentName)"
                @click="emit('signIn')"
                >{{ t("connection.signIn") }}</el-button
            >
        </div>
        <p class="hint">{{ t("connection.signInHint") }}</p>
    </el-form>
</template>
<style scoped lang="scss">
.el-select {
    width: 100%;
}
p {
    margin: 0;
    line-height: 1.7;
}
.connection-account {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
}
.connection-recent {
    display: flex;
    gap: 8px;
    width: 100%;
    .el-select {
        min-width: 0;
        flex: 1;
    }
}
</style>
