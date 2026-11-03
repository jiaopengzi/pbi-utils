/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\udfLocalPackageManager.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 真实桥接下本地管理器按名合并和tag切换后的精确编辑删除.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { markRaw } from "vue"
import { ElMessage, ElSelect } from "element-plus"
import { BridgeClient } from "@/bridge/client"
import { MockTransport } from "@/bridge/mock"
import CodeEditor from "@/features/code/CodeEditor.vue"
import UdfLocalBrowser from "@/features/scaffold/UdfLocalBrowser.vue"
import type { UdfSelection } from "@/features/scaffold/contracts"
import UdfStep from "@/features/scaffold/UdfStep.vue"
import UdfLocalManager from "@/features/scaffold/UdfLocalManager.vue"
import UdfFunctionRow from "@/features/scaffold/UdfFunctionRow.vue"
const cleanup: (() => void)[] = []
afterEach(() => {
    cleanup.splice(0).forEach((close) => close())
    vi.restoreAllMocks()
    ElMessage.closeAll()
})
/** 从真实模态查找当前操作按钮. */
function dialogButton(label: string) {
    return new DOMWrapper([...document.querySelectorAll<HTMLButtonElement>(".el-dialog button")].findLast((item) => item.textContent?.trim() === label)!)
}
/** 同名多tag通过宿主保存获得各自ID与基线. */
async function fixture(selected = false) {
    const bridge = markRaw(new BridgeClient(new MockTransport())),
        request = vi.spyOn(bridge, "request")
    const entries = await Promise.all(
        ["1.9.0", "1.10.0", "2.0.0-beta"].map(
            (version) =>
                bridge.request("udf.localSave", {
                    definition: { schemaVersion: 1, name: "Local.Managed", version, expression: "() => 1", description: "", dependencies: [] },
                }).promise,
        ),
    )
    const wrapper = mount(UdfStep, {
        attachTo: document.body,
        props: {
            bridge,
            disabled: false,
            modelValue: selected
                ? [
                      {
                          source: "local",
                          packageId: entries[1]!.id,
                          version: entries[1]!.definition.version,
                          functions: [entries[1]!.definition.name],
                          baseline: entries[1]!.baseline,
                      },
                  ]
                : ([] as UdfSelection[]),
            "onUpdate:modelValue": (value: UdfSelection[]) => {
                void wrapper.setProps({ modelValue: value })
            },
        },
    })
    cleanup.push(() => {
        wrapper.unmount()
        bridge.dispose()
    })
    await flushPromises()
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "管理本地函数")!
        .trigger("click")
    await flushPromises()
    return { bridge, request, wrapper, entries, manager: wrapper.findComponent(UdfLocalManager) }
}
it("管理器同名一行, tag切换后编辑与删除使用当前GUID, 删除当前tag后保留存活包", async () => {
    const { bridge, request, manager, entries } = await fixture()
    expect(manager.findAllComponents(UdfFunctionRow)).toHaveLength(1)
    const row = manager.findComponent(UdfFunctionRow),
        picker = row.findComponent(ElSelect)
    expect(picker.props("modelValue")).toBe(entries[1]!.id)
    await picker.setValue(entries[0]!.id)
    await flushPromises()
    expect(picker.props("modelValue")).toBe(entries[0]!.id)
    await row
        .findAll("button")
        .find((button) => button.text() === "编辑")!
        .trigger("click")
    await flushPromises()
    expect(request.mock.calls.findLast(([method]) => method === "udf.localGet")?.[1]).toEqual({ id: entries[0]!.id })
    await dialogButton("取消").trigger("click")
    await flushPromises()
    await row
        .findAll("button")
        .find((button) => button.text() === "删除")!
        .trigger("click")
    await flushPromises()
    await dialogButton("确认删除").trigger("click")
    await flushPromises()
    expect(request.mock.calls.findLast(([method]) => method === "udf.localDelete")?.[1]).toEqual({
        id: entries[0]!.id,
        baseline: entries[0]!.baseline,
        confirm: true,
    })
    expect((await bridge.request("udf.localList", {}).promise).items).toHaveLength(2)
    expect(manager.findAllComponents(UdfFunctionRow)).toHaveLength(1)
    expect(manager.findComponent(UdfFunctionRow).findComponent(ElSelect).props("modelValue")).toBe(entries[1]!.id)
})

