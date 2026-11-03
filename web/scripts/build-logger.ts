/**
 * FilePath    : pbi-utils\web\scripts\build-logger.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 通过Vite诊断API阻断全部构建告警和错误, 资源或分块体积问题不得作为成功放行.
 */
import { createLogger } from "vite"
import type { Logger, LogOptions } from "vite"

/** 正式构建使用独立Vite CLI, 诊断立即标记该进程失败, 保留取消/已有错误码. */
function markBuildFailed(): void {
    if (process.exitCode === undefined || Number(process.exitCode) === 0) process.exitCode = 1
}

/**
 * 保留Vite输出并标记独立CLI失败, 原生晚期回调吞掉异常也不能返回退出码0.
 * output为原日志器, fail默认设置进程失败码; 测试可注入回调, 不污染测试宿主.
 * error不另抛通用横幅异常, 让构建器保留真正的解析/写盘首因; 不作为多轮build() API成功判据.
 */
export function createBuildLogger(output: Logger = createLogger(), fail: () => void = markBuildFailed): Logger {
    /** 统一阻断warn和warnOnce, 保留原始参数、格式和输出次数. */
    function warn(channel: "warn" | "warnOnce", message: string, options?: LogOptions): void {
        fail()
        output[channel](message, options)
        throw new Error("构建诊断不允许作为成功结果: " + message)
    }
    return {
        info: (message, options) => output.info(message, options),
        warn: (message, options) => warn("warn", message, options),
        warnOnce: (message, options) => warn("warnOnce", message, options),
        error: (message, options) => {
            fail()
            output.error(message, options)
        },
        clearScreen: (type) => output.clearScreen(type),
        hasErrorLogged: (error) => output.hasErrorLogged(error),
        get hasWarned() {
            return output.hasWarned
        },
        set hasWarned(value) {
            output.hasWarned = value
        },
    }
}
