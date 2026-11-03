/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useScaffoldCatalog.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 模板目录、精确元数据及宿主固定打开入口.
 */
import { initializeConfigurationCatalog } from "@/features/configuration/draft"
import type { ProjectSnapshot } from "@/bridge/client"
import type { ScaffoldOperations } from "@/composables/features/scaffold/useScaffoldOperations"
import type { ScaffoldState } from "@/composables/features/scaffold/useScaffoldState"
/** 模板刷新核实 ID 与基线, 不以同名替代来源, 目录仅由宿主绑定.
 * @param state 模板目录、草稿与已创建事实.
 * @param operations 当前挂载的受控请求入口.
 * @param acceptConnection 成功后向唯一工作区发布宿主连接快照.
 * @param canConnect 当前工作区是否允许替换连接, 待保存状态由外层控制.
 * @returns 目录加载、选择与固定打开操作; 请求错误由操作链展示.
 */
export function useScaffoldCatalog(
    state: ScaffoldState,
    operations: ScaffoldOperations,
    acceptConnection: (value: ProjectSnapshot) => void,
    canConnect: () => boolean,
) {
    const {
        lifecycle,
        result,
        details,
        template,
        templates,
        templateQueries,
        templateId,
        templateSelectionInvalid,
        detailsTemplateIdentity,
        draft,
        periods,
        git,
        gitCapability,
        definitions,
        templateDirectory,
        simulation,
        measures,
        measuresLoaded,
        error,
        output,
        locked,
        createdOpenReady,
        desktopOpenReady,
        canStartAnother,
        identity,
        step,
        validationIssue,
    } = state
    const { run, tracked, invalidate } = operations
    /** 读取当前模板实际 TOM 表、日期字段与基础度量值. */
    async function readDetails(): Promise<void> {
        details.value = undefined
        const current = template.value
        if (!current) return
        const value = await tracked("scaffold.templateDetails", { templateId: current.id, templateBaseline: current.baseline }).promise
        if (lifecycle.disposed) return
        details.value = value
        detailsTemplateIdentity.value = `${current.id}:${current.baseline}`
        initializeConfigurationCatalog(draft, value)
        periods.value.baseMeasures = []
    }
    /** 刷新白名单模板目录, 旧模板失效不会通过同名自动替代. */
    async function load(): Promise<void> {
        if (result.value?.projectCreated) return
        await run("load", async () => {
            const value = await tracked("scaffold.templates", {}).promise
            if (lifecycle.disposed) return
            const selected = template.value
            if (
                !result.value?.projectCreated &&
                selected &&
                !value.templates.some((item) => item.id === selected.id && item.baseline === selected.baseline && !!item.baseline)
            ) {
                templateSelectionInvalid.value = true
                templateId.value = ""
                details.value = undefined
                detailsTemplateIdentity.value = ""
            }
            templates.value = value.templates
            templateQueries.value = value.templateQueries
            gitCapability.value = value.git ?? { available: false, reason: "GIT_PROBE_FAILED" }
            if (!value.git?.available) git.value = false
            definitions.value = value.periods
            templateDirectory.value = value.templateDirectory
            simulation.value = value.isSimulation === true
            // 空集也是用户显式配置, 往返/刷新不得以默认六项复活已删除条目.
            if (!measuresLoaded.value) {
                measures.value = value.defaultMeasures
                measuresLoaded.value = true
            }
            if (value.measurePreferencesWarning) error.value = value.measurePreferencesWarning
            if (!templateId.value && !templateSelectionInvalid.value) templateId.value = value.templates.find((item) => !!item.baseline)?.id ?? ""
            invalidate()
            if (!result.value?.projectCreated && (!details.value || detailsTemplateIdentity.value !== `${template.value?.id}:${template.value?.baseline}`))
                await readDetails()
        })
    }
    /** 模板显式切换时丢弃旧元数据和预览. */
    async function changeTemplate(): Promise<void> {
        if (result.value?.projectCreated) return
        templateSelectionInvalid.value = false
        await run("details", readDetails)
    }
    /** 目标仅由系统目录选择器绑定, 网页没有路径写入参数. */
    async function choose(): Promise<void> {
        if (result.value?.projectCreated) return
        await run("choose", async () => {
            const value = await tracked("scaffold.chooseOutput", {}).promise
            if (!lifecycle.disposed) output.value = value.directory
        })
    }
    /** 只打开固定模板目录, 不增加通用打开路径 API. */
    async function openTemplates(): Promise<void> {
        await run("openTemplates", async () => {
            await tracked("scaffold.openTemplates", {}).promise
        })
    }
    /** 打开宿主绑定的已创建工程; Desktop 成功才发布连接, 失败/取消留页且不重放创建, 卸载后的迟到结果不更新工作区. */
    async function openCreated(method: "scaffold.openDirectory" | "scaffold.openDesktop"): Promise<void> {
        if (locked.value || !createdOpenReady.value || (method === "scaffold.openDesktop" && (!desktopOpenReady.value || !canConnect()))) return
        await run(method, async () => {
            if (method === "scaffold.openDesktop") {
                const value = await tracked(method, {}).promise
                if (!lifecycle.disposed && value.opened) acceptConnection(value.project)
            } else await tracked(method, {}).promise
        })
    }
    /** 明确结束当前目标模式, 新项目仍遵守已发布目录防重放且不删除现有工程. */
    function startAnother(): void {
        if (!canStartAnother.value) return
        invalidate()
        result.value = undefined
        identity.value = undefined
        step.value = "template"
        validationIssue.value = undefined
        void run("details", readDetails)
    }
    return { load, changeTemplate, choose, openTemplates, openCreated, startAnother }
}
