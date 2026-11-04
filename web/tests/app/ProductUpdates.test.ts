/**
 * FilePath    : pbi-utils\web\tests\app\ProductUpdates.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证更新设置权威回读、并发身份、通知和纯文本发布列表与固定来源入口.
 */
import { expect, it, vi } from "vitest"
import { effectScope, markRaw, ref } from "vue"
import { flushPromises, mount } from "@vue/test-utils"
import { BridgeClient, BridgeError } from "@/bridge/client"
import type { ProductUpdateCheck, ProductUpdateState } from "@/bridge/updateContracts"
import { MockTransport } from "@/bridge/mock"
import { useProductUpdates } from "@/composables/app/useProductUpdates"
import SoftwareUpdatePanel from "@/app/SoftwareUpdatePanel.vue"
import { showMessage } from "@/ui/message"
import { setLocale } from "@/i18n"
vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))

/** 创建可显式完成的结果, 模拟不遵守取消的迟到宿主响应. */
function deferred<T>() {
    let resolve!: (value: T) => void
    let reject!: (cause: Error) => void
    const promise = new Promise<T>((done, fail) => {
        resolve = done
        reject = fail
    })
    return { promise, resolve, reject }
}
/** 创建不含真实地址的宿主更新结果. */
function result(source: "github" | "gitee" = "github", status: ProductUpdateCheck["status"] = "available"): ProductUpdateCheck {
    return {
        source,
        currentVersion: "0.1.0",
        status,
        checkedAtUtc: "2026-11-03T00:00:00Z",
        latestVersion: "0.2.0",
        releases:
            status === "failed" || status === "noRelease"
                ? []
                : [
                      {
                          tag: "v0.2.0",
                          version: "0.2.0",
                          prerelease: false,
                          notes: "<img src=x onerror=alert(1)>\n[link](javascript:alert(1))",
                          notesTruncated: false,
                      },
                      { tag: "v0.1.0-v2.1", version: "0.1.0-v2.1", prerelease: true, notes: "Preview notes", notesTruncated: true },
                  ],
        errorCode: status === "failed" ? "UPDATE_TIMEOUT" : null,
        isSimulation: false,
    }
}

it("等待全局桥接就绪, 读取权威设置后后台检查, 同版本在应用实例中只通知一次", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const ready = ref(false)
    const request = vi.spyOn(bridge, "request")
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => ready.value,
        ),
    )!
    expect(request).not.toHaveBeenCalled()
    ready.value = true
    await flushPromises()
    expect(request.mock.calls).toContainEqual(["updates.check", { source: "github", automatic: true }])
    request.mockImplementation(
        (method) =>
            ({ id: "available", promise: Promise.resolve(method === "updates.check" ? { checkedNow: true, check: result() } : {}) }) as ReturnType<
                BridgeClient["request"]
            >,
    )
    vi.mocked(showMessage).mockClear()
    await updates.check(false)
    await updates.check(false)
    expect(showMessage).toHaveBeenCalledTimes(1)
    expect(showMessage).toHaveBeenCalledWith(expect.stringContaining("0.2.0"), "info")
    scope.stop()
    bridge.dispose()
})

it("保存失败保留旧设置, 切源保存成功后丢弃旧检查并发送取消", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const request = vi.spyOn(bridge, "request")
    request.mockReturnValueOnce({ id: "save-failed", promise: Promise.reject(new Error("fixture")) })
    await updates.save({ automaticCheck: false, source: "gitee" })
    expect(updates.state.value?.settings).toEqual({ automaticCheck: true, source: "github" })
    const late = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    request.mockReturnValueOnce({ id: "old-check", promise: late.promise })
    const checking = updates.check(false)
    await updates.save({ automaticCheck: true, source: "gitee" })
    expect(request.mock.calls).toContainEqual(["request.cancel", { requestId: "old-check" }])
    late.resolve({ checkedNow: true, check: result() })
    await checking
    expect(updates.state.value?.settings.source).toBe("gitee")
    expect(updates.state.value?.lastCheck?.source).not.toBe("github")
    scope.stop()
    bridge.dispose()
})

