/**
 * FilePath    : pbi-utils\web\src\bridge\measureBatchBudget.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 共享度量批次请求构造和完整 UTF-8 桥接预算, 防止回填不可保存的格式化候选.
 */
import type { MeasureChange, ModelEditRequest } from "./modelContracts"
import { estimatedRequestBytes } from "./requestEnvelope"

/** 将精确基线和属性集合构造成真实保存请求, 不修改输入或发送模型写入. */
export function measureBatchRequest(baselineId: string, items: readonly MeasureChange[]): ModelEditRequest {
    return { operation: "measure.batch", objectName: "度量值批量操作", property: "items", value: JSON.stringify({ baselineId, items }), table: "" }
}

/** 同时核验业务条数、value 字符数与 UUID/最大安全代际信封 UTF-8 字节上限; 超限不拆批或改写候选. */
export function measureBatchAllowed(baselineId: string, items: readonly MeasureChange[]): boolean {
    if (!items.length || items.length > 128) return false
    const params = measureBatchRequest(baselineId, items)
    return params.value.length <= 30000 && estimatedRequestBytes("model.edit", params) <= 65536
}
