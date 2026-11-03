/**
 * FilePath    : pbi-utils\web\tests\app\SettingsWorkspace.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证设置迁移、统一语言保存及失败恢复, 不把模拟偏好标为桌面保存.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient, BridgeError } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setUiSettings } from "@/i18n"
import { useI18n } from "@/composables/i18n/useI18n"
import SettingsWorkspace from "@/app/SettingsWorkspace.vue"
import UiSettingsPanel from "@/app/UiSettingsPanel.vue"
import LoggingPanel from "@/app/LoggingPanel.vue"
import McpSettingsPanel from "@/app/McpSettingsPanel.vue"
import DataResetPanel from "@/app/DataResetPanel.vue"
import { showMessage } from "@/ui/message"

vi.mock("@/ui/message", () => ({ showMessage: vi.fn() }))

it("两个设置窗口读取旧快照后修改不同字段, 各次只提交实际修改并保留另一个窗口的新偏好", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const first = mount(UiSettingsPanel, { props: { bridge } })
    const second = mount(UiSettingsPanel, { props: { bridge } })
    await flushPromises()
    const request = vi.spyOn(bridge, "request")
    const secondStyle = second.findAllComponents({ name: "ElSwitch" })[1]!
    secondStyle.vm.$emit("update:modelValue", "short")
    secondStyle.vm.$emit("change", "short")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { daxFormatStyle: "short" })
    expect(first.findAllComponents({ name: "ElSwitch" })[1]!.props("modelValue")).toBe("long")
    const firstRecent = first.findAllComponents({ name: "ElSwitch" })[0]!
    firstRecent.vm.$emit("update:modelValue", false)
    firstRecent.vm.$emit("change", false)
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { showRecentFiles: false })
    expect(useI18n().daxFormatStyle.value).toBe("short")
    expect(useI18n().showRecentFiles.value).toBe(false)
    expect(first.findAllComponents({ name: "ElSwitch" })[1]!.props("modelValue")).toBe("short")
    const secondLanguage = second.findComponent({ name: "ElSelect" })
    secondLanguage.vm.$emit("update:modelValue", "en-us")
    secondLanguage.vm.$emit("change", "en-us")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { locale: "en-us" })
    expect(await bridge.request("ui.settings", {}).promise).toMatchObject({ locale: "en-us", showRecentFiles: false, daxFormatStyle: "short" })
    first.unmount()
    second.unmount()
    bridge.dispose()
})

it("DAX 格式在设置保存成功后共享生效, 重开设置和切语言保留所选布局", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    let wrapper = mount(UiSettingsPanel, { props: { bridge } })
    await flushPromises()
    const style = wrapper.findAllComponents({ name: "ElSwitch" })[1]!
    expect(style.props("modelValue")).toBe("long")
    expect(style.props("activeValue")).toBe("long")
    expect(style.props("inactiveValue")).toBe("short")
    style.vm.$emit("update:modelValue", "short")
    style.vm.$emit("change", "short")
    expect(useI18n().daxFormatStyle.value).toBe("long")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { daxFormatStyle: "short" })
    expect(useI18n().daxFormatStyle.value).toBe("short")
    const language = wrapper.findComponent({ name: "ElSelect" })
    language.vm.$emit("update:modelValue", "en-us")
    language.vm.$emit("change", "en-us")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { locale: "en-us" })
    expect(wrapper.get('label[for="ui-dax-format-style"]').text()).toBe("DAX format")
    wrapper.unmount()
    wrapper = mount(UiSettingsPanel, { props: { bridge } })
    await flushPromises()
    expect(wrapper.findAllComponents({ name: "ElSwitch" })[1]!.props("modelValue")).toBe("short")
    expect(request.mock.calls.map(([method]) => method)).not.toContain("dax.format")
    wrapper.unmount()
    bridge.dispose()
})

it("DAX 格式保存失败恢复已生效布局, 不改变语言或最近文件偏好", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(UiSettingsPanel, { props: { bridge } })
    await flushPromises()
    const style = wrapper.findAllComponents({ name: "ElSwitch" })[1]!
    vi.spyOn(bridge, "request").mockReturnValueOnce({
        id: "failed-style",
        promise: Promise.reject(new BridgeError("SAVE_FAILED", "fixture style save failure")),
    })
    style.vm.$emit("update:modelValue", "short")
    style.vm.$emit("change", "short")
    await flushPromises()
    expect(style.props("modelValue")).toBe("long")
    expect(useI18n().daxFormatStyle.value).toBe("long")
    expect(useI18n().locale.value).toBe("zh-cn")
    expect(useI18n().showRecentFiles.value).toBe(true)
    expect(showMessage).toHaveBeenCalledWith("fixture style save failure", "error")
    wrapper.unmount()
    bridge.dispose()
})

it("最近文件开关独立保存并与语言共用同一偏好, 切换语言不会重新开启已关闭的连接页模块", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(UiSettingsPanel, { props: { bridge } })
    await flushPromises()
    const toggle = wrapper.findComponent({ name: "ElSwitch" })
    expect(toggle.props("modelValue")).toBe(true)
    toggle.vm.$emit("update:modelValue", false)
    toggle.vm.$emit("change", false)
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { showRecentFiles: false })
    expect(useI18n().showRecentFiles.value).toBe(false)
    const language = wrapper.findComponent({ name: "ElSelect" })
    language.vm.$emit("update:modelValue", "en-us")
    language.vm.$emit("change", "en-us")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { locale: "en-us" })
    expect(wrapper.text()).toContain("Show recently opened files")
    expect(toggle.props("modelValue")).toBe(false)
    expect(request.mock.calls.map(([method]) => method)).not.toContain("model.updateSaveSettings")
    wrapper.unmount()
    bridge.dispose()
})

it("失败的偏好请求恢复最新共享状态, 不使用面板旧快照覆盖其他入口已生效的值", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(UiSettingsPanel, { props: { bridge } })
    await flushPromises()
    const formNode = wrapper.get("form").element
    const selectNode = wrapper.findComponent({ name: "ElSelect" }).element
    let reject!: (reason: Error) => void
    vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "pending-ui", promise: new Promise((_, fail) => (reject = fail)) })
    const toggle = wrapper.findComponent({ name: "ElSwitch" })
    toggle.vm.$emit("update:modelValue", false)
    toggle.vm.$emit("change", false)
    await flushPromises()
    expect(toggle.props("disabled")).toBe(true)
    expect(wrapper.get("form").element).toBe(formNode)
    expect(wrapper.findComponent({ name: "ElSelect" }).element).toBe(selectNode)
    expect(wrapper.text()).not.toContain("正在保存")
    expect(useI18n().showRecentFiles.value).toBe(true)
    setUiSettings({ locale: "en-us", showRecentFiles: false, daxFormatStyle: "long" })
    reject(new BridgeError("SAVE_FAILED", "fixture save failure"))
    await flushPromises()
    expect(useI18n().locale.value).toBe("en-us")
    expect(useI18n().showRecentFiles.value).toBe(false)
    expect(wrapper.findComponent({ name: "ElSelect" }).props("modelValue")).toBe("en-us")
    expect(toggle.props("modelValue")).toBe(false)
    expect(wrapper.get("form").element).toBe(formNode)
    wrapper.unmount()
    bridge.dispose()
})

it("设置移除产品介绍并保留运行环境检测, MCP 在顶部且本地数据在底部", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(SettingsWorkspace, { props: { bridge } })
    await flushPromises()
    expect(wrapper.findComponent(UiSettingsPanel).exists()).toBe(true)
    expect(wrapper.findComponent(LoggingPanel).exists()).toBe(true)
    expect(wrapper.findComponent(McpSettingsPanel).exists()).toBe(true)
    expect(wrapper.findComponent(DataResetPanel).exists()).toBe(true)
    expect(wrapper.find(".about-panel").exists()).toBe(false)
    expect(wrapper.find(".environment-panel").exists()).toBe(true)
    expect(wrapper.findAll(".environment-descriptions .el-descriptions__label")).toHaveLength(7)
    expect(wrapper.text()).not.toContain("C# / .NET 10 · Vue 3 · WinForms / WebView2")
    expect(wrapper.findAll("h2").map((heading) => heading.text())).not.toContain("关于")
    expect(wrapper.text()).not.toContain("模型保存")
    expect(wrapper.text()).not.toContain("AI 提供方与模型")
    const sections = wrapper.findAll(".settings-workspace > section")
    expect(sections.map((section) => section.attributes("aria-label"))).toEqual(["本地 MCP", "常规", "软件更新", "运行环境", "本地数据"])
    expect(request.mock.calls.map(([method]) => method)).toEqual([
        "updates.status",
        "app.info",
        "mcp.connections",
        "mcp.configuration",
        "ui.settings",
        "logging.settings",
        "doctor",
    ])
    wrapper.unmount()
    bridge.dispose()
})

it("不同设置面板同时在途时保持全局忙状态, 最后一个面板结束才解除导航锁", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(SettingsWorkspace, { props: { bridge } })
    await flushPromises()
    const language = wrapper.findComponent(UiSettingsPanel)
    const mcp = wrapper.findComponent(McpSettingsPanel)
    language.vm.$emit("busy", true)
    mcp.vm.$emit("busy", true)
    await flushPromises()
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
    language.vm.$emit("busy", false)
    await flushPromises()
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([true])
    mcp.vm.$emit("busy", false)
    await flushPromises()
    expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    wrapper.unmount()
    bridge.dispose()
})

it("重复搜索 MCP 分区避让顶栏, 保留当前配置示例且不重新加载", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const header = document.createElement("header")
    header.className = "workspace-header"
    vi.spyOn(header, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 1000, 132))
    document.body.append(header)
    const wrapper = mount(SettingsWorkspace, { props: { bridge } })
    try {
        await flushPromises()
        const panel = wrapper.getComponent(McpSettingsPanel)
        expect(panel.findAll('[role="tab"]')).toHaveLength(0)
        const section = wrapper.findAll(".settings-workspace > section")[0]!.element as HTMLElement
        const scroll = vi.fn(() => section.style.scrollMarginBlockStart)
        Object.defineProperty(section, "scrollIntoView", { configurable: true, value: scroll })
        const requestCount = request.mock.calls.length
        const node = panel.element
        await wrapper.setProps({ initialTab: "mcp", navigationId: 1 })
        await flushPromises()
        expect(scroll).toHaveBeenCalledTimes(1)
        expect(scroll.mock.results[0]?.value).toBe("150px")
        await wrapper.setProps({ navigationId: 2 })
        await flushPromises()
        expect(scroll).toHaveBeenCalledTimes(2)
        expect(wrapper.getComponent(McpSettingsPanel).element).toBe(node)
        expect(panel.get("pre").text()).toContain("--allow-write")
        expect(request).toHaveBeenCalledTimes(requestCount)
    } finally {
        wrapper.unmount()
        header.remove()
        bridge.dispose()
    }
})

it("语言成功保存后即时切换同一设置工作区, 只传白名单语言并保留原始日志级别", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(SettingsWorkspace, { props: { bridge } })
    await flushPromises()
    const panel = wrapper.findComponent(UiSettingsPanel)
    const language = panel.findComponent({ name: "ElSelect" })
    language.vm.$emit("update:modelValue", "en-us")
    language.vm.$emit("change", "en-us")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith("ui.saveSettings", { locale: "en-us" })
    expect(useI18n().locale.value).toBe("en-us")
    expect(document.documentElement.lang).toBe("en-us")
    expect(wrapper.text()).toContain("Business logs")
    expect(wrapper.text()).not.toContain("Model saving")
    expect(showMessage).toHaveBeenLastCalledWith(expect.stringContaining("Simulated interface settings updated"), "info")
    expect(wrapper.text()).not.toContain("Simulated interface settings updated")
    expect(wrapper.text()).not.toContain("语言设置已保存")
    expect(wrapper.findComponent(LoggingPanel).findComponent({ name: "ElSelect" }).props("modelValue")).toBe("Information")
    expect(request.mock.calls.map(([method]) => method)).not.toContain("logging.save")
    wrapper.unmount()
    bridge.dispose()
})

it("语言保存失败恢复已生效选择并保持全局语言, 取消退出只针对未完成的偏好请求", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const wrapper = mount(UiSettingsPanel, { props: { bridge } })
    await flushPromises()
    const request = vi.spyOn(bridge, "request")
    request.mockReturnValueOnce({ id: "failed-language", promise: Promise.reject(new BridgeError("SAVE_FAILED", "保存失败，原语言保留")) })
    const select = wrapper.findComponent({ name: "ElSelect" })
    select.vm.$emit("update:modelValue", "en-us")
    select.vm.$emit("change", "en-us")
    await flushPromises()
    expect(select.props("modelValue")).toBe("zh-cn")
    expect(useI18n().locale.value).toBe("zh-cn")
    expect(showMessage).toHaveBeenCalledWith(expect.stringContaining("原语言保留"), "error")
    let finish: ((value: { locale: "en-us"; showRecentFiles: boolean; daxFormatStyle: "long"; warning: null; isSimulation: boolean }) => void) | undefined
    request.mockReturnValueOnce({ id: "pending-language", promise: new Promise((resolve) => (finish = resolve)) })
    select.vm.$emit("update:modelValue", "en-us")
    select.vm.$emit("change", "en-us")
    await flushPromises()
    expect(select.props("disabled")).toBe(true)
    wrapper.unmount()
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "pending-language" })
    finish?.({ locale: "en-us", showRecentFiles: false, daxFormatStyle: "long", warning: null, isSimulation: true })
    await flushPromises()
    expect(useI18n().locale.value).toBe("zh-cn")
    expect(useI18n().showRecentFiles.value).toBe(true)
    bridge.dispose()
})