it("卸载取消尚未完成的请求, 迟到响应不更新状态或通知", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const before = updates.state.value
    const late = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "pending-check", promise: late.promise })
    const checking = updates.check(false)
    vi.mocked(showMessage).mockClear()
    scope.stop()
    late.resolve({ checkedNow: true, check: result() })
    await checking
    expect(request.mock.calls).toContainEqual(["request.cancel", { requestId: "pending-check" }])
    expect(updates.state.value).toBe(before)
    expect(showMessage).not.toHaveBeenCalled()
    bridge.dispose()
})

it("自动关闭后启动不检查, 手动检查绕过自动开关, 模拟结果明确标识", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    await bridge.request("updates.save", { source: "gitee", automaticCheck: false }).promise
    const request = vi.spyOn(bridge, "request")
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
        ),
    )!
    await flushPromises()
    expect(request.mock.calls.some(([method]) => method === "updates.check")).toBe(false)
    await updates.check(false)
    expect(request.mock.calls).toContainEqual(["updates.check", { source: "gitee", automatic: false }])
    expect(updates.checkResult.value).toMatchObject({ source: "gitee", status: "noRelease", isSimulation: true })
    scope.stop()
    bridge.dispose()
})

it("设置保存等待权威结果, 失败恢复原控件值, 切语言不重放请求", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(SoftwareUpdatePanel, { props: { bridge } })
    await flushPromises()
    const pending = deferred<ProductUpdateState>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "save", promise: pending.promise })
    const toggle = wrapper.getComponent({ name: "ElSwitch" })
    toggle.vm.$emit("change", false)
    await flushPromises()
    expect(toggle.props("modelValue")).toBe(true)
    expect(toggle.props("disabled")).toBe(true)
    expect(request.mock.calls).toEqual([["updates.save", { automaticCheck: false }]])
    pending.reject(new Error("private diagnostic must not be shown"))
    await flushPromises()
    expect(toggle.props("modelValue")).toBe(true)
    expect(showMessage).toHaveBeenLastCalledWith(expect.stringContaining("原设置"), "error")
    setLocale("en-us")
    await flushPromises()
    expect(wrapper.get("h2").text()).toBe("Software updates")
    expect(request).toHaveBeenCalledTimes(1)
    wrapper.unmount()
    bridge.dispose()
})

it("取消确认不提前释放检查守卫, 未结束期间不重复检查, 取消终态不报失败", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const pending = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "pending", promise: pending.promise })
    const checking = updates.check(false)
    updates.cancelCheck()
    expect(updates.checking.value).toBe(true)
    expect(updates.cancellationRequested.value).toBe(true)
    await updates.check(false)
    expect(request.mock.calls.filter(([method]) => method === "updates.check")).toHaveLength(1)
    vi.mocked(showMessage).mockClear()
    pending.reject(new BridgeError("CANCELLED", "cancelled"))
    await checking
    expect(updates.checking.value).toBe(false)
    expect(updates.error.value).toBe("")
    expect(showMessage).not.toHaveBeenCalled()
    scope.stop()
    bridge.dispose()
})

it("自动网络失败静默, 缓存与手动失败均不阻止独立打开发布页", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const request = vi.spyOn(bridge, "request")
    vi.mocked(showMessage).mockClear()
    request.mockReturnValueOnce({ id: "failed-auto", promise: Promise.resolve({ checkedNow: true, check: result("github", "failed") }) })
    await updates.check(true)
    expect(updates.checkResult.value?.status).toBe("failed")
    expect(showMessage).not.toHaveBeenCalled()
    await updates.open()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "github" })
    request.mockReturnValueOnce({ id: "cached", promise: Promise.resolve({ checkedNow: false, check: result() }) })
    await updates.check(true)
    expect(updates.checkResult.value?.releases).toHaveLength(2)
    request.mockReturnValueOnce({ id: "network", promise: Promise.reject(new Error("private network exception")) })
    await updates.check(false)
    expect(updates.error.value).not.toContain("private")
    const before = updates.error.value
    request.mockReturnValueOnce({ id: "open-failure", promise: Promise.reject(new Error("open failed")) })
    await updates.open()
    expect(updates.error.value).toBe(before)
    await updates.open()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "github" })
    scope.stop()
    bridge.dispose()
})

