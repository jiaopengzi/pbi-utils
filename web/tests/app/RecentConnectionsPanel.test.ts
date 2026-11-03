/**
 * FilePath    : pbi-utils\web\tests\app\RecentConnectionsPanel.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证最近记录独立删除按钮、双语确认、取消和异步身份及操作锁边界.
 */
import { afterEach, expect, it, vi } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { defineComponent, h, ref } from "vue"
import { ElMessageBox, type MessageBoxData } from "element-plus"
import { setLocale } from "@/i18n"
import RecentConnectionsPanel from "@/app/RecentConnectionsPanel.vue"

const items = [
    { id: "record-1", name: "学习工程 <b>保留原文</b>", detail: "C:\\reports\\学习工程.pbip" },
    { id: "record-2", name: "另一个模型", detail: "工作区 / 模型" },
]
// confirm 实际解析为 Action; 锁定版本的上游交叉声明需在测试边界显式兼容.
const confirmed = "confirm" as MessageBoxData

/** 每个用例独立恢复确认框和共享语言, 避免模拟结果泄漏到其他页面测试. */
afterEach(() => {
    vi.restoreAllMocks()
    setLocale("zh-cn")
})

/** 由测试显式结束确认阶段, 用于复现等待期间的导航锁和列表变动. */
function pendingConfirmation() {
    let resolve!: (value: MessageBoxData) => void
    const promise = new Promise<MessageBoxData>((accept) => {
        resolve = accept
    })
    return { promise, resolve }
}

it.each(["zh-cn", "en-us"] as const)("%s 删除按钮独立可访问, 以记录原名确认后只发宿主标识", async (locale) => {
    setLocale(locale)
    const confirm = vi.spyOn(ElMessageBox, "confirm").mockResolvedValue(confirmed)
    const events: string[] = []
    const wrapper = mount(RecentConnectionsPanel, {
        props: {
            items,
            onConfirming: (value: boolean) => events.push(`confirming:${value}`),
            onRemove: (id: string) => events.push(`remove:${id}`),
        },
    })
    try {
        expect(wrapper.findAll(".recent-project-row")).toHaveLength(2)
        expect(wrapper.find("button button").exists()).toBe(false)
        expect(wrapper.get(".recent-project-remove").attributes("aria-label")).toBe(
            locale === "zh-cn" ? `移除最近记录: ${items[0]!.name}` : `Remove recent record: ${items[0]!.name}`,
        )
        expect(wrapper.get(".recent-project-remove img").attributes("src")).toMatch(/icons\/trash-2\.svg$/)
        await wrapper.get(".recent-project-remove").trigger("click")
        await flushPromises()
        expect(confirm).toHaveBeenCalledWith(
            locale === "zh-cn"
                ? `将“${items[0]!.name}”从最近打开列表中移除？此操作仅移除记录。`
                : `Remove “${items[0]!.name}” from the recently opened list? This only removes the record.`,
            locale === "zh-cn" ? "移除最近记录" : "Remove recent record",
            expect.objectContaining({ confirmButtonText: locale === "zh-cn" ? "移除记录" : "Remove record", type: "warning" }),
        )
        expect(confirm.mock.calls[0]?.[2]).not.toHaveProperty("dangerouslyUseHTMLString", true)
        expect(events).toEqual(["confirming:true", "confirming:false", "remove:record-1"])
        expect(wrapper.emitted("open")).toBeUndefined()
        expect(wrapper.emitted("remove")).toEqual([["record-1"]])
        expect(wrapper.findAll(".recent-project-row")).toHaveLength(2)
    } finally {
        wrapper.unmount()
    }
})

it.each(["cancel", "close"])("删除确认 %s 时保留记录且不触发打开或隐藏", async (action) => {
    vi.spyOn(ElMessageBox, "confirm").mockRejectedValue(action)
    const wrapper = mount(RecentConnectionsPanel, { props: { items } })
    try {
        await wrapper.get(".recent-project-remove").trigger("click")
        await flushPromises()
        expect(wrapper.emitted("remove")).toBeUndefined()
        expect(wrapper.emitted("open")).toBeUndefined()
        expect(wrapper.emitted("hide")).toBeUndefined()
        expect(wrapper.emitted("confirming")).toEqual([[true], [false]])
        expect(wrapper.get(".recent-project-remove").attributes("disabled")).toBeUndefined()
    } finally {
        wrapper.unmount()
    }
})

it("等待删除确认期间锁定开关、打开及所有删除按钮, 不重复确认", async () => {
    const pending = pendingConfirmation()
    const confirm = vi.spyOn(ElMessageBox, "confirm").mockReturnValue(pending.promise)
    const wrapper = mount(RecentConnectionsPanel, { props: { items } })
    try {
        await wrapper.get(".recent-project-remove").trigger("click")
        expect(wrapper.findAll(".recent-projects button").every((button) => button.attributes("disabled") !== undefined)).toBe(true)
        expect(wrapper.findComponent({ name: "ElSwitch" }).props("disabled")).toBe(true)
        await wrapper.get(".recent-project-open").trigger("click")
        await wrapper.findAll(".recent-project-remove")[1]!.trigger("click")
        wrapper.findComponent({ name: "ElSwitch" }).vm.$emit("change", false)
        expect(confirm).toHaveBeenCalledTimes(1)
        expect(wrapper.emitted("open")).toBeUndefined()
        expect(wrapper.emitted("hide")).toBeUndefined()
        pending.resolve(confirmed)
        await flushPromises()
        expect(wrapper.findComponent({ name: "ElSwitch" }).props("disabled")).toBe(false)
        expect(wrapper.emitted("remove")).toEqual([["record-1"]])
    } finally {
        wrapper.unmount()
    }
})

