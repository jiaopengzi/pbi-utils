/**
 * FilePath    : pbi-utils\web\src\app\modelSummary.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 复用宿主统一摘要, 为旧快照保留实际计数和未提供的在线布局边界.
 */
import type { ModelSummary, PowerBiModel, ProjectSnapshot } from "@/bridge/client"

/** 返回当前工作区摘要; 优先宿主结果, 旧在线快照的空页集合不代表实际零页. */
export function projectSummary(project: ProjectSnapshot): ModelSummary {
    return (
        project.summary ?? {
            compatibilityLevel: project.connection?.isLimited || project.model.compatibilityLevel <= 0 ? null : project.model.compatibilityLevel,
            tableCount: project.model.tables.length,
            measureCount: project.model.measures.length,
            reportPageCount:
                project.projectPath.startsWith("model://") || (project.connection && project.connection.kind !== "pbip") ? null : project.pages.length,
        }
    )
}

/** 返回扫描中的模型摘要; 多模型实例不合并兼容级别, 调用方按各模型分别展示. */
export function scannedSummary(model: PowerBiModel): ModelSummary {
    return (
        model.summary ?? {
            compatibilityLevel: model.compatibilityLevel > 0 ? model.compatibilityLevel : null,
            tableCount: model.tableCount,
            measureCount: model.measureCount,
            reportPageCount: null,
        }
    )
}
