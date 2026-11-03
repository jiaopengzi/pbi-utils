/**
 * FilePath    : pbi-utils\web\tests\app\ProductUpdates.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证更新设置权威回读、并发身份、通知和纯文本下载入口.
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
        releaseUrl: "https://example.invalid/release",
        notes: "<img src=x onerror=alert(1)>\n[link](javascript:alert(1))",
        assets: [{ dotNetMode: "without-dotnet", fileName: "fixture.zip", downloadUrl: "https://example.invalid/file", size: null }],
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

it("自动网络失败静默保留失败状态, 节流缓存不能启用下载, 手动失败禁止旧入口", async () => {
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
    request.mockReturnValueOnce({ id: "cached", promise: Promise.resolve({ checkedNow: false, check: result() }) })
    await updates.check(true)
    expect(updates.canOpen("without-dotnet")).toBe(false)
    request.mockReturnValueOnce({ id: "fresh", promise: Promise.resolve({ checkedNow: true, check: result() }) })
    await updates.check(false)
    expect(updates.canOpen("without-dotnet")).toBe(true)
    request.mockReturnValueOnce({ id: "network", promise: Promise.reject(new Error("private network exception")) })
    await updates.check(false)
    expect(updates.canOpen("without-dotnet")).toBe(false)
    expect(updates.error.value).not.toContain("private")
    scope.stop()
    bridge.dispose()
})

it.each(["failed", "noRelease"] as const)("%s 撤销本实例授权, 其他客户端的同版本成功缓存不能重新开放下载", async (status) => {
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
    request.mockReturnValueOnce({ id: "fresh", promise: Promise.resolve({ checkedNow: true, check: result() }) })
    await updates.check(false)
    expect(updates.canOpen("without-dotnet")).toBe(true)
    request.mockReturnValueOnce({ id: "revoked", promise: Promise.resolve({ checkedNow: true, check: result("github", status) }) })
    await updates.check(false)
    expect(updates.canOpen("without-dotnet")).toBe(false)
    await updates.save({ source: "gitee" })
    request.mockReturnValueOnce({
        id: "foreign-cache",
        promise: Promise.resolve({ settings: { source: "github", automaticCheck: true }, lastCheck: result() }),
    })
    await updates.save({ source: "github" })
    expect(updates.checkResult.value?.status).toBe("available")
    expect(updates.canOpen("without-dotnet")).toBe(false)
    expect(updates.canOpen("release")).toBe(false)
    scope.stop()
    bridge.dispose()
})

it("同版本缓存后来替换附件不能借旧检查授权, 保存回读与失效响应均要求重新核验", async () => {
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
    request.mockReturnValueOnce({ id: "fresh", promise: Promise.resolve({ checkedNow: true, check: result() }) })
    await updates.check(false)
    expect(updates.canOpen("without-dotnet")).toBe(true)
    const foreign = result()
    foreign.assets[0] = { dotNetMode: "without-dotnet", fileName: "new.exe", downloadUrl: "https://example.invalid/new", size: null }
    request.mockReturnValueOnce({ id: "cache", promise: Promise.resolve({ settings: { source: "github", automaticCheck: false }, lastCheck: foreign }) })
    await updates.save({ automaticCheck: false })
    expect(updates.canOpen("without-dotnet")).toBe(false)
    request.mockReturnValueOnce({ id: "new-fresh", promise: Promise.resolve({ checkedNow: true, check: foreign }) })
    await updates.check(false)
    expect(updates.canOpen("without-dotnet")).toBe(true)
    const late = deferred<{ checkedNow: boolean; check: ProductUpdateCheck }>()
    request.mockReturnValueOnce({ id: "late", promise: late.promise })
    const checking = updates.check(false)
    updates.cancelCheck()
    late.resolve({ checkedNow: true, check: foreign })
    await checking
    expect(updates.canOpen("without-dotnet")).toBe(false)
    request.mockReturnValueOnce({ id: "throttled", promise: Promise.resolve({ checkedNow: false, check: foreign }) })
    await updates.check(false)
    expect(updates.canOpen("without-dotnet")).toBe(false)
    scope.stop()
    bridge.dispose()
})

it("status缓存回读或错误来源检查响应不能继承已核验链接", async () => {
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
    const original = bridge.request.bind(bridge)
    const request = vi.spyOn(bridge, "request")
    const check = result()
    request.mockReturnValueOnce({ id: "fresh", promise: Promise.resolve({ checkedNow: true, check }) })
    await updates.check(false)
    expect(updates.canOpen("release")).toBe(true)
    request.mockImplementation((method, params) =>
        method === "updates.status"
            ? ({ id: "cache", promise: Promise.resolve({ settings: { automaticCheck: true, source: "github" }, lastCheck: check }) } as ReturnType<
                  BridgeClient["request"]
              >)
            : original(method, params),
    )
    await updates.load()
    expect(updates.canOpen("release")).toBe(false)
    request.mockReturnValueOnce({ id: "wrong-source", promise: Promise.resolve({ checkedNow: true, check: result("gitee") }) })
    await updates.check(false)
    expect(updates.checkResult.value).toBeNull()
    expect(updates.canOpen("release")).toBe(false)
    scope.stop()
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

it.each(["available", "current", "noRelease", "failed"] as const)("展示 %s 的独立结果、纯文本说明与精确资产入口", async (status) => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const state: ProductUpdateState = { settings: { automaticCheck: false, source: "github" }, lastCheck: result("github", status) }
    const original = bridge.request.bind(bridge)
    const request = vi.spyOn(bridge, "request")
    request.mockImplementation((method, params) =>
        method === "updates.status"
            ? ({ id: "state", promise: Promise.resolve(state) } as ReturnType<BridgeClient["request"]>)
            : method === "updates.check"
              ? ({ id: "check", promise: Promise.resolve({ checkedNow: true, check: state.lastCheck }) } as ReturnType<BridgeClient["request"]>)
              : original(method, params),
    )
    const wrapper = mount(SoftwareUpdatePanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.get(".update-result").attributes("data-status")).toBe(status)
    expect(wrapper.find(".release-notes img").exists()).toBe(false)
    expect(wrapper.find(".release-notes a").exists()).toBe(false)
    expect(wrapper.find('[data-target="with-dotnet"]').exists()).toBe(false)
    expect(wrapper.findAll(".update-actions button")).toHaveLength(2)
    const download = wrapper.get('[data-target="without-dotnet"]')
    expect(download.attributes("disabled")).toBeDefined()
    await wrapper.get('[data-action="check"]').trigger("click")
    await flushPromises()
    if (status === "available" || status === "current") {
        expect(wrapper.get(".release-notes").text()).toContain("<img")
        await download.trigger("click")
        expect(request.mock.calls).toContainEqual(["updates.open", { source: "github", version: "0.2.0", target: "without-dotnet" }])
    } else expect(download.attributes("disabled")).toBeDefined()
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
    expect(wrapper.get('[data-target="without-dotnet"]').text()).toBe("下载 ZIP 安装包")
    expect(wrapper.find(".update-notes").attributes("open")).toBeUndefined()
    const requestsBefore = request.mock.calls.length
    setLocale("en-us")
    await flushPromises()
    expect(time.text()).toBe(expected)
    expect(wrapper.get(".update-details dt").text()).toBe("Last checked")
    expect(wrapper.get('[data-target="without-dotnet"]').text()).toBe("Download ZIP package")
    expect(request).toHaveBeenCalledTimes(requestsBefore)
    expect(check.checkedAtUtc).toBe(timestamp)
    wrapper.unmount()
    bridge.dispose()
})

it("本次发布缺少不含 .NET 的 ZIP 安装包时只启用发布页面, 不回退其他附件", async () => {
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
    const check = result()
    // 模拟旧缓存或异常宿主送入的其他模式, 前端必须仍按当前精确目标判断可用性.
    check.assets = [{ ...check.assets[0]!, dotNetMode: "with-dotnet" } as unknown as ProductUpdateCheck["assets"][number]]
    vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "fresh", promise: Promise.resolve({ checkedNow: true, check }) })
    await updates.check(false)
    const wrapper = mount(SoftwareUpdatePanel, { props: { bridge, updates } })
    expect(wrapper.get('[data-target="without-dotnet"]').attributes("disabled")).toBeDefined()
    expect(wrapper.get('[data-target="without-dotnet"]').attributes("title")).toBe("此发布未提供 ZIP 安装包")
    expect(wrapper.get('[data-target="release"]').attributes("disabled")).toBeUndefined()
    expect(wrapper.find('[data-target="with-dotnet"]').exists()).toBe(false)
    await expect(bridge.request("updates.open", { source: "github", version: "0.2.0", target: "with-dotnet" } as never).promise).rejects.toMatchObject({
        code: "VALIDATION_ERROR",
    })
    wrapper.unmount()
    scope.stop()
    bridge.dispose()
})
