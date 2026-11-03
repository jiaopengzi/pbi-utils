/**
 * FilePath    : pbi-utils\web\src\bridge\udfLintBudget.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : UDF 清理整批请求构造及普通桥接信封预算, 不放宽宿主通用限制.
 */
import type { ModelEditRequest, UdfLintResult } from "./modelContracts"
import { estimatedRequestBytes } from "./requestEnvelope"

/** 构造准确名称与基线的完整删除请求; 不拆分闭包, 不发送请求. */
export function udfLintDeleteRequest(result: UdfLintResult): ModelEditRequest {
    return {
        operation: "udf.deleteUnused",
        objectName: "UDF",
        property: "items",
        value: JSON.stringify({ projectId: result.projectId, baselineId: result.baselineId, names: result.unused }),
        table: "",
        pageId: "",
    }
}

/** 按实际 UUID 和最大安全代际估算完整信封, 同时遵守后端 value 字符上限; 超限仅禁止提交. */
export function udfLintDeleteAllowed(result: UdfLintResult): boolean {
    if (result.unused.length < 1 || result.unused.length > 8192) return false
    const request = udfLintDeleteRequest(result)
    return request.value.length <= 30000 && estimatedRequestBytes("model.edit", request) <= 65536
}
