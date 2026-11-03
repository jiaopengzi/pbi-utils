/**
 * FilePath    : pbi-utils\web\src\features\code\formatDax.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 复用宿主本地 DAX 格式化, 返回可取消请求而不写入模型.
 */
import { translate as t } from "@/i18n"
import type { BridgeClient, Methods } from "@/bridge/client"

export type DaxFormatStyle = Methods["dax.format"]["params"]["style"]

/** 格式化有界表达式列表, 样式默认 long 并保留显式 short; 返回可取消请求, 输入超限抛错, 宿主成功后由调用方决定预览或保存. */
export function formatDax(bridge: BridgeClient, expressions: string[], style: DaxFormatStyle = "long") {
    const value = JSON.stringify(expressions)
    if (!expressions.length || expressions.length > 128 || expressions.some((s) => !s.trim()) || value.length > 30000) throw new Error(t("model.formatBounds"))
    return bridge.request("dax.format", { value, style })
}
