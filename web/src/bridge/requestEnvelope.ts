/**
 * FilePath    : pbi-utils\web\src\bridge\requestEnvelope.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 统一真实请求与保存预估的完整桥接信封, 将连接代际计入 UTF-8 预算.
 */

/** 构造实际发送的五字段信封; id 和代际来自桥接客户端, 不改写业务参数. */
export function requestEnvelope(method: string, params: unknown, id: string, connectionRevision: number) {
    return { protocolVersion: 1, id, method, params, connectionRevision }
}

/** 以真实 UUID 长度和最大安全代际估算完整保存请求; 序列化失败向调用方传播, 不缩短内容. */
export function estimatedRequestBytes(method: string, params: unknown): number {
    return new TextEncoder().encode(JSON.stringify(requestEnvelope(method, params, "00000000-0000-0000-0000-000000000000", Number.MAX_SAFE_INTEGER))).length
}