it("错误来源检查响应不展示列表或改写当前版本", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const before = updates.currentVersion.value
    const request = vi
        .spyOn(bridge, "request")
        .mockReturnValueOnce({ id: "wrong-source", promise: Promise.resolve({ checkedNow: true, check: result("gitee") }) })
    await updates.check(false)
    expect(updates.checkResult.value).toBeNull()
    expect(updates.currentVersion.value).toBe(before)
    await updates.open()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "github" })
    scope.stop()
    bridge.dispose()
})

it("加载、未检查、保存、检查与打开在途均保留发布页入口, 只使用已保存来源", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const original = bridge.request.bind(bridge)
    const loading = deferred<ProductUpdateState>()
    const request = vi
        .spyOn(bridge, "request")
        .mockImplementation((method, params) =>
            method === "updates.status" ? ({ id: "loading", promise: loading.promise } as ReturnType<BridgeClient["request"]>) : original(method, params),
        )
    const wrapper = mount(SoftwareUpdatePanel, { props: { bridge } })
    const button = wrapper.get('[data-action="release"]')
    expect(button.attributes("disabled")).toBeUndefined()
    await button.trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "github" })
    loading.resolve({ settings: { source: "gitee", automaticCheck: false }, lastCheck: null })
    await flushPromises()
    await button.trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "gitee" })
    const saving = deferred<ProductUpdateState>()
    request.mockReturnValueOnce({ id: "saving", promise: saving.promise })
    wrapper.getComponent({ name: "ElSelect" }).vm.$emit("change", "github")
    await flushPromises()
    expect(button.attributes("disabled")).toBeUndefined()
    await button.trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "gitee" })
    saving.resolve({ settings: { source: "github", automaticCheck: false }, lastCheck: null })
    await flushPromises()
    const checking = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    request.mockReturnValueOnce({ id: "checking", promise: checking.promise })
    await wrapper.get('[data-action="check"]').trigger("click")
    expect(button.attributes("disabled")).toBeUndefined()
    const opening = deferred<{ opened: boolean; isSimulation: boolean }>()
    request.mockReturnValueOnce({ id: "opening", promise: opening.promise })
    await button.trigger("click")
    expect(button.attributes("disabled")).toBeUndefined()
    const count = request.mock.calls.length
    await button.trigger("click")
    expect(request).toHaveBeenCalledTimes(count)
    checking.resolve({ checkedNow: true, check: result() })
    opening.resolve({ opened: true, isSimulation: false })
    await flushPromises()
    expect(wrapper.get(".update-result").attributes("data-status")).toBe("available")
    wrapper.unmount()
    bridge.dispose()
})

it("设置读取失败仍可使用默认来源打开发布页, 打开失败不覆盖设置错误", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const original = bridge.request.bind(bridge)
    const request = vi
        .spyOn(bridge, "request")
        .mockImplementation((method, params) =>
            method === "updates.status"
                ? ({ id: "failed", promise: Promise.reject(new Error("private settings error")) } as ReturnType<BridgeClient["request"]>)
                : original(method, params),
        )
    const wrapper = mount(SoftwareUpdatePanel, { props: { bridge } })
    await flushPromises()
    const before = wrapper.get(".update-result").text()
    request.mockReturnValueOnce({ id: "open-failure", promise: Promise.reject(new Error("private open error")) })
    await wrapper.get('[data-action="release"]').trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "github" })
    expect(wrapper.get(".update-result").text()).toBe(before)
    wrapper.unmount()
    bridge.dispose()
})

