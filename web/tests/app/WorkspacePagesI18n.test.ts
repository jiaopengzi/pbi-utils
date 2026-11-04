/**
 * FilePath    : pbi-utils\web\tests\app\WorkspacePagesI18n.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证连接中心英文显示时保留原始业务值.
 */
import { expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import { setLocale } from "@/i18n"
import ConnectionPanel from "@/app/ConnectionPanel.vue"
import { expectConsoleWarnings } from "../support/testDiagnostics"

it("连接中心英文邮箱校验和阶段提示使用同一连接参数, 不改写账号邮箱", async () => {
    setLocale("en-us")
    const bridge = markRaw(new BridgeClient(new MockTransport()))
    const request = vi.spyOn(bridge, "request")
    const wrapper = mount(ConnectionPanel, { props: { bridge }, attachTo: document.body })
    const popup = new DOMWrapper(document.body)
    await flushPromises()
    expect(popup.text()).toContain("Select PBIP / PBIX file")
    await popup
        .findAll('[role="tab"]')
        .find((tab) => tab.text() === "Power BI Service")!
        .trigger("click")
    await flushPromises()
    const email = popup.get('input[aria-label="Work or school email"]')
    await expectConsoleWarnings([["async-validator:", ["email is not a valid email"]]], async () => {
        await email.setValue("invalid")
        await popup
            .findAll("button")
            .find((button) => button.text() === "Sign in")!
            .trigger("click")
        await flushPromises()
        await vi.waitFor(() => expect(popup.text()).toContain("Enter a valid email address"))
    })
    expect(request.mock.calls.map(([method]) => method)).not.toContain("powerbi.serviceSignIn")
    await email.setValue("test@example.com")
    await popup
        .findAll("button")
        .find((button) => button.text() === "Sign in")!
        .trigger("click")
    await flushPromises()
    expect(request).toHaveBeenLastCalledWith(
        "powerbi.serviceSignIn",
        { email: "test@example.com", environmentName: "", remember: true },
        undefined,
        expect.any(Function),
    )
    expect(popup.text()).toContain("Multiple environments are available")
    expect((email.element as HTMLInputElement).value).toBe("test@example.com")
    wrapper.unmount()
    bridge.dispose()
})
