/**
 * FilePath    : pbi-utils\web\src\composables\app\useServiceAccount.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : Power BI Service 账号、云环境发现与隔离登录历史, 复用连接工作区请求排空.
 */
import { computed, ref, watch, type Ref } from "vue"
import type { FormInstance, FormRules } from "element-plus"
import { validateForm } from "@/ui/forms"
import type { Methods, PowerBiServiceStatus, RecentPowerBiServiceModel } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"

export type ServiceLoginDraft = { email: string; environmentName: string; remember: boolean }

/** 连接工作区提供相同请求身份和锁; 账号模块不另开请求通道. */
export type ServiceAccountCall = <M extends keyof Methods>(method: M, params: Methods[M]["params"]) => Promise<Methods[M]["result"]>
type ServiceAccountOptions = {
    blocked: Readonly<Ref<boolean>>
    serviceLoginPhase: Ref<"discoveringEnvironment" | "authenticating">
    call: ServiceAccountCall
    run: (action: () => Promise<void>, showProgress?: boolean) => Promise<void>
    loadModels: () => Promise<void>
    resetModels: () => void
    isMounted: () => boolean
    isServiceConnected: () => boolean
    onDisconnected: () => void
}

/** options 绑定当前连接工作区的请求门闩和模型目录; 返回账号表单与登录操作, 发现/登录失败由同一工作区留页报告, 不改变当前模型. */
export function useServiceAccount({
    blocked,
    serviceLoginPhase,
    call,
    run,
    loadModels,
    resetModels,
    isMounted,
    isServiceConnected,
    onDisconnected,
}: ServiceAccountOptions) {
    const { t } = useI18n()
    const status = ref<PowerBiServiceStatus>()
    const loginForm = ref<FormInstance>()
    const login = ref<ServiceLoginDraft>({ email: "", environmentName: "", remember: true })
    const environments = ref<Methods["powerbi.serviceSignIn"]["result"]["environments"]>([])
    const recentLoginId = ref("")
    const preferredEnvironment = ref("")
    const serviceRecents = ref<RecentPowerBiServiceModel[]>([])
    const recentStorageWarning = ref("")
    const rememberedLogin = computed(() => {
        const email = login.value.email.trim().toLowerCase()
        return (
            status.value?.recentLogins.find((item) => item.id === recentLoginId.value && item.email.toLowerCase() === email) ??
            status.value?.recentLogins.find((item) => item.email.toLowerCase() === email)
        )
    })
    const loginRules = computed<FormRules>(() => ({
        email: [
            { required: true, message: t("connection.emailRequired"), trigger: "blur" },
            { type: "email", message: t("connection.emailInvalid"), trigger: "blur", transform: (value: string) => value.trim() },
        ],
    }))

    /** 同一按钮执行发现和登录; 仅多云时等待选择, 单云由后端直接认证. */
    async function signIn() {
        if (blocked.value || !(await validateForm(loginForm.value))) return
        if (environments.value.length > 1 && !login.value.environmentName) return
        await run(async () => {
            serviceRecents.value = []
            const environmentName = environments.value.length > 1 ? login.value.environmentName : (rememberedLogin.value?.environmentName ?? "")
            serviceLoginPhase.value = environmentName ? "authenticating" : "discoveringEnvironment"
            const result = await call("powerbi.serviceSignIn", {
                email: login.value.email.trim(),
                environmentName,
                remember: login.value.remember,
            })
            if (!isMounted()) return
            status.value = result
            environments.value = result.environments
            if (result.account) {
                preferredEnvironment.value = result.account.environmentName
                recentLoginId.value =
                    result.recentLogins.find(
                        (item) => item.email.toLowerCase() === login.value.email.trim().toLowerCase() && item.environmentName === preferredEnvironment.value,
                    )?.id ?? ""
                await loadModels()
            } else login.value.environmentName = result.environments.find((item) => item.name === preferredEnvironment.value)?.name ?? ""
        })
    }

    /** 最近记录回填邮箱与首选云, 点击登录后由宿主重新核验环境并直接认证. */
    function selectRecentLogin() {
        const recent = status.value?.recentLogins.find((item) => item.id === recentLoginId.value)
        login.value.email = recent?.email ?? ""
        preferredEnvironment.value = recent?.environmentName ?? ""
        environments.value = []
        login.value.environmentName = ""
    }

    /** 删除只影响本机记录, 不退出当前账号或清除正在编辑的输入. */
    async function deleteRecentLogin() {
        if (!recentLoginId.value) return
        await run(async () => {
            status.value = await call("powerbi.serviceDeleteLogin", { id: recentLoginId.value })
            recentLoginId.value = ""
            preferredEnvironment.value = ""
        })
    }

    /** 退出清理账号目录, 当前 Service 模型由宿主同步断开. */
    async function signOut() {
        await run(async () => {
            const result = await call("powerbi.serviceSignOut", {})
            if (!isMounted()) return
            status.value = result
            serviceRecents.value = []
            recentStorageWarning.value = ""
            resetModels()
            environments.value = []
            login.value.environmentName = ""
            if (isServiceConnected()) onDisconnected()
        })
    }

    /** 邮箱编辑立即撤销原环境选择, 必须重新发现; 防止复用另一邮箱的云配置. */
    watch(
        () => login.value.email,
        () => {
            environments.value = []
            login.value.environmentName = ""
            const selected = status.value?.recentLogins.find((item) => item.id === recentLoginId.value)
            if (selected?.email.toLowerCase() !== login.value.email.trim().toLowerCase()) {
                recentLoginId.value = ""
                preferredEnvironment.value = ""
            }
        },
    )

    return {
        status,
        loginForm,
        login,
        environments,
        recentLoginId,
        serviceRecents,
        recentStorageWarning,
        loginRules,
        signIn,
        selectRecentLogin,
        deleteRecentLogin,
        signOut,
    }
}
