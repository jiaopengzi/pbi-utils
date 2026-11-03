/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\UdfFunctionDetails.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证包详情横向元数据、完整文档、去重复展示及批准和关闭边界.
 */
import { afterEach, expect, it, vi } from "vitest"
import { DOMWrapper, flushPromises, mount } from "@vue/test-utils"
import { ElDialog } from "element-plus"
import UiDialog from "@/ui/UiDialog.vue"
import UdfPackageDetailDialog from "@/features/scaffold/UdfPackageDetailDialog.vue"
import type { UdfPackage } from "@/features/scaffold/contracts"

const cleanup: (() => void)[] = []
afterEach(() => cleanup.splice(0).forEach((close) => close()))

/** 手工定义独立详情事实, 不读取用户模型、磁盘或网络, 内容包含真实 Markdown 结构和原始标签文本. */
function details(): UdfPackage {
    return {
        id: "PQL.Assert",
        version: "0.7.1",
        hash: "a".repeat(64),
        description: "A library for **model testing**.",
        readme: "# PQL.Assert\n\n## Getting Started\n\n1. Load `functions.tmdl`\n2. Start writing tests\n\n```dax\nEVALUATE { 1 }\n```\n\n| Name | Type |\n| --- | --- |\n| Passed | Boolean |",
        metadata: {
            authors: "John Kerski",
            tags: ["Testing", "Validation"],
            releaseNotes: "## Changes\n\n- Updated assertions",
            projectUrl: null,
            repositoryUrl: null,
        },
        dependencies: [{ id: "Helper", version: "1.0.0" }],
        licenseFiles: { LICENSE: "Copyright original author\n\nPermission granted.\n<script>plain text</script>" },
        functions: [
            {
                name: "PQL.Assert.BP.CheckDAXExpressions",
                expression: "() =>\n    UNION(\n        First(),\n        Second()\n    )",
                description: "Runs **DAX expression** checks.",
                annotations: {},
            },
            { name: "PQL.Assert.Second", expression: "() => TRUE()", description: "Second assertion", annotations: {} },
        ],
        retrievedAt: "2026-10-24T00:00:00Z",
        fromCache: true,
        committedAt: null,
    }
}

it("共享弹窗保留默认宽度, 调用方可显式指定适应视口的文档宽度", async () => {
    const wrapper = mount(UiDialog, { props: { modelValue: true, title: "Details" } })
    cleanup.push(() => wrapper.unmount())
    expect(wrapper.findComponent(ElDialog).props("width")).toBe("min(640px, calc(100vw - 32px))")
    await wrapper.setProps({ width: "min(1000px, calc(100vw - 32px))" })
    expect(wrapper.findComponent(ElDialog).props("width")).toBe("min(1000px, calc(100vw - 32px))")
})

it("包详情使用1200px自适应弹窗, 优先README且展示全部函数且不再显示预发行许可开关", async () => {
    const value = details()
    const wrapper = mount(UdfPackageDetailDialog, { props: { modelValue: true, details: value }, attachTo: document.body })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    expect(wrapper.findComponent(ElDialog).props("width")).toBe("min(1200px, calc(100vw - 32px))")
    const dialog = document.querySelector<HTMLElement>(".udf-detail-dialog")!
    expect(dialog.querySelector(".el-dialog__footer")).toBeNull()
    expect(dialog.textContent).toContain(value.id)
    expect(dialog.querySelector("h1")?.textContent).toBe("PQL.Assert")
    expect(dialog.querySelector("ol")?.children).toHaveLength(2)
    expect(dialog.querySelector("table td")?.textContent).toBe("Passed")
    expect([...dialog.querySelectorAll(".code-block code")].map((item) => item.textContent)).toEqual(value.functions.map((item) => item.expression))
    expect(dialog.textContent).toContain("PQL.Assert.Second")
    expect(dialog.querySelector(".udf-detail-document")?.textContent).not.toContain("A library for model testing.")
    expect(dialog.textContent).toContain("John Kerski")
    expect(dialog.querySelector("header .udf-detail-metadata")?.textContent).toContain("John Kerski")
    expect(dialog.querySelector("aside")).toBeNull()
    expect(dialog.textContent).not.toContain("Helper @ 1.0.0")
    expect(dialog.textContent).not.toContain(value.licenseFiles.LICENSE)
    expect(dialog.textContent).not.toContain("Updated assertions")
    expect(dialog.querySelectorAll(".udf-detail-section > summary")).toHaveLength(0)
    expect(value.dependencies).toEqual([{ id: "Helper", version: "1.0.0" }])
    expect(value.licenseFiles.LICENSE).toContain("Copyright original author")
    expect(dialog.querySelector("script")).toBeNull()
    expect(dialog.querySelector(".udf-detail-document img")).toBeNull()
    expect(wrapper.emitted("approval")).toBeUndefined()
    expect(dialog.querySelector(".el-checkbox")).toBeNull()
    expect(value.functions[0]!.expression).toBe("() =>\n    UNION(\n        First(),\n        Second()\n    )")
})

it("没有README时渲染包说明, 右上角关闭详情卸载全部函数代码", async () => {
    const value = { ...details(), readme: "" }
    const wrapper = mount(UdfPackageDetailDialog, {
        props: { modelValue: true, details: value },
        attachTo: document.body,
        global: { stubs: { transition: false } },
    })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    const documentSection = document.querySelector(".udf-detail-document")!
    expect(documentSection.querySelector("strong")?.textContent).toBe("model testing")
    await new DOMWrapper(document.querySelector<HTMLButtonElement>(".udf-detail-dialog .el-dialog__headerbtn")!).trigger("click")
    // Element Plus 在离场完成后回传关闭, 使用真实过渡等待该事件, 不以默认过渡桩推断完成.
    await vi.waitFor(() => expect(wrapper.emitted("update:modelValue")).toEqual([[false]]))
    await wrapper.setProps({ modelValue: false })
    expect(wrapper.findAllComponents({ name: "CodeBlock" })).toHaveLength(0)
})

it("精确版本最后修改提交的提交者时间按本机时区显示完整日期时间, 不借用缓存获取时间", async () => {
    const value = { ...details(), committedAt: "2026-10-20T03:28:40.731292+08:00" }
    const wrapper = mount(UdfPackageDetailDialog, { props: { modelValue: true, details: value }, attachTo: document.body })
    cleanup.push(() => wrapper.unmount())
    await flushPromises()
    const formatter = new Intl.DateTimeFormat("sv-SE", {
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
        hourCycle: "h23",
    })
    const expected = formatter.format(new Date(value.committedAt))
    const dialog = document.querySelector<HTMLElement>(".udf-detail-dialog")!
    expect(dialog.textContent).toContain(expected)
    expect(dialog.textContent).not.toContain(formatter.format(new Date(value.retrievedAt)))
})
