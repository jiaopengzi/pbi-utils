/**
 * FilePath    : pbi-utils\web\tests\app\ModelTablesPanel.support.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为表格批量导出行为提供可控桥接终态与独立统计回复, 不查询真实模型或写文件.
 */
import { markRaw } from "vue"
import { mount, flushPromises, type VueWrapper } from "@vue/test-utils"
import ModelTablesPanel from "@/app/ModelTablesPanel.vue"
import { BridgeClient, type ProjectSnapshot } from "@/bridge/client"
import type { ModelExportLocation, ModelTableStatisticsResult } from "@/bridge/modelContracts"
import { previewProject } from "@/bridge/preview"

export type TestRequest = { id: string; method: string; params: Record<string, unknown> }
export type StatisticsRows = ModelTableStatisticsResult["tables"]
export type TableTestOptions = { statistics?: StatisticsRows; autoStatistics?: boolean; location?: ModelExportLocation; autoLocation?: boolean }

/** 创建两个可选表的只读在线夹具, 不依赖其它用例的对象或选择状态. */
export function tablesProject(): ProjectSnapshot {
    const project = structuredClone(previewProject)
    project.projectPath = "model://isolated"
    project.connection = { kind: "ssas-xmla", label: "isolated" }
    project.model.tables.push({ ...structuredClone(project.model.tables[0]!), name: "另一张表" })
    return project
}

/** 将统计请求与业务请求分别记录; 默认异步回应当前请求身份, 也可手动注入迟到统计结果. */
export function controlledBridge(project: ProjectSnapshot, initial: TableTestOptions = {}) {
    let receive: ((event: MessageEvent) => void) | undefined
    let statistics: StatisticsRows =
        initial.statistics ??
        project.model.tables.map((table) => ({ table: table.name, rowCount: null, sizeBytes: null, isEstimate: false, isQueryCount: false }))
    let autoStatistics = initial.autoStatistics ?? true
    let location: ModelExportLocation = initial.location ?? { id: null, displayPath: null, available: false, warning: null, isSimulation: false }
    let autoLocation = initial.autoLocation ?? true
    const sent: TestRequest[] = [],
        statisticsRequests: TestRequest[] = [],
        rowCountRequests: TestRequest[] = [],
        locationRequests: TestRequest[] = [],
        directoryRequests: TestRequest[] = []
    /** 仅向当前客户端发送带匹配协议版本的受控结果, 不把取消确认当作原请求终态. */
    const send = (message: object) => receive?.(new MessageEvent("message", { data: { protocolVersion: 1, ...message } }))
    /** 用原统计请求的精确身份回复; 数据缺失以null保留, 不将其替换成0. */
    function replyStatistics(request: TestRequest, rows: StatisticsRows = statistics): void {
        send({
            id: request.id,
            result: { projectId: request.params.projectId, baselineId: request.params.baselineId, tables: structuredClone(rows), isSimulation: false },
        })
    }
    /** 目录读取独立回应, 不把自动初始化请求混入导出/取消的发送顺序. */
    function replyLocation(request: TestRequest, value: ModelExportLocation = location): void {
        send({ id: request.id, result: structuredClone(value) })
    }
    const bridge = markRaw(
        new BridgeClient({
            postMessage: (message) => {
                const request = message as TestRequest
                if (request.method === "model.tableStatistics") {
                    statisticsRequests.push(request)
                    if (autoStatistics) queueMicrotask(() => replyStatistics(request))
                } else if (request.method === "model.tableRowCounts") {
                    rowCountRequests.push(request)
                } else if (request.method === "model.exportLocation") {
                    locationRequests.push(request)
                    if (autoLocation) queueMicrotask(() => replyLocation(request))
                } else if (request.method === "model.openExportDirectory") {
                    directoryRequests.push(request)
                } else sent.push(request)
            },
            addEventListener: (_type, listener) => {
                receive = listener
            },
            removeEventListener: () => undefined,
        }),
    )
    return {
        bridge,
        sent,
        statisticsRequests,
        rowCountRequests,
        locationRequests,
        directoryRequests,
        send,
        replyStatistics,
        replyLocation,
        setStatistics: (rows: StatisticsRows) => {
            statistics = rows
        },
        setAutoStatistics: (value: boolean) => {
            autoStatistics = value
        },
        setLocation: (value: ModelExportLocation) => {
            location = value
        },
        setAutoLocation: (value: boolean) => {
            autoLocation = value
        },
    }
}

/** 挂载实际表格组件和真实类型化客户端, 统计默认排空, 选择保持初始为空. */
export async function setupTables(options: TableTestOptions & { project?: ProjectSnapshot } = {}) {
    const project = options.project ?? tablesProject()
    const transport = controlledBridge(project, options)
    const wrapper = mount(ModelTablesPanel, { props: { project, bridge: transport.bridge } })
    await flushPromises()
    let mounted = true
    /** 作用域只卸载一次, 避免重复清理污染Vue诊断或其它测试的全局消息. */
    function unmount(): void {
        if (mounted) {
            mounted = false
            wrapper.unmount()
        }
    }
    return {
        project,
        wrapper,
        ...transport,
        unmount,
        cleanup: () => {
            unmount()
            transport.bridge.dispose()
        },
    }
}

/** 通过当前语言的可访问名称选择真实checkbox, 不调用组件内部选择函数. */
export async function selectTable(wrapper: VueWrapper, table: string, selected = true): Promise<void> {
    const label = wrapper
        .findAll("label[aria-label]")
        .find((item) => [`选择表 ${table}`, `Select table ${table}`].includes(item.attributes("aria-label") ?? ""))
    if (!label) throw new Error(`Table selection checkbox is missing: ${table}`)
    await label.get<HTMLInputElement>('input[type="checkbox"]').setValue(selected)
}

/** 返回用户可见表项, 用于核对字段和表统计, 不读取父组件私有状态. */
export function tableItem(wrapper: VueWrapper, table: string) {
    const item = wrapper.findAll(".el-collapse-item").find((entry) => entry.find(".table-name").text() === table)
    if (!item) throw new Error(`Table item is missing: ${table}`)
    return item
}

/** 根据用户可见的行数与表名构造宿主完成摘要, 不制造任何真实文件. */
export function exportResult(
    fileName: string,
    tables: string[],
    count = 2,
    format: "xlsx" | "csv" = "xlsx",
    isSimulation = false,
    rowLimit: number | null = 100,
) {
    return {
        fileName,
        rowCount: count * tables.length,
        tables: tables.map((table) => ({ table, rowCount: count })),
        format,
        isSimulation,
        rowLimit,
        locationWarning: null,
    }
}
