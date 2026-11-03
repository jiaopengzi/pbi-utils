/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\WorkdayImportDialog.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证导入只用宿主内容解析, 冲突默认保留, 取消和行错误不改变原草稿.
 */
import { expect, it, vi } from "vitest"
import { mount, flushPromises } from "@vue/test-utils"
import WorkdayImportDialog from "@/features/scaffold/WorkdayImportDialog.vue"
import { ElCheckbox } from "element-plus"
/** 使用轻量插槽外壳测试内容, 不将转场/焦点计时误判为业务结果. */
function harness(fail = false) {
    const original = [{ date: "2025-01-01", isWorkday: false, name: "原有" }]
    const call = fail
        ? vi.fn().mockRejectedValue(new Error("第 3 行: 日期无效"))
        : vi.fn().mockResolvedValue({
              overrides: [
                  { date: "2025-01-01", isWorkday: true, name: "新" },
                  { date: "2025-01-02", isWorkday: true, name: "新增" },
              ],
          })
    const wrapper = mount(WorkdayImportDialog, {
        props: {
            modelValue: true,
            bridge: { request: (...args: unknown[]) => ({ promise: call(...args) }) } as never,
            overrides: original,
            startYear: 2025,
            endYear: 2025,
        },
        global: { stubs: { UiDialog: { template: '<div><slot/><slot name="footer"/></div>' } } },
    })
    return { wrapper, original, call }
}
it("预览新增冲突且默认保留, 取消不发apply", async () => {
    const { wrapper, original, call } = harness()
    await wrapper.find("textarea").setValue("date,isWorkday,name\n2025-01-01,true,新")
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "解析并预览")!
        .trigger("click")
    await flushPromises()
    expect(call).toHaveBeenCalledWith("calendar.parseWorkdays", expect.objectContaining({ delimiter: "csv", startYear: 2025 }))
    expect(wrapper.text()).toContain("新增 1 · 相同 0 · 冲突 1")
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "取消")!
        .trigger("click")
    expect(wrapper.emitted("apply")).toBeUndefined()
    expect(original[0]?.name).toBe("原有")
    wrapper.unmount()
})
it.each([false, true])("确认仅合并草稿且显式替换=%s", async (replace) => {
    const { wrapper, original } = harness()
    await wrapper.find("textarea").setValue("date,isWorkday,name")
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "解析并预览")!
        .trigger("click")
    await flushPromises()
    if (replace) wrapper.findComponent(ElCheckbox).vm.$emit("update:modelValue", true)
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "确认导入草稿")!
        .trigger("click")
    expect(wrapper.emitted("apply")?.[0]?.[0]).toEqual([
        { date: "2025-01-01", isWorkday: replace, name: replace ? "新" : "原有" },
        { date: "2025-01-02", isWorkday: true, name: "新增" },
    ])
    expect(original).toHaveLength(1)
    wrapper.unmount()
})
it("宿主报原始行错误时整批拒绝并可见说明", async () => {
    const { wrapper, original } = harness(true)
    await wrapper.find("textarea").setValue("bad")
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "解析并预览")!
        .trigger("click")
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain("第 3 行")
    expect(wrapper.emitted("apply")).toBeUndefined()
    expect(original).toHaveLength(1)
    wrapper.unmount()
})
it("2MiB按UTF8计算且超限不发送宿主请求, 原草稿保持", async () => {
    const { wrapper, original, call } = harness()
    await wrapper.find("textarea").setValue("中".repeat(700000))
    await wrapper
        .findAll("button")
        .find((button) => button.text() === "解析并预览")!
        .trigger("click")
    await flushPromises()
    expect(call).not.toHaveBeenCalled()
    expect(wrapper.find('[role="alert"]').text()).toContain("2 MiB UTF-8")
    expect(original).toHaveLength(1)
    wrapper.unmount()
})
