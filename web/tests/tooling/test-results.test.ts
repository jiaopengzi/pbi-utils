/**
 * FilePath    : pbi-utils\web\tests\tooling\test-results.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证实际测试结果统计, 零执行失败并如实输出部分跳过的UNRUN边界.
 */
import { expect, it, vi } from "vitest"
import { assertExecutedTests } from "../../scripts/test-results"

it.each([{ states: [] }, { states: ["skipped"] }, { states: ["pending", "skipped"] }] as const)("无实际执行结果$states时失败", ({ states }) => {
    expect(() =>
        assertExecutedTests(
            states.map((state) => ({ state, name: "fixture" })),
            { info: vi.fn() },
        ),
    ).toThrow("没有实际执行")
})

it("部分跳过允许继续但准确标记UNRUN, 失败也算实际执行并由Vitest保留失败状态", () => {
    const output = { info: vi.fn() }
    assertExecutedTests(
        [
            { state: "passed", name: "passed fixture" },
            { state: "failed", name: "failed fixture" },
            { state: "skipped", name: "skipped fixture" },
        ],
        output,
    )
    expect(output.info).toHaveBeenCalledExactlyOnceWith("[UNRUN] 1 个测试未执行:\nskipped fixture")
})
