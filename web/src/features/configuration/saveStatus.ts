/**
 * FilePath    : pbi-utils\web\src\features\configuration\saveStatus.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 根据宿主保存事实选择配置恢复提示, 不把未保存草稿或未知结果当成已提交.
 */
import type { ProjectSnapshot } from "@/bridge/client"
import { requiresModelVerification } from "@/bridge/modelAccess"

/** 返回当前项目的兜底提示键, 待核验优先于提交布尔值; 调用方仍优先展示宿主原错误, 本函数不改变保存权限或草稿. */
export function configurationRecoveryKey(project?: ProjectSnapshot): string {
    const result = project?.saveResult
    return requiresModelVerification(project)
        ? "configuration.verification"
        : result?.modelSaved === true
          ? "configuration.partial"
          : result?.fileSaved === true
            ? "configuration.fileSaved"
            : "configuration.uncommitted"
}
