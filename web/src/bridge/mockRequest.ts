/**
 * FilePath    : pbi-utils\web\src\bridge\mockRequest.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 定义显式浏览器模拟请求及回调, 不参与生产桥接授权.
 */

/** 模拟领域处理器接收的现有可选参数; 不接触真实文件或服务. */
export interface MockRequest {
    id: string
    method: string
    params: {
        formId?: string
        action?: "delete" | "clearPassword"
        recordId?: string
        values?: import("./client").RemoteConnectionValues
        requestId?: string
        text?: string
        value?: string
        style?: "short" | "long"
        modelId?: string
        email?: string
        environmentName?: string
        remember?: boolean
        id?: string
        operation?: string
        objectName?: string
        projectId?: string
        baselineId?: string
        property?: string
        table?: string
        mode?: "full" | "incremental"
        locale?: "zh-cn" | "en-us"
        showRecentFiles?: boolean
        daxFormatStyle?: "short" | "long"
        productCount?: number
        storeCount?: number
        inventoryCycle?: number
        startDate?: string
        endDate?: string
    }
}
/** 模拟传输的结构化消息出口. */
export type MockSend = (data: unknown) => void