it("保存同名新tag定位展示但保留旧已选版本, 明确勾选新tag才原子替换", async () => {
    const { bridge, wrapper, manager, entries } = await fixture(true)
    const original = JSON.parse(JSON.stringify(wrapper.props("modelValue"))) as UdfSelection[]
    await dialogButton("新建本地函数").trigger("click")
    await flushPromises()
    const inputs = [...document.querySelectorAll<HTMLInputElement>(".el-dialog .scaffold-form .el-input__inner")]
    await new DOMWrapper(inputs[0]!).setValue("Local.Managed")
    await new DOMWrapper(inputs[1]!).setValue("0.8.0")
    await wrapper
        .findAllComponents(CodeEditor)
        .find((editor) => editor.props("language") === "dax")!
        .setValue("() => 8")
    await dialogButton("保存").trigger("click")
    await flushPromises()
    const saved = (await bridge.request("udf.localList", {}).promise).items.find((item) => item.definition.version === "0.8.0")!
    expect(manager.findComponent(UdfFunctionRow).findComponent(ElSelect).props("modelValue")).toBe(saved.id)
    const browserRow = wrapper.findComponent(UdfLocalBrowser).findComponent(UdfFunctionRow)
    expect(browserRow.props("entry").id).toBe(saved.id)
    expect(browserRow.props("checked")).toBe(false)
    expect(wrapper.props("modelValue")).toEqual(original)
    expect(wrapper.find(".udf-selected").text()).toContain(entries[1]!.definition.version)
    await new DOMWrapper(document.querySelector<HTMLButtonElement>('[role="dialog"][aria-label="管理本地函数"] .el-dialog__headerbtn')!).trigger("click")
    await flushPromises()
    await browserRow.find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.props("modelValue")).toHaveLength(1)
    expect(wrapper.props("modelValue")[0]).toMatchObject({ packageId: saved.id, version: "0.8.0", baseline: saved.baseline })
})

it("本地描述使用 Markdown 编辑器, 保存与重开保留多行原文, 取消不覆盖已存内容", async () => {
    const { bridge, wrapper, manager } = await fixture()
    await dialogButton("新建本地函数").trigger("click")
    await flushPromises()
    const inputs = [...document.querySelectorAll<HTMLInputElement>(".el-dialog .scaffold-form .el-input__inner")]
    await new DOMWrapper(inputs[0]!).setValue("Local.Markdown")
    const description = "# 函数说明\r\n\r\n**用途** 与 `代码`\r\n- 第一项\r\n- 第二项\r\n\r\n```dax\r\nRETURN 1\r\n```"
    const markdown = wrapper.findAllComponents(CodeEditor).find((editor) => editor.props("language") === "markdown")
    expect(markdown).toBeDefined()
    expect(markdown!.props()).toMatchObject({ label: "描述", maxlength: 8192, disabled: false })
    await markdown!.setValue(description)
    await wrapper
        .findAllComponents(CodeEditor)
        .find((editor) => editor.props("language") === "dax")!
        .setValue("() => 1")
    await dialogButton("保存").trigger("click")
    await flushPromises()
    const saved = (await bridge.request("udf.localList", {}).promise).items.find((item) => item.definition.name === "Local.Markdown")!
    expect(saved.definition.description).toBe(description)
    const row = manager.findAllComponents(UdfFunctionRow).find((item) => item.props("entry").id === saved.id)!
    await row
        .findAll("button")
        .find((button) => button.text() === "编辑")!
        .trigger("click")
    await flushPromises()
    const reopened = wrapper.findAllComponents(CodeEditor).find((editor) => editor.props("language") === "markdown")!
    expect(reopened.props("modelValue")).toBe(description)
    await reopened.setValue("# 未保存的修改")
    await dialogButton("取消").trigger("click")
    await flushPromises()
    expect((await bridge.request("udf.localGet", { id: saved.id }).promise).definition.description).toBe(description)
})
