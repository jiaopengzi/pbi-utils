/**
 * FilePath    : pbi-utils\web\src\bridge\loggingContracts.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 定义宿主日志级别与固定日志目录的显示契约.
 */

/** 宿主固定日志目录与 Serilog 最低输出级别, 不包含日志正文或凭据. */
export type LogLevel = "Verbose" | "Debug" | "Information" | "Warning" | "Error" | "Fatal"
export interface LoggingView {
    level: LogLevel
    directory: string
    warning: string | null
    isSimulation: boolean
}
