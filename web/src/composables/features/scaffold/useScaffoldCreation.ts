/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useScaffoldCreation.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 一次创建动作内保存草稿、冻结精确计划并发布已核验结果.
 */
import { nextTick } from "vue"
import type { BridgeClient } from "@/bridge/client"
import { useFeedback } from "@/composables/ui/useFeedback"
import { configurationSections } from "@/features/configuration/draft"
import { saveDraft } from "@/features/scaffold/draft"
import { validateScaffold } from "@/features/scaffold/validation"
import type { ScaffoldOperations } from "@/composables/features/scaffold/useScaffoldOperations"
import type { ScaffoldValidationFocus } from "@/composables/features/scaffold/useScaffoldValidationFocus"
import { useI18n } from "@/composables/i18n/useI18n"
import type { ScaffoldState } from "@/composables/features/scaffold/useScaffoldState"
/** 取消只在提交前生效; 成功提示等权威结果及页面渲染, 同目录禁止重放.
 * @param state 当前草稿、创建可用性与发布事实.
 * @param operations 覆盖整条准备及发布链的互斥入口.
 * @param validation 字段错误与焦点入口.
 * @returns 唯一创建动作; 失败和取消由操作链记录, 不重放已发布工程.
 */
export function useScaffoldCreation(state: ScaffoldState, operations: ScaffoldOperations, validation: ScaffoldValidationFocus) {
    const {
        lifecycle,
        canCreate,
        validationInput,
        gitCapability,
        git,
        template,
        identity,
        projectName,
        draft,
        selectedTemplateQueries,
        templateQueries,
        templateQueryGroups,
        result,
        plan,
        publishedTargets,
        currentTarget,
        resultGitRequired,
        percent,
    } = state
    const { t, locale } = useI18n()
    const { run, tracked, invalidate } = operations
    const { clearIssue, showIssue } = validation
    const { showFeedback } = useFeedback()
    /** 一次创建动作内串行保存草稿并冻结精确计划, 取消在任意准备阶段生效且不跳过宿主校验. */
    async function create(): Promise<void> {
        if (!canCreate.value) return
        const issue = validateScaffold(validationInput.value)
        if (issue) {
            await showIssue(issue)
            return
        }
        clearIssue()
        await run("create", async () => {
            invalidate()
            await lifecycle.discardQueue
            const capability = await tracked("scaffold.templates", {}).promise
            gitCapability.value = capability.git ?? { available: false, reason: "GIT_PROBE_FAILED" }
            if (!capability.git?.available) git.value = false
            const current = template.value!
            identity.value = await saveDraft(
                { request: tracked } as BridgeClient,
                identity.value,
                { templateId: current.id, templateBaseline: current.baseline, projectName: projectName.value, locale: locale.value, gitInitialize: git.value },
                {
                    ...configurationSections(draft, locale.value),
                    templateQueries: [...selectedTemplateQueries.value],
                    templateQueryGroups: Object.fromEntries(
                        templateQueries.value
                            .filter((query) => selectedTemplateQueries.value.includes(query.id))
                            .map((query) => [query.id, templateQueryGroups.value[query.id] ?? query.queryGroup]),
                    ),
                },
                (saved) => {
                    identity.value = saved
                },
            )
            const frozen = await tracked("scaffold.preview", identity.value).promise
            if (lifecycle.disposed) return
            plan.value = frozen
            const value = await tracked("scaffold.create", { ...identity.value, digest: frozen.digest }).promise
            if (lifecycle.disposed) return
            result.value = value
            if (value.projectCreated) {
                lifecycle.publishedDigest = frozen.digest
                publishedTargets.value = new Set([...publishedTargets.value, currentTarget.value])
            }
            resultGitRequired.value = frozen.request.gitInitialize
            await nextTick()
            if (value.projectCreated && value.verified && (!frozen.request.gitInitialize || value.gitInitialized)) {
                percent.value = 100
                await nextTick()
                showFeedback({ message: t("scaffold.created"), type: "success" })
            }
        })
    }
    return { create }
}
