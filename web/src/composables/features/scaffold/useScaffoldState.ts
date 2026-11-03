/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useScaffoldState.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 新建页面的响应式展示状态与可用性计算, 不保存DOM或请求到会话.
 */
import { computed, ref, shallowRef, toRefs, watch } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useI18n } from "@/composables/i18n/useI18n"
import { configurationSections } from "@/features/configuration/draft"
import { scaffoldSession } from "@/features/configuration/session"
import { validateScaffoldBase } from "@/features/scaffold/validation"
import type { ScaffoldValidationInput, ScaffoldValidationIssue } from "@/features/scaffold/validation"
import type { CalendarPreviewContext, ScaffoldPlan } from "@/features/scaffold/contracts"
/** 为当前挂载创建展示状态; 同桥接仅复用 session 中的纯业务数据.
 * @param bridge 当前文档的类型化宿主桥接.
 * @returns 当前挂载的响应式状态与可用性计算; 不保存请求或DOM到会话.
 */
export function useScaffoldState(bridge: BridgeClient) {
    const { t, locale, hostText } = useI18n()
    const {
        step,
        projectName,
        templateId,
        templateSelectionInvalid,
        git,
        output,
        visited,
        templates,
        templateQueries,
        selectedTemplateQueries,
        templateQueryGroups,
        definitions,
        templateDirectory,
        draft,
        measuresLoaded,
        details,
        identity,
        result,
        gitCapability,
        resultGitRequired,
        publishedTargets,
        detailsTemplateIdentity,
    } = scaffoldSession(bridge, locale.value)
    // 首次进入再挂载复杂表单, 已进入的步骤保留实例和编辑状态, 避免首屏一次创建隐藏的51项选择.
    watch(
        step,
        (value) => {
            visited.value = new Set([...visited.value, value])
        },
        { flush: "sync" },
    )
    const { measuresEnabled, sampleEnabled, calendarEnabled, periodsEnabled, udfsEnabled, measures, calendar, periods, udfs } = toRefs(draft)
    const sample = draft.sample
    const plan = shallowRef<ScaffoldPlan>()
    const templateManager = ref(false)
    // 非空远端选择必须先由当前挂载核实目录基线, 关闭可选段不影响其他配置创建.
    const udfValid = ref(false)
    const busy = ref(false),
        childBusy = ref(false),
        requestId = ref(""),
        error = ref(""),
        percent = ref(0),
        committed = ref(false),
        cancelling = ref(false),
        action = ref(""),
        simulation = ref(result.value?.isSimulation === true)
    const visibleError = computed(() => hostText(error.value))
    const validationIssue = shallowRef<ScaffoldValidationIssue>()
    const panelRoot = ref<HTMLElement>()
    const stage = ref("")
    const toolbarTop = ref(0)
    const measureDialog = ref(false)
    const formLocked = computed(() => busy.value || childBusy.value)
    const locked = computed(() => formLocked.value || measureDialog.value || templateManager.value)
    const template = computed(() => templates.value.find((item) => item.id === templateId.value))
    /** 创建前的代码预览只引用精确模板; 创建后退出编辑, 不再读取配置目标. */
    const previewContext = computed<CalendarPreviewContext | undefined>(() => {
        if (result.value?.projectCreated) return undefined
        const selected = template.value
        return selected && details.value && !templateSelectionInvalid.value && detailsTemplateIdentity.value === `${selected.id}:${selected.baseline}`
            ? { kind: "template", templateId: selected.id, templateBaseline: selected.baseline }
            : undefined
    })
    const stepIds = ["template", "sample", "measures", "calendar", "udfs"]
    const steps = computed(() => stepIds.map((id) => [`scaffold-${id}`, t(`scaffold.${id}`)]))
    const currentStep = computed({
        get: () => `scaffold-${step.value}`,
        set: (value: string) => {
            const id = value.replace(/^scaffold-/, "")
            if (!locked.value && !result.value?.projectCreated && steps.value.some((item) => item[0] === value)) step.value = id
        },
    })
    const currentTarget = computed(() => `${output.value.replaceAll("\\", "/").replace(/\/+$/, "")}/${projectName.value.trim()}`.toLowerCase())
    const targetCreated = computed(() => publishedTargets.value.has(currentTarget.value))
    const validationInput = computed<ScaffoldValidationInput>(() => ({
        template: template.value,
        projectName: projectName.value,
        output: output.value,
        ...configurationSections(draft, locale.value),
        details: details.value,
        periodDefinitions: definitions.value,
        templateQueries: selectedTemplateQueries.value,
        templateQueryGroups: templateQueryGroups.value,
        templateQueryDefinitions: templateQueries.value,
    }))
    const baseIssue = computed(() => (result.value?.projectCreated ? undefined : validateScaffoldBase(validationInput.value)))
    const createReady = computed(() => !baseIssue.value && !targetCreated.value && !result.value?.projectCreated)
    const canCreate = computed(
        () => !locked.value && createReady.value && (!draft.udfsEnabled || !draft.udfs.some((item) => item.source !== "local") || udfValid.value),
    )
    const gitStatus = computed(() =>
        t(
            !gitCapability.value
                ? "configuration.gitChecking"
                : gitCapability.value.reason === "GIT_NOT_FOUND"
                  ? "configuration.gitMissing"
                  : gitCapability.value.reason === "GIT_PROBE_TIMEOUT"
                    ? "configuration.gitTimeout"
                    : gitCapability.value.reason === "GIT_PROBE_FAILED"
                      ? "configuration.gitFailed"
                      : "configuration.gitUnavailable",
        ),
    )
    const canStartAnother = computed(() => !locked.value)
    /** 创建回执仅控制入口展示; 打开时宿主继续复核文件身份、保存状态及当前连接. */
    const createdOpenReady = computed(() => result.value?.projectCreated === true && result.value.verified)
    const desktopOpenReady = createdOpenReady
    const customTemplates = computed(() => templates.value.filter((item) => item.source !== "builtin"))
    const templateCounts = computed(() => ({
        available: customTemplates.value.filter((item) => !!item.baseline).length,
        invalid: customTemplates.value.filter((item) => !item.baseline).length,
    }))

    const lifecycle = { disposed: false, discardQueue: Promise.resolve(), publishedDigest: "", operationGeneration: 0 }
    return {
        bridge,
        lifecycle,
        step,
        projectName,
        templateId,
        templateSelectionInvalid,
        git,
        output,
        visited,
        templates,
        templateQueries,
        selectedTemplateQueries,
        templateQueryGroups,
        definitions,
        templateDirectory,
        draft,
        measuresLoaded,
        details,
        identity,
        result,
        gitCapability,
        resultGitRequired,
        publishedTargets,
        detailsTemplateIdentity,
        measuresEnabled,
        sampleEnabled,
        calendarEnabled,
        periodsEnabled,
        udfsEnabled,
        measures,
        calendar,
        periods,
        udfs,
        sample,
        plan,
        templateManager,
        udfValid,
        busy,
        childBusy,
        requestId,
        error,
        percent,
        committed,
        cancelling,
        action,
        simulation,
        visibleError,
        validationIssue,
        panelRoot,
        stage,
        toolbarTop,
        measureDialog,
        formLocked,
        locked,
        template,
        previewContext,
        steps,
        currentStep,
        currentTarget,
        targetCreated,
        validationInput,
        baseIssue,
        createReady,
        canCreate,
        gitStatus,
        canStartAnother,
        createdOpenReady,
        desktopOpenReady,
        customTemplates,
        templateCounts,
    }
}
export type ScaffoldState = ReturnType<typeof useScaffoldState>
