/**
 * FilePath    : pbi-utils\web\tests\app\useMeasureDraftGuard.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证度量草稿确认在失败、目标变化、在途与卸载时不会重放旧导航.
 */
import { effectScope, ref, shallowRef } from "vue"
import { expect, it, vi } from "vitest"
import { useMeasureDraftGuard } from "@/composables/app/useMeasureDraftGuard"
import { previewProject } from "@/bridge/preview"

/** 建立真实响应式作用域与独立目标, 返回可控保存终态和导航观察点; 不调用模型写入. */
function setup() {
    const scope = effectScope()
    const project = shallowRef(structuredClone(previewProject))
    const dirty = ref(true),
        busy = ref(false),
        invalidated = ref(false)
    const save = vi.fn<() => Promise<boolean>>().mockResolvedValue(true)
    const apply = vi.fn()
    const guard = scope.run(() =>
        useMeasureDraftGuard({
            project: () => project.value,
            dirty: () => dirty.value,
            busy: () => busy.value,
            invalidated: () => invalidated.value,
            save,
            apply,
        }),
    )!
    return { scope, project, dirty, busy, invalidated, save, apply, guard }
}

it("未完成或取消保存保留原导航确认, 显式放弃才继续唯一原意图", async () => {
    const { scope, guard, save, apply } = setup()
    try {
        guard.request({ kind: "selection", keys: ["original"] })
        guard.request({ kind: "create" })
        save.mockResolvedValueOnce(false)
        await guard.resolve("save")
        expect(guard.confirming.value).toBe(true)
        expect(apply).not.toHaveBeenCalled()
        await guard.resolve("discard")
        expect(apply.mock.calls).toEqual([[{ kind: "selection", keys: ["original"] }]])
        expect(guard.confirming.value).toBe(false)
    } finally {
        scope.stop()
    }
})

it.each(["target", "dispose", "invalidated"] as const)("保存期间 %s 变化使旧导航失效, 迟到成功不触发切换", async (change) => {
    const { scope, project, guard, invalidated, save, apply } = setup()
    try {
        let complete!: (value: boolean) => void
        save.mockImplementationOnce(
            () =>
                new Promise<boolean>((resolve) => {
                    complete = resolve
                }),
        )
        guard.request({ kind: "create" })
        const saving = guard.resolve("save")
        expect(guard.saving.value).toBe(true)
        await guard.resolve("discard")
        if (change === "target") project.value = { ...project.value, projectId: "other-target" }
        else if (change === "invalidated") invalidated.value = true
        else scope.stop()
        complete(true)
        await saving
        expect(apply).not.toHaveBeenCalled()
        expect(guard.confirming.value).toBe(false)
        expect(guard.saving.value).toBe(false)
    } finally {
        scope.stop()
    }
})

it("无草稿直接导航, 在途拒绝新意图, 取消确认不触发保存", async () => {
    const { scope, guard, busy, dirty, save, apply } = setup()
    try {
        busy.value = true
        guard.request({ kind: "create" })
        expect(guard.confirming.value).toBe(false)
        busy.value = false
        dirty.value = false
        guard.request({ kind: "create" })
        expect(apply).toHaveBeenCalledExactlyOnceWith({ kind: "create" })
        dirty.value = true
        guard.request({ kind: "edit" })
        await guard.resolve("cancel")
        expect(save).not.toHaveBeenCalled()
        expect(apply).toHaveBeenCalledTimes(1)
    } finally {
        scope.stop()
    }
})
