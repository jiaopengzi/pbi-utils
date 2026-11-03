/**
 * FilePath    : pbi-utils\web\scripts\test-results.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用Vitest公开结果API拒绝零执行的成功结果, 部分跳过明确标记UNRUN并保留默认报告.
 */
import type { Reporter } from "vitest/node"

interface TestExecutionResult {
    state: "passed" | "failed" | "skipped" | "pending"
    name: string
}

/** 根据真实结果核验至少执行一例; 失败仍交给Vitest, skipped/pending不会伪装成已执行. */
export function assertExecutedTests(results: TestExecutionResult[], output: Pick<Console, "info"> = console): void {
    const unrun = results.filter((result) => result.state !== "passed" && result.state !== "failed")
    if (unrun.length > 0) output.info(`[UNRUN] ${unrun.length} 个测试未执行:\n${unrun.map((result) => result.name).join("\n")}`)
    if (results.length === unrun.length) throw new Error("没有实际执行的测试结果, 不能将零测试、全部skip或todo报告为成功")
}

/** 追加到默认reporter, 在公开onTestRunEnd回调读取真实测试状态; 不扫描源码或禁止合法的部分skip. */
export function createTestResultsReporter(): Reporter {
    return {
        onTestRunEnd(modules) {
            assertExecutedTests(
                modules.flatMap((module) =>
                    [...module.children.allTests()].map((test) => ({ state: test.result().state, name: `${module.relativeModuleId} > ${test.fullName}` })),
                ),
            )
        },
    }
}
