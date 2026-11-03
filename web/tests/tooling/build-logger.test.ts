/**
 * FilePath    : pbi-utils\web\tests\tooling\build-logger.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证全部构建诊断保留输出并失败, 分块体积超限不得例外放行.
 */
import { expect, it, vi } from "vitest"
import { createBuildLogger } from "../../scripts/build-logger"
import type { Logger } from "vite"

/** 提供原始输出替身, 不替换全局console诊断守卫. */
function output(): Logger {
    return { info: vi.fn(), warn: vi.fn(), warnOnce: vi.fn(), error: vi.fn(), clearScreen: vi.fn(), hasErrorLogged: () => false, hasWarned: false }
}

it.each(["warn", "warnOnce"] as const)("%s保留真实诊断并标记进程失败, 正常回调尽早抛错", (channel) => {
    const original = output(),
        fail = vi.fn(),
        logger = createBuildLogger(original, fail)
    expect(() => logger[channel]("unresolved fixture")).toThrow("构建诊断")
    expect(fail).toHaveBeenCalledExactlyOnceWith()
    expect(original[channel]).toHaveBeenCalledExactlyOnceWith("unresolved fixture", undefined)
})

it("error标记进程失败并保留输出, 让构建器继续抛出原始异常", () => {
    const original = output(),
        fail = vi.fn(),
        logger = createBuildLogger(original, fail)
    expect(() => logger.error("Build failed fixture")).not.toThrow()
    expect(fail).toHaveBeenCalledExactlyOnceWith()
    expect(original.error).toHaveBeenCalledExactlyOnceWith("Build failed fixture", undefined)
})

it.each(["warn", "warnOnce"] as const)("%s的500kB超限诊断必须阻断, 不再保留临时放行", (channel) => {
    const original = output(),
        fail = vi.fn(),
        logger = createBuildLogger(original, fail)
    const diagnostic =
        "[plugin builtin:vite-reporter] \n(!) Some chunks are larger than 500 kB after minification. Consider:\n- Using dynamic import() to code-split the application\n- Use build.rolldownOptions.output.codeSplitting to improve chunking: https://rolldown.rs/reference/OutputOptions.codeSplitting\n- Adjust chunk size limit for this warning via build.chunkSizeWarningLimit."
    expect(() => logger[channel](diagnostic)).toThrow("构建诊断")
    expect(fail).toHaveBeenCalledExactlyOnceWith()
    expect(original[channel]).toHaveBeenCalledExactlyOnceWith(diagnostic, undefined)
})