it("两个设置面板只提交实际修改字段并保留另一客户端新值, mock拒绝空patch与未知字段", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const first = mount(SoftwareUpdatePanel, { props: { bridge } })
    const second = mount(SoftwareUpdatePanel, { props: { bridge } })
    await flushPromises()
    const request = vi.spyOn(bridge, "request")
    first.getComponent({ name: "ElSelect" }).vm.$emit("change", "gitee")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("updates.save", { source: "gitee" })
    expect(second.getComponent({ name: "ElSelect" }).props("modelValue")).toBe("github")
    second.getComponent({ name: "ElSwitch" }).vm.$emit("change", false)
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("updates.save", { automaticCheck: false })
    expect(second.getComponent({ name: "ElSelect" }).props("modelValue")).toBe("gitee")
    expect((await bridge.request("updates.status", {}).promise).settings).toEqual({ source: "gitee", automaticCheck: false })
    await expect(bridge.request("updates.save", {}).promise).rejects.toMatchObject({ code: "VALIDATION_ERROR" })
    await expect(bridge.request("updates.save", { unexpected: true } as never).promise).rejects.toMatchObject({ code: "VALIDATION_ERROR" })
    first.unmount()
    second.unmount()
    bridge.dispose()
})

it("离开设置只取消手动检查, 全局自动检查继续且不会锁住模型导航", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const request = vi.spyOn(bridge, "request")
    const automatic = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    request.mockReturnValueOnce({ id: "automatic", promise: automatic.promise })
    const automaticTask = updates.check(true)
    let wrapper = mount(SoftwareUpdatePanel, { props: { bridge, updates } })
    wrapper.unmount()
    expect(request.mock.calls.some(([method]) => method === "request.cancel")).toBe(false)
    automatic.resolve({ checkedNow: true, check: result() })
    await automaticTask
    const manual = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    request.mockReturnValueOnce({ id: "manual", promise: manual.promise })
    const manualTask = updates.check(false)
    wrapper = mount(SoftwareUpdatePanel, { props: { bridge, updates } })
    expect(wrapper.emitted("busy")).toBeUndefined()
    wrapper.unmount()
    expect(request.mock.calls).toContainEqual(["request.cancel", { requestId: "manual" }])
    const before = updates.state.value
    manual.resolve({ checkedNow: true, check: result("github", "failed") })
    await manualTask
    expect(updates.state.value).toBe(before)
    scope.stop()
    bridge.dispose()
})

it.each(["available", "current", "noRelease", "failed"] as const)("展示 %s 状态与简洁版本号, 不渲染发布列表或说明", async (status) => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const state: ProductUpdateState = { settings: { automaticCheck: false, source: "github" }, lastCheck: result("github", status) }
    const original = bridge.request.bind(bridge)
    const request = vi
        .spyOn(bridge, "request")
        .mockImplementation((method, params) =>
            method === "updates.status" ? ({ id: "state", promise: Promise.resolve(state) } as ReturnType<BridgeClient["request"]>) : original(method, params),
        )
    const wrapper = mount(SoftwareUpdatePanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.get(".update-result").attributes("data-status")).toBe(status)
    expect(wrapper.find(".release-notes img").exists()).toBe(false)
    expect(wrapper.find(".release-notes a").exists()).toBe(false)
    expect(wrapper.find('[data-target="without-dotnet"]').exists()).toBe(false)
    expect(wrapper.findAll(".update-actions button")).toHaveLength(2)
    const release = wrapper.get('[data-action="release"]')
    expect(release.attributes("disabled")).toBeUndefined()
    await release.trigger("click")
    expect(request.mock.calls).toContainEqual(["updates.open", { source: "github" }])
    expect(wrapper.find(".update-release").exists()).toBe(false)
    expect(wrapper.find(".update-notes").exists()).toBe(false)
    expect(wrapper.find(".release-notes").exists()).toBe(false)
    expect(wrapper.find("summary").exists()).toBe(false)
    expect(wrapper.get(".update-release-version").text()).toContain("0.2.0")
    expect(wrapper.get(".update-actions").find('[data-action="check"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain("当前已是最新版本")
    wrapper.unmount()
    bridge.dispose()
})

it.each([
    [new Date(2026, 0, 2, 3, 4, 5).toISOString(), "2026-01-02 03:04:05"],
    [new Date(2026, 6, 9, 13, 14, 15).toISOString(), "2026-07-09 13:14:15"],
    ["invalid", "-"],
    ["", "-"],
])("检查时间 %s 使用机器本地时间, 双语保持固定格式且不改变权威值", async (timestamp, expected) => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const check = { ...result(), checkedAtUtc: timestamp }
    const original = bridge.request.bind(bridge)
    const request = vi
        .spyOn(bridge, "request")
        .mockImplementation((method, params) =>
            method === "updates.status"
                ? ({ id: "state", promise: Promise.resolve({ settings: { automaticCheck: false, source: "github" }, lastCheck: check }) } as ReturnType<
                      BridgeClient["request"]
                  >)
                : original(method, params),
        )
    const wrapper = mount(SoftwareUpdatePanel, { props: { bridge } })
    await flushPromises()
    const time = wrapper.get(".update-details time")
    expect(time.text()).toBe(expected)
    expect(time.attributes("datetime")).toBe(expected === "-" ? undefined : timestamp)
    expect(wrapper.get('[data-action="release"]').text()).toBe("打开软件发布页面")
    expect(wrapper.find(".update-notes").exists()).toBe(false)
    const requestsBefore = request.mock.calls.length
    setLocale("en-us")
    await flushPromises()
    expect(time.text()).toBe(expected)
    expect(wrapper.get(".update-details dt").text()).toBe("Last checked")
    expect(wrapper.get('[data-action="release"]').text()).toBe("Open software releases")
    expect(request).toHaveBeenCalledTimes(requestsBefore)
    expect(check.checkedAtUtc).toBe(timestamp)
    wrapper.unmount()
    bridge.dispose()
})

