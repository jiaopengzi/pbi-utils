/**
 * FilePath    : pbi-utils\web\tests\bridge\configurationSources.test.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 验证显式配置模拟的来源修订、空配置回读和模型/文件事实隔离.
 */
import { expect, it } from "vitest"
import { ConfigurationMock } from "@/bridge/configurationMock"
import { previewProject } from "@/bridge/preview"

it("模拟 Config 的空集合可写入并回读文件事实, 后续 TMDL 切换移除本功能文件来源", () => {
    const mock = new ConfigurationMock()
    const project = structuredClone(previewProject)
    const initial = mock.context("current", project)
    expect(initial.measureConfigurationRevision).toBe("missing")
    const configured = mock.apply(
        {
            targetId: initial.targetId,
            baselineId: project.baselineId,
            section: "measures",
            request: { measuresMode: "config", measures: [], measureConfigurationRevision: "missing" },
        },
        project,
    )
    expect(configured).toMatchObject({ measureConfiguration: { mode: "config", measures: [] }, configMeasures: [], isSimulation: true })
    expect(configured.measureConfigurationRevision).toMatch(/^[a-f0-9]{64}$/)
    const tmdl = mock.apply(
        {
            targetId: configured.targetId,
            baselineId: project.baselineId,
            section: "measures",
            request: { measuresMode: "tmdl", measures: [], measureConfigurationRevision: configured.measureConfigurationRevision },
        },
        project,
    )
    expect(tmdl).toMatchObject({ measureConfiguration: { mode: "tmdl", measures: [] }, configMeasures: null, measureConfigurationRevision: "missing" })
})

it.each([
    { path: "模拟项目.pbip", kind: "pbip" as const },
    { path: "模拟报告.pbix", kind: "desktop" as const },
    { path: "model://preview", kind: "desktop" as const },
])("显式本机 $kind 模拟 $path 无预建配置时可选择并应用 Config", ({ path, kind }) => {
    const mock = new ConfigurationMock()
    const project = { ...structuredClone(previewProject), projectPath: path, connection: { kind, label: "模拟本机目标" } }
    const initial = mock.context("current", project)
    expect(initial).toMatchObject({
        canUseMeasureConfig: true,
        measureConfigurationRevision: "missing",
        configMeasures: null,
        measureConfigUnavailableReason: null,
        isSimulation: true,
    })
    const configured = mock.apply(
        {
            targetId: initial.targetId,
            baselineId: project.baselineId,
            section: "measures",
            request: {
                measuresMode: "config",
                measures: [{ name: "Custom", value: "模拟配置", enabled: false, dataCategory: "Uncategorized", description: "", displayFolder: "" }],
                measureConfigurationRevision: "missing",
            },
        },
        project,
    )
    expect(configured).toMatchObject({
        measureConfiguration: { mode: "config", measures: [{ name: "Custom", value: "模拟配置" }] },
        configMeasures: [{ name: "Custom", value: "模拟配置" }],
        isSimulation: true,
    })
    expect(configured.measureConfigurationRevision).toMatch(/^[a-f0-9]{64}$/)
})

it.each(["powerbi-service", "ssas-xmla"] as const)("显式 %s 模拟保持只读且不发放 Config 能力", (kind) => {
    const mock = new ConfigurationMock()
    const project = { ...structuredClone(previewProject), projectPath: "模拟名称.pbix", connection: { kind, label: "模拟远端目标" } }
    expect(mock.context("current", project)).toMatchObject({ canWrite: false, canUseMeasureConfig: false, measureConfigurationRevision: null })
})

it("模拟拒绝旧来源修订并保留当前配置与模型基线", () => {
    const mock = new ConfigurationMock()
    const project = structuredClone(previewProject)
    const current = mock.context("current", project)
    const original = structuredClone(project)
    expect(() =>
        mock.apply(
            {
                targetId: current.targetId,
                baselineId: project.baselineId,
                section: "measures",
                request: { measuresMode: "config", measures: [], measureConfigurationRevision: "a".repeat(64) },
            },
            project,
        ),
    ).toThrow("STALE_CONFIGURATION_SOURCE")
    expect(project).toEqual(original)
    expect(mock.context("current", project).measureConfigurationRevision).toBe("missing")
})
