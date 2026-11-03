/**
 * FilePath    : pbi-utils\web\tests\app\ModelUdfLint.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : UDF lint 的只读预览、精确删除、目标切换、迟到响应和只读保护行为测试.
 */
import { expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient, type ProjectSnapshot, type UdfLintResult } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { previewProject } from "@/bridge/preview"
import ModelUdfLint from "@/app/ModelUdfLint.vue"

/** 建立可控宿主分析响应; desktop 启用同目标父级快照回传, 不分析真实模型也不调用真实保存. */
function setup(desktop = false) {
    const project = structuredClone(previewProject)
    if (desktop) {
        project.projectPath = "model://connected-desktop"
        project.connection = { kind: "desktop", label: "Desktop", instanceId: "desktop-instance" }
    }
    project.model.functions = [
        { name: "Lib.Unused", expression: "() => 1", description: "" },
        { name: "Lib.Other", expression: "() => 2", description: "" },
    ]
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ModelUdfLint, {
        props: {
            bridge,
            project,
            onUpdated: (value: ProjectSnapshot): void => {
                if (desktop) void wrapper.setProps({ project: value })
            },
        },
        global: {
            stubs: {
                UiDialog: { props: ["modelValue"], template: '<section v-if="modelValue"><slot/><slot name="footer"/></section>' },
            },
        },
    })
    const result: UdfLintResult = {
        projectId: project.projectId,
        baselineId: project.baselineId,
        unused: ["Lib.Unused", "Lib.Other"],
        totalCount: 2,
        complete: true,
    }
    const reply = (value: unknown) =>
        request.mockImplementationOnce(() => ({ id: "lint-request", promise: Promise.resolve(value) }) as ReturnType<BridgeClient["request"]>)
    return {
        project,
        wrapper,
        request,
        result,
        reply,
        cleanup: () => {
            wrapper.unmount()
            bridge.dispose()
            vi.restoreAllMocks()
        },
    }
}

