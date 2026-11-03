/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfSourceRepository.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证多平台和内网 HTTP 仓库登记、引用边界及显式模拟一致性.
 */
import { expect, it } from "vitest"
import { normalizeUdfRepositoryUrl, validateUdfRepositoryReference } from "@/features/scaffold/udfSourceRepository"
import { UdfMock } from "@/features/scaffold/udfMock"

it("GitHub 仓库大小写、默认端口、.git 与末尾斜线规范为相同身份", () => {
    expect(normalizeUdfRepositoryUrl("https://GitHub.com:443/Example/Library.git/")).toBe("https://github.com/example/library")
    expect(normalizeUdfRepositoryUrl("https://github.com/example/library")).toBe("https://github.com/example/library")
})

it.each([
    ["https://Gitee.com/Example/Library.git/", "https://gitee.com/Example/Library.git"],
    ["https://gitlab.com/Group/Subgroup/Library.git", "https://gitlab.com/Group/Subgroup/Library.git"],
    ["http://git.internal:8080/Team/Library.git", "http://git.internal:8080/Team/Library.git"],
    ["http://git.internal/Library.git", "http://git.internal/Library.git"],
    ["http://10.10.10.109/Team/Library", "http://10.10.10.109/Team/Library"],
])("跨平台来源规范化 %s 并保留仓库路径大小写", (value, expected) => {
    expect(normalizeUdfRepositoryUrl(value)).toBe(expected)
})

it.each([
    "http://github.com/example/library",
    "https://github.com:444/example/library",
    "https://user:secret@github.com/example/library",
    "https://github.com/example/library?token=secret",
    "https://github.com/example/library#main",
    "https://github.com/example/library/tree/main",
    "https://github.com/example/../example/library",
    "https://github.com/example/%6cibrary",
    "https://github.com/\u212a/library",
    "https://github.com/example/\u212aibrary",
    "https://github.com/example/.git",
    "https://github.com/example/library\n",
    "http://localhost/Team/Library.git",
    "http://git.localhost/Team/Library.git",
    "http://git.internal./Team/Library.git",
    "http://git.internal/Team/.git",
    "http://git.internal/Team/Library.git//",
    `http://git.internal/Team/${"a".repeat(256)}`,
])("来源登记拒绝不支持的仓库地址 %s", (value) => {
    expect(() => normalizeUdfRepositoryUrl(value)).toThrow()
})

it("显式模拟可登记内网和 GitLab 来源并即时列出全部来源", () => {
    const mock = new UdfMock()
    const internal = mock.handle("udf.sourceSave", { name: "Team", baseUrl: "http://git.internal:8080/Team/Library.git" })
    const gitlab = mock.handle("udf.sourceSave", { name: "GitLab", baseUrl: "https://gitlab.com/Group/Subgroup/Library" })
    expect(internal).toMatchObject({ name: "Team", baseUrl: "http://git.internal:8080/Team/Library.git" })
    expect(gitlab).toMatchObject({ name: "GitLab", baseUrl: "https://gitlab.com/Group/Subgroup/Library" })
    expect(mock.handle("udf.sources", {})).toMatchObject({
        items: [{ id: "daxlib", name: "DAXLib", baseUrl: "https://github.com/daxlib/daxlib" }, { id: "local" }, { name: "Team" }, { name: "GitLab" }],
    })
})

it("引用长度边界为 256, 空引用绑定默认分支, 分支和标签保留精确大小写", () => {
    expect(validateUdfRepositoryReference(null)).toBeNull()
    expect(validateUdfRepositoryReference("Feature/Release-1")).toBe("Feature/Release-1")
    expect(validateUdfRepositoryReference("a".repeat(256))).toBe("a".repeat(256))
    expect(() => validateUdfRepositoryReference("a".repeat(257))).toThrow()
})

it.each(["", "-main", "/main", "main/", "main.", "refs//main", "a..b", "a@{b", "a b", "a\\b", ".main", "main.lock", "@"])(
    "来源登记拒绝不安全的引用 %s",
    (value) => expect(() => validateUdfRepositoryReference(value)).toThrow(),
)

it("显式模拟默认使用官方 GitHub 来源, 只允许 Git 协议且以仓库和引用防重复", () => {
    const mock = new UdfMock()
    expect(mock.handle("udf.sources", {})).toMatchObject({
        items: [
            { id: "daxlib", protocol: "git", baseUrl: "https://github.com/daxlib/daxlib" },
            { id: "local", baseUrl: null },
        ],
    })
    expect(() => mock.handle("udf.sourceSave", { name: "API", baseUrl: "https://github.com/example/library", protocol: "api" })).toThrow()
    expect(() => mock.handle("udf.sourceSave", { name: "Official duplicate", baseUrl: "https://github.com/DAXLib/DAXLib.git" })).toThrow("UDF_SOURCE_DUPLICATE")
    expect(mock.handle("udf.sourceSave", { name: "Tag", baseUrl: "https://github.com/DAXLib/DAXLib.git", gitRef: "v1" })).toMatchObject({
        protocol: "git",
        gitRef: "v1",
    })
})
