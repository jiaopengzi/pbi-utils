/**
 * FilePath    : pbi-utils\web\tests\app\McpSettingsPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证 MCP 统一结果表提示词的双语复制、真实路径与权限, 以及失败与卸载隔离.
 */
import { expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { marked } from "marked"
import { BridgeClient, BridgeError, type McpConfiguration } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale } from "@/i18n"
import McpSettingsPanel from "@/app/McpSettingsPanel.vue"

/** 构造包含空格和非 ASCII 字符的宿主路径, 检查完整 JSON 转义及启动参数保持. */
function configuration(): McpConfiguration {
    const command = "C:\\用户目录\\Power BI tools\\pbi-utils.exe"
    return {
        executablePath: command,
        readWrite: { command, args: ["mcp", "serve", "--allow-write"] },
        isSimulation: false,
    }
}

it("双语提示词复制完整配置和七项结果表, 切换语言不启动服务", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const value = configuration()
    const request = vi.spyOn(bridge, "request").mockReturnValue({ id: "configuration", promise: Promise.resolve(value) })
    const copy = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue(undefined)
    const wrapper = mount(McpSettingsPanel, { props: { bridge }, global: { stubs: { McpConnectionsPanel: true } } })
    try {
        await flushPromises()
        /** 切换指定语言并核验实际复制结果; rows/unrun 为预期表格标签, 不匹配时由断言报告失败. */
        async function verifyCopy(locale: "zh-cn" | "en-us", rows: string[], unrun: string): Promise<void> {
            setLocale(locale)
            await flushPromises()
            await wrapper.get(".mcp-prompt button").trigger("click")
            const prompt = copy.mock.lastCall?.[0] ?? ""
            expect(prompt).toBe(wrapper.get(".mcp-prompt pre").text())
            expect(prompt).not.toMatch(/initialize|tools\/list|list_mcp_resources|15 行|15 lines/)
            expect(prompt).toMatch(/server_info \{\}[\s\S]*desktop_sessions \{\}[\s\S]*desktop_app_info/)

            // 核验真实剪贴板中的完整配置与有序参数, 不用逐句文案断言代替复制结果.
            const lines = prompt.split("\n")
            expect(JSON.parse(lines.slice(lines.lastIndexOf("{")).join("\n"))).toEqual({ mcpServers: { "pbi-utils": value.readWrite } })
            const table = lines.filter((line) => line.startsWith("|"))
            expect(table).toHaveLength(9)
            expect(table.slice(2).map((line) => line.split("|")[1]?.trim())).toEqual(rows)
            expect(table.at(-1)?.split("|")[2]?.trim()).toBe(unrun)
            expect(table[2]).toContain("readback=`<JSON>`")
            expect(table[2]).toContain("match=<true/false/unknown>")

            // Markdown 渲染后仍为七行三列, 行内 JSON 的 Windows 路径反斜杠可无损复制解析.
            const rendered = document.createElement("div")
            rendered.innerHTML = marked.parse(table.join("\n").replace("<JSON>", JSON.stringify(value.readWrite)), { async: false })
            expect(rendered.querySelectorAll("thead th")).toHaveLength(3)
            expect(rendered.querySelectorAll("tbody tr")).toHaveLength(7)
            for (const row of rendered.querySelectorAll("tbody tr")) expect(row.querySelectorAll("td")).toHaveLength(3)
            const evidence = rendered.querySelector("tbody tr td:nth-child(3) code")
            expect(evidence).not.toBeNull()
            expect(JSON.parse(evidence?.textContent ?? "")).toEqual(value.readWrite)
        }
        await verifyCopy("zh-cn", ["配置", "工具发现", "server_info", "desktop_sessions", "desktop_app_info", "写权限", "实际写入测试"], "未验证")
        await verifyCopy(
            "en-us",
            ["Configuration", "Tool discovery", "server_info", "desktop_sessions", "desktop_app_info", "Write permission", "Actual write test"],
            "UNRUN",
        )
        expect(wrapper.findAll("pre")).toHaveLength(1)
        expect(wrapper.findAll("button")).toHaveLength(1)
        expect(wrapper.findAll('[role="tab"]')).toHaveLength(0)
        expect(copy).toHaveBeenCalledTimes(2)
        expect(request.mock.calls).toEqual([["mcp.configuration", {}]])
    } finally {
        wrapper.unmount()
        bridge.dispose()
        setLocale("zh-cn")
    }
})

it("读取失败不显示配置, 重新读取成功后标明模拟状态", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "failed", promise: Promise.reject(new BridgeError("MCP_FAILED", "配置读取失败")) })
    const wrapper = mount(McpSettingsPanel, { props: { bridge }, global: { stubs: { McpConnectionsPanel: true } } })
    try {
        await flushPromises()
        expect(wrapper.find("pre").exists()).toBe(false)
        await wrapper.get("button").trigger("click")
        await flushPromises()
        expect(wrapper.text()).toContain("浏览器模拟路径")
        expect(wrapper.find("pre").exists()).toBe(true)
        expect(request.mock.calls).toEqual([
            ["mcp.configuration", {}],
            ["mcp.configuration", {}],
        ])
        expect(wrapper.emitted("busy")?.at(-1)).toEqual([false])
    } finally {
        wrapper.unmount()
        bridge.dispose()
    }
})

it("卸载取消当前读取并忽略迟到结果", async () => {
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    let finish: ((value: McpConfiguration) => void) | undefined
    const request = vi.spyOn(bridge, "request").mockReturnValueOnce({ id: "pending", promise: new Promise((resolve) => (finish = resolve)) })
    const wrapper = mount(McpSettingsPanel, { props: { bridge }, global: { stubs: { McpConnectionsPanel: true } } })
    await flushPromises()
    wrapper.unmount()
    expect(request).toHaveBeenLastCalledWith("request.cancel", { requestId: "pending" })
    const emitted = wrapper.emitted("busy")?.length
    finish?.(configuration())
    await flushPromises()
    expect(wrapper.emitted("busy")?.length).toBe(emitted)
    bridge.dispose()
})
