/**
 * FilePath    : pbi-utils\web\tests\features\scaffold\calendarPreferences.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证跨入口共享偏好, 迟到读取, 串行合并和保存失败保留编辑.
 */
import { expect, it, vi } from "vitest"
import { calendarPreferences } from "@/features/scaffold/calendarPreferences"
import type { CalendarPreferences } from "@/features/scaffold/contracts"
it("两个入口共享宿主偏好, 迟到读取不覆盖主动新方案", async () => {
    let resolveRead!: (value: CalendarPreferences) => void
    const bridge = {
        request: vi.fn((method: string, params: CalendarPreferences) => ({
            promise:
                method === "calendar.preferences"
                    ? new Promise<CalendarPreferences>((resolve) => {
                          resolveRead = resolve
                      })
                    : Promise.resolve(params),
        })),
    }
    const first = calendarPreferences(bridge as never),
        second = calendarPreferences(bridge as never)
    expect(first).toBe(second)
    const reading = first.read()
    await second.save(["Dates", "Status"], false)
    resolveRead({ fields: ["Dates", "LunarYear"], numberedColumns: true })
    await reading
    expect(first.last.value).toMatchObject({ fields: ["Dates", "Status"], numberedColumns: false })
})
it("在途保存只合并最后待写方案, 失败仍保留编辑和明确警告", async () => {
    let resolveFirst!: (value: CalendarPreferences) => void
    const bridge = {
        request: vi
            .fn()
            .mockImplementationOnce(() => ({
                promise: new Promise((resolve) => {
                    resolveFirst = resolve
                }),
            }))
            .mockImplementation(() => ({ promise: Promise.reject(new Error("storage")) })),
    }
    const preference = calendarPreferences(bridge as never)
    const writing = preference.save(["Dates", "Day"], true)
    void preference.save(["Dates", "Month"], true)
    void preference.save(["Dates", "Status"], false)
    resolveFirst({ fields: ["Dates", "Day"], numberedColumns: true })
    await writing
    expect(bridge.request).toHaveBeenCalledTimes(2)
    expect(bridge.request.mock.calls[1]?.[1]).toMatchObject({ fields: ["Dates", "Status"], numberedColumns: false })
    expect(preference.last.value?.fields).toEqual(["Dates", "Status"])
    expect(preference.remembered.value?.fields).toEqual(["Dates", "Day"])
    expect(preference.warning.value).toBe("save")
})