it("分析只读展示准确候选, 取消预览没有模型修改", async () => {
    const { wrapper, request, reply, result, cleanup } = setup()
    try {
        expect(request).not.toHaveBeenCalled()
        reply(result)
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(request.mock.calls[0]).toEqual(["model.udfLint", { projectId: result.projectId, baselineId: result.baselineId }])
        expect(wrapper.findAll("li").map((item) => item.text())).toEqual(result.unused)
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "取消")!
            .trigger("click")
        expect(wrapper.findAll("li")).toHaveLength(0)
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it("Desktop 分析前读取最新模型, 发布新基线后保留分析预览", async () => {
    const { project, wrapper, request, result, cleanup } = setup(true)
    const fresh = { ...project, baselineId: "external-refresh" }
    try {
        request.mockImplementation(
            (method) =>
                ({
                    id: method,
                    promise: Promise.resolve(method === "project.inspect" ? fresh : { ...result, baselineId: fresh.baselineId }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(request.mock.calls.map(([method]) => method)).toEqual(["project.inspect", "model.udfLint"])
        expect(request.mock.calls[1]?.[1]).toEqual({ projectId: project.projectId, baselineId: "external-refresh" })
        expect(wrapper.emitted("updated")?.[0]).toEqual([fresh])
        expect(wrapper.findAll("li").map((item) => item.text())).toEqual(result.unused)
        expect(
            wrapper
                .findAll("button")
                .find((button) => button.text() === "确认删除并保存")
                ?.attributes("disabled"),
        ).toBeUndefined()
    } finally {
        cleanup()
    }
})

it("Desktop 确认期间模型变化先重分析并展示新候选, 再次确认才删除", async () => {
    const { project, wrapper, request, result, cleanup } = setup(true)
    let fresh = project
    try {
        request.mockImplementation(
            (method) =>
                ({
                    id: method,
                    promise: Promise.resolve(
                        method === "project.inspect"
                            ? fresh
                            : method === "model.udfLint"
                              ? { ...result, baselineId: fresh.baselineId, unused: fresh === project ? result.unused : ["Lib.Other"] }
                              : { ...fresh, model: { ...fresh.model, functions: [] } },
                    ),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("button").trigger("click")
        await flushPromises()
        fresh = { ...project, baselineId: "changed-during-confirmation" }
        const remove = () => wrapper.findAll("button").find((button) => button.text() === "确认删除并保存")!
        await remove().trigger("click")
        await flushPromises()
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
        expect(wrapper.findAll("li").map((item) => item.text())).toEqual(["Lib.Other"])
        expect(wrapper.props("project").baselineId).toBe("changed-during-confirmation")
        await remove().trigger("click")
        await flushPromises()
        const deletes = request.mock.calls.filter(([method]) => method === "model.edit")
        expect(deletes).toHaveLength(1)
        expect(JSON.parse((deletes[0]![1] as { value: string }).value)).toEqual({
            projectId: project.projectId,
            baselineId: "changed-during-confirmation",
            names: ["Lib.Other"],
        })
    } finally {
        cleanup()
    }
})

it("Desktop 读取失败不分析旧快照或提交删除", async () => {
    const { wrapper, request, cleanup } = setup(true)
    try {
        request.mockImplementationOnce(() => ({ id: "read-failed", promise: Promise.reject(new Error("模型读取失败")) }) as ReturnType<BridgeClient["request"]>)
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(request.mock.calls.map(([method]) => method)).toEqual(["project.inspect"])
        expect(document.querySelector(".el-message--error")?.textContent).toContain("模型读取失败")
        expect(wrapper.findAll("li")).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it("Desktop 外部引用变化使旧候选不再闲置时关闭删除预览", async () => {
    const { project, wrapper, request, result, cleanup } = setup(true)
    let changed = false
    try {
        request.mockImplementation(
            (method) =>
                ({
                    id: method,
                    promise: Promise.resolve(
                        method === "project.inspect"
                            ? { ...project, baselineId: changed ? "new-references" : project.baselineId }
                            : { ...result, baselineId: changed ? "new-references" : project.baselineId, unused: changed ? [] : result.unused },
                    ),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("button").trigger("click")
        await flushPromises()
        changed = true
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "确认删除并保存")!
            .trigger("click")
        await flushPromises()
        expect(wrapper.findAll("li")).toHaveLength(0)
        expect(wrapper.findAll("button").some((button) => button.text() === "确认删除并保存")).toBe(false)
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
        expect(wrapper.props("project").baselineId).toBe("new-references")
    } finally {
        cleanup()
    }
})

it.each([true, false])("Desktop 预检查请求取消 accepted=%s 后即使晚到成功也不继续分析", async (accepted) => {
    const { project, wrapper, request, cleanup } = setup(true)
    try {
        let finish!: (value: ProjectSnapshot) => void
        request.mockImplementationOnce(
            () =>
                ({
                    id: "read",
                    promise: new Promise<ProjectSnapshot>((resolve) => {
                        finish = resolve
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("button").trigger("click")
        await flushPromises()
        request.mockImplementationOnce(() => ({ id: "cancel", promise: Promise.resolve({ accepted }) }) as ReturnType<BridgeClient["request"]>)
        const cancel = new DOMWrapper(document.body).findAll("button").find((button) => button.text() === "取消")!
        await cancel.trigger("click")
        await flushPromises()
        request.mockImplementation(
            () =>
                ({
                    id: "lint",
                    promise: Promise.resolve({ projectId: project.projectId, baselineId: project.baselineId, unused: [], totalCount: 2, complete: true }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        finish(project)
        await flushPromises()
        expect(request.mock.calls.map(([method]) => method)).toEqual(["project.inspect", "request.cancel"])
    } finally {
        cleanup()
    }
})

it("Desktop 预检查期间目标切换并接受取消后不再发起旧目标分析", async () => {
    const { project, wrapper, request, cleanup } = setup(true)
    let finish!: (value: ProjectSnapshot) => void
    try {
        request.mockImplementationOnce(
            () =>
                ({
                    id: "desktop-read",
                    promise: new Promise<ProjectSnapshot>((resolve) => {
                        finish = resolve
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("button").trigger("click")
        await flushPromises()
        request.mockImplementationOnce(() => ({ id: "cancel", promise: Promise.resolve({ accepted: true }) }) as ReturnType<BridgeClient["request"]>)
        await wrapper.setProps({ project: { ...project, projectId: "new-target" } })
        await flushPromises()
        finish(project)
        await flushPromises()
        expect(request.mock.calls).toEqual([
            ["project.inspect", {}],
            ["request.cancel", { requestId: "desktop-read" }],
        ])
        expect(wrapper.findAll("li")).toHaveLength(0)
        expect(wrapper.emitted("updated")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it("确认仅提交原目标原基线的完整候选, 部分保存事实交还父级", async () => {
    const { project, wrapper, request, reply, result, cleanup } = setup()
    try {
        reply(result)
        await wrapper.get("button").trigger("click")
        await flushPromises()
        const partial: ProjectSnapshot = {
            ...project,
            model: { ...project.model, functions: [] },
            saveResult: { modelSaved: true, fileSaved: false, desktopSynchronized: false, message: "pending", errorCode: "FILE_PENDING" },
        }
        reply(partial)
        await wrapper
            .findAll("button")
            .find((button) => button.text() === "确认删除并保存")!
            .trigger("click")
        await flushPromises()
        const [method, parameters] = request.mock.calls[1]!
        expect(method).toBe("model.edit")
        expect(parameters).toMatchObject({ operation: "udf.deleteUnused", property: "items", table: "", pageId: "" })
        expect(JSON.parse((parameters as { value: string }).value)).toEqual({
            projectId: result.projectId,
            baselineId: result.baselineId,
            names: result.unused,
        })
        expect(wrapper.emitted("updated")?.[0]).toEqual([partial])
        expect(wrapper.findAll("li")).toHaveLength(0)
    } finally {
        cleanup()
    }
})

it("目标切换后旧分析响应不打开预览, 已接受取消仍等待原终态", async () => {
    const { project, wrapper, request, result, cleanup } = setup()
    try {
        let finish!: (value: UdfLintResult) => void
        request.mockImplementationOnce(
            () =>
                ({
                    id: "late-lint",
                    promise: new Promise<UdfLintResult>((resolve) => {
                        finish = resolve
                    }),
                }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("button").trigger("click")
        await flushPromises()
        request.mockImplementationOnce(() => ({ id: "cancel-lint", promise: Promise.resolve({ accepted: true }) }) as ReturnType<BridgeClient["request"]>)
        await wrapper.setProps({ project: { ...project, projectId: "different-target" } })
        await flushPromises()
        expect(request.mock.calls.at(-1)).toEqual(["request.cancel", { requestId: "late-lint" }])
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
        finish(result)
        await flushPromises()
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
        expect(wrapper.findAll("li")).toHaveLength(0)
        expect(wrapper.emitted("updated")).toBeUndefined()
    } finally {
        cleanup()
    }
})

it("基线变化立即撤销已展示候选, 不能提交旧删除集合", async () => {
    const { project, wrapper, request, reply, result, cleanup } = setup()
    try {
        reply(result)
        await wrapper.get("button").trigger("click")
        await flushPromises()
        await wrapper.setProps({ project: { ...project, baselineId: "changed" } })
        await flushPromises()
        expect(wrapper.findAll("li")).toHaveLength(0)
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it("Desktop 实例切换即使展示基线相同也撤销旧删除确认", async () => {
    const { project, wrapper, request, result, cleanup } = setup(true)
    try {
        request.mockImplementation(
            (method) => ({ id: method, promise: Promise.resolve(method === "project.inspect" ? project : result) }) as ReturnType<BridgeClient["request"]>,
        )
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(wrapper.findAll("li")).toHaveLength(2)
        await wrapper.setProps({ project: { ...project, connection: { ...project.connection!, instanceId: "different-instance" } } })
        await flushPromises()
        expect(wrapper.findAll("li")).toHaveLength(0)
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it("无法完整识别的表达式不提供删除确认", async () => {
    const { wrapper, request, reply, result, cleanup } = setup()
    try {
        reply({ ...result, complete: false, unused: [] })
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(document.querySelector(".el-message")?.textContent).toContain("无法完整识别")
        expect(wrapper.findAll("li")).toHaveLength(0)
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it("有效候选超过128项仍可一次确认, 不人为拆开依赖闭包", async () => {
    const { wrapper, reply, result, cleanup } = setup()
    try {
        reply({ ...result, unused: Array.from({ length: 129 }, (_, index) => `Lib.F${index}`), totalCount: 129 })
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(wrapper.findAll("li")).toHaveLength(129)
        expect(
            wrapper
                .findAll("button")
                .find((button) => button.text() === "确认删除并保存")!
                .attributes("disabled"),
        ).toBeUndefined()
    } finally {
        cleanup()
    }
})

it.each(["characters", "utf8"])("%s 预算超限在预览即禁止提交, 不等待模型写入错误", async (kind) => {
    const { wrapper, request, reply, result, cleanup } = setup()
    try {
        const prefix = (kind === "utf8" ? "函" : "x").repeat(80)
        const names = Array.from({ length: kind === "utf8" ? 300 : 400 }, (_, index) => `Lib.${prefix}${index}`)
        reply({ ...result, unused: names, totalCount: names.length })
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(Array.from(document.querySelectorAll(".el-message--warning"), (message) => message.textContent).join(" ")).toContain("超过单次请求")
        expect(
            wrapper
                .findAll("button")
                .find((button) => button.text() === "确认删除并保存")!
                .attributes("disabled"),
        ).toBeDefined()
        expect(request.mock.calls.some(([method]) => method === "model.edit")).toBe(false)
    } finally {
        cleanup()
    }
})

it.each(["remote", "pending", "locked"])("%s 状态下原生禁用且无分析请求", async (state) => {
    const { project, wrapper, request, cleanup } = setup()
    try {
        await wrapper.setProps(
            state === "locked"
                ? { locked: true }
                : {
                      project: {
                          ...project,
                          ...(state === "remote" ? { connection: { kind: "ssas-xmla" as const, label: "Remote" } } : { hasPendingChanges: true }),
                      },
                  },
        )
        expect(wrapper.get("button").attributes("disabled")).toBeDefined()
        await wrapper.get("button").trigger("click")
        expect(request).not.toHaveBeenCalled()
    } finally {
        cleanup()
    }
})