it("模拟打开只接受固定来源, 拒绝旧版本目标与任意 URL", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    await Promise.all(
        [{ source: "github", version: "0.2.0", target: "release" }, { source: "github", url: "https://example.invalid" }, { source: "other" }].map((params) =>
            expect(bridge.request("updates.open", params as never).promise).rejects.toMatchObject({ code: "VALIDATION_ERROR" }),
        ),
    )
    await expect(bridge.request("updates.open", { source: "gitee" }).promise).resolves.toEqual({ opened: false, isSimulation: true })
    bridge.dispose()
})

it("关闭自动检查会取消在途自动请求, 迟到发布列表不回填", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const pending = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "automatic", promise: pending.promise })
    const task = updates.check(true)
    await updates.save({ automaticCheck: false })
    expect(request.mock.calls).toContainEqual(["request.cancel", { requestId: "automatic" }])
    vi.mocked(showMessage).mockClear()
    pending.resolve({ checkedNow: true, check: result() })
    await task
    expect(updates.state.value?.settings.automaticCheck).toBe(false)
    expect(updates.checkResult.value).toBeNull()
    expect(showMessage).not.toHaveBeenCalled()
    await updates.check(true)
    expect(request.mock.calls.filter(([method]) => method === "updates.check")).toHaveLength(1)
    scope.stop()
    bridge.dispose()
})

it("切源后迟到的打开失败不污染新来源反馈, 卸载仍取消打开请求", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const scope = effectScope()
    const updates = scope.run(() =>
        useProductUpdates(
            () => bridge,
            () => true,
            false,
        ),
    )!
    await flushPromises()
    const pending = deferred<{ opened: boolean; isSimulation: boolean }>()
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "old-open", promise: pending.promise })
    const task = updates.open()
    await updates.save({ source: "gitee" })
    vi.mocked(showMessage).mockClear()
    pending.reject(new Error("old source failure"))
    await task
    expect(showMessage).not.toHaveBeenCalled()
    const next = deferred<{ opened: boolean; isSimulation: boolean }>()
    request.mockReturnValueOnce({ id: "new-open", promise: next.promise })
    const nextTask = updates.open()
    expect(request).toHaveBeenLastCalledWith("updates.open", { source: "gitee" })
    scope.stop()
    expect(request.mock.calls).toContainEqual(["request.cancel", { requestId: "new-open" }])
    next.resolve({ opened: true, isSimulation: false })
    await nextTask
    expect(showMessage).not.toHaveBeenCalled()
    bridge.dispose()
})
