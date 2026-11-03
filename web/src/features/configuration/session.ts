/**
 * FilePath    : pbi-utils\web\src\features\configuration\session.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按文档桥接会话保留配置草稿和已创建目标展示, 不持久化路径授权或跨文档恢复.
 */
import { ref, shallowRef } from "vue"
import type { BridgeClient } from "@/bridge/client"
import type { AppLocale } from "@/i18n"
import type { DraftIdentity, PbipTemplateInfo, ScaffoldPeriodTemplate, ScaffoldResult, TemplateDetails, TemplateQueryDefinition } from "../scaffold/contracts"
import { createConfigurationDraft, type ConfigurationDraft } from "./draft"
const scaffoldSessions = new WeakMap<BridgeClient, ReturnType<typeof createScaffoldSession>>()
const currentDrafts = new WeakMap<BridgeClient, Map<string, ConfigurationDraft>>()
/** 创建只含数据的会话状态, 不保留已卸载组件的 DOM, 请求或监听器. */
function createScaffoldSession(locale: AppLocale) {
    return {
        step: ref("template"),
        projectName: ref("MyPowerBIProject"),
        templateId: ref(""),
        templateSelectionInvalid: ref(false),
        git: ref(false),
        output: ref(""),
        visited: shallowRef(new Set(["template"])),
        templates: ref<PbipTemplateInfo[]>([]),
        templateQueries: ref<TemplateQueryDefinition[]>([]),
        selectedTemplateQueries: ref<string[]>([]),
        templateQueryGroups: ref<Record<string, string>>({}),
        definitions: ref<ScaffoldPeriodTemplate[]>([]),
        templateDirectory: ref(""),
        draft: createConfigurationDraft(locale),
        measuresLoaded: ref(false),
        details: shallowRef<TemplateDetails>(),
        identity: shallowRef<DraftIdentity>(),
        result: shallowRef<ScaffoldResult>(),
        gitCapability: shallowRef<{ available: boolean; reason?: string | null }>(),
        resultGitRequired: ref(false),
        publishedTargets: shallowRef(new Set<string>()),
        detailsTemplateIdentity: ref(""),
    }
}
/** 同一 BridgeClient 页面往返恢复数据, 新文档使用新桥接即获得独立状态. */
export function scaffoldSession(bridge: BridgeClient, locale: AppLocale) {
    let session = scaffoldSessions.get(bridge)
    if (!session) {
        session = createScaffoldSession(locale)
        scaffoldSessions.set(bridge, session)
    }
    return session
}
/** 当前模型草稿仅以宿主完整目标句柄为键, 目标换绑后不会按同名或路径迁移. */
export function configurationDrafts(bridge: BridgeClient): Map<string, ConfigurationDraft> {
    let drafts = currentDrafts.get(bridge)
    if (!drafts) {
        drafts = new Map()
        currentDrafts.set(bridge, drafts)
    }
    return drafts
}