it("父层确认锁经来源面板属性往返同步后才提交 slot 删除处理", async () => {
    const pending = pendingConfirmation()
    const confirm = vi.spyOn(ElMessageBox, "confirm").mockReturnValue(pending.promise)
    const removed = vi.fn()
    const source = defineComponent({
        props: { disabled: Boolean },
        /** 用真实属性更新和插槽模拟来源面板拒绝在途操作的入口. */
        setup(props, { slots }) {
            /** 删除处理在组件属性仍为禁用时拒绝进入宿主请求. */
            function remove(id: string): void {
                if (!props.disabled) removed(id)
            }
            return () => h("section", slots.default?.({ remove }))
        },
    })
    const parent = defineComponent({
        /** 确认只锁父层来源面板, 最近组件的外部请求锁独立于确认状态. */
        setup() {
            const confirming = ref(false)
            return () =>
                h(
                    source,
                    { disabled: confirming.value },
                    {
                        default: ({ remove }: { remove: (id: string) => void }) =>
                            h(RecentConnectionsPanel, { items, onConfirming: (value: boolean) => (confirming.value = value), onRemove: remove }),
                    },
                )
        },
    })
    const wrapper = mount(parent)
    try {
        await wrapper.get(".recent-project-remove").trigger("click")
        expect(wrapper.findComponent(source).props("disabled")).toBe(true)
        pending.resolve(confirmed)
        await flushPromises()
        expect(wrapper.findComponent(source).props("disabled")).toBe(false)
        expect(removed).toHaveBeenCalledExactlyOnceWith("record-1")
        expect(confirm).toHaveBeenCalledTimes(1)
    } finally {
        wrapper.unmount()
    }
})

it.each(["removed", "renamed", "replaced", "disabled", "temporarily-disabled", "removed-and-restored"] as const)(
    "等待确认中记录或操作状态发生 %s 时作废删除授权",
    async (change) => {
        const pending = pendingConfirmation()
        vi.spyOn(ElMessageBox, "confirm").mockReturnValue(pending.promise)
        const wrapper = mount(RecentConnectionsPanel, { props: { items: [...items] } })
        try {
            await wrapper.get(".recent-project-remove").trigger("click")
            if (change === "disabled" || change === "temporarily-disabled") {
                await wrapper.setProps({ disabled: true })
                if (change === "temporarily-disabled") await wrapper.setProps({ disabled: false })
            } else {
                await wrapper.setProps({
                    items:
                        change === "removed" || change === "removed-and-restored"
                            ? items.slice(1)
                            : items.map((item, index) =>
                                  index > 0 ? item : { ...item, ...(change === "renamed" ? { name: "已更名模型" } : { detail: "新账号 / 同名模型" }) },
                              ),
                })
                if (change === "removed-and-restored") await wrapper.setProps({ items: [...items] })
            }
            pending.resolve(confirmed)
            await flushPromises()
            expect(wrapper.emitted("remove")).toBeUndefined()
            expect(wrapper.emitted("confirming")).toEqual([[true], [false]])
        } finally {
            wrapper.unmount()
        }
    },
)

it("列表卸载后撤销父层确认锁, 延迟确认不删除新来源中的同标识记录", async () => {
    const pending = pendingConfirmation()
    vi.spyOn(ElMessageBox, "confirm").mockReturnValue(pending.promise)
    const confirming: boolean[] = []
    const remove = vi.fn()
    const wrapper = mount(RecentConnectionsPanel, { props: { items, onConfirming: (value: boolean) => confirming.push(value), onRemove: remove } })
    await wrapper.get(".recent-project-remove").trigger("click")
    wrapper.unmount()
    expect(confirming).toEqual([true, false])
    pending.resolve(confirmed)
    await flushPromises()
    expect(confirming).toEqual([true, false])
    expect(remove).not.toHaveBeenCalled()
})

it("外部请求锁与已从列表移除的旧按钮均不能发起确认", async () => {
    const confirm = vi.spyOn(ElMessageBox, "confirm").mockResolvedValue(confirmed)
    const wrapper = mount(RecentConnectionsPanel, { props: { items: [...items], disabled: true } })
    try {
        await wrapper.get(".recent-project-remove").trigger("click")
        expect(confirm).not.toHaveBeenCalled()
        await wrapper.setProps({ disabled: false })
        const oldButton = wrapper.get(".recent-project-remove").element
        await wrapper.setProps({ items: items.slice(1) })
        oldButton.dispatchEvent(new MouseEvent("click", { bubbles: true }))
        await flushPromises()
        expect(confirm).not.toHaveBeenCalled()
        expect(wrapper.emitted("remove")).toBeUndefined()
        await wrapper.get(".recent-project-open").trigger("click")
        expect(wrapper.emitted("open")).toEqual([["record-2"]])
    } finally {
        wrapper.unmount()
    }
})
