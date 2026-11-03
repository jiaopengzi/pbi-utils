/**
 * FilePath    : pbi-utils\web\src\features\scaffold\calendarPreferences.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 两入口共用宿主日期方案, 主动编辑串行合并保存且不写浏览器存储.
 */
import { ref, shallowRef } from "vue"
import type { BridgeClient } from "@/bridge/client"
import type { CalendarPreferences, ScaffoldCalendarOptions } from "./contracts"
import { effectiveFields } from "./calendarFields"
const draftStates = new WeakMap<ScaffoldCalendarOptions, { baseline: string; initialized: boolean }>()
/** 以实际草稿对象隔离自动初始化状态, 初始快照用于保护挂载前已经编辑的草稿. */
export function calendarPreferenceDraftState(calendar: ScaffoldCalendarOptions) {
    let state = draftStates.get(calendar)
    if (!state) {
        state = { baseline: JSON.stringify(calendar), initialized: false }
        draftStates.set(calendar, state)
    }
    return state
}
/** 每个宿主桥接隔离一份队列, 页面卸载不丢失已授权的偏好排空. */
function createPreferences(bridge: BridgeClient) {
    const last = shallowRef<CalendarPreferences>()
    const remembered = shallowRef<CalendarPreferences>()
    const warning = ref("")
    let reading: Promise<CalendarPreferences | undefined> | undefined
    let pending: CalendarPreferences | undefined
    let writing: Promise<void> | undefined
    let editRevision = 0
    /** 迟到读取只更新未被主动保存取代的缓存, 不覆盖用户新方案. */
    async function read(): Promise<CalendarPreferences | undefined> {
        if (last.value) return last.value
        if (reading) return reading
        const revision = editRevision
        reading = Promise.resolve()
            .then(() => bridge.request("calendar.preferences", {}).promise)
            .then((value) => {
                if (revision === editRevision) {
                    last.value = value
                    if (!value.warning) remembered.value = value
                    warning.value = value.warning ?? ""
                }
                return revision === editRevision ? value : last.value
            })
            .catch(() => {
                if (revision === editRevision) warning.value = "read"
                return undefined
            })
            .finally(() => {
                reading = undefined
            })
        return reading
    }
    /** 主动字段变更仅写通用方案; 在途期间仅保留最后一份待写方案. */
    function save(fields: string[], numberedColumns: boolean): Promise<void> {
        pending = { fields: effectiveFields(fields), numberedColumns }
        last.value = pending
        editRevision++
        if (writing) return writing
        writing = (async () => {
            while (pending) {
                const value = pending
                pending = undefined
                try {
                    // eslint-disable-next-line no-await-in-loop -- 偏好必须串行排空, 后一版本不得先于前一版本保存.
                    const saved = await bridge.request("calendar.savePreferences", { fields: value.fields, numberedColumns: value.numberedColumns }).promise
                    if (!saved.warning) remembered.value = saved
                    warning.value = saved.warning ?? ""
                } catch {
                    warning.value = "save"
                }
            }
        })().finally(() => {
            writing = undefined
        })
        return writing
    }
    return { last, remembered, warning, read, save }
}
const shared = new WeakMap<BridgeClient, ReturnType<typeof createPreferences>>()
/** 两入口复用相同宿主方案和串行保存队列, 不共享模型草稿身份. */
export function calendarPreferences(bridge: BridgeClient) {
    let value = shared.get(bridge)
    if (!value) {
        value = createPreferences(bridge)
        shared.set(bridge, value)
    }
    return value
}
