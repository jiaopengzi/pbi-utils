/**
 * FilePath    : pbi-utils\web\src\bridge\preview.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式浏览器预览夹具, 不引用用户工程或模拟成真实文件.
 */
import type { ProjectSnapshot } from "./client"
export const previewProject: ProjectSnapshot = {
    projectPath: "模拟零售.pbip",
    projectId: "preview",
    baselineId: "preview-only",
    modelFormat: "TMDL",
    reportFormat: "PBIR",
    modelVersion: "4.2",
    reportVersion: "4.0",
    model: {
        name: "零售演示（模拟）",
        compatibilityLevel: 1702,
        culture: "zh-CN",
        relationshipCount: 2,
        roleCount: 0,
        tables: [
            {
                name: "销售",
                description: "虚构数据",
                isHidden: false,
                partitionKinds: ["MPartitionSource"],
                columns: [{ name: "金额", kind: "Data", dataType: "Decimal", isHidden: false, description: null }],
            },
        ],
        measures: [
            {
                table: "销售",
                name: "销售额",
                expression: "\nSUM ( '销售'[金额] )",
                description: "模拟指标，未读取真实文件",
                formatString: "0.00",
                displayFolder: "基础指标",
                isHidden: false,
            },
        ],
        functions: [{ name: "Demo.Double", expression: "(value : NUMERIC) => value * 2", description: "未执行的模拟 UDF" }],
    },
    pages: [
        {
            id: "overview",
            displayName: "经营概览（模拟）",
            width: 1280,
            height: 720,
            visuals: [{ id: "card1", type: "card", x: 30, y: 30, width: 200, height: 100 }],
        },
    ],
    files: [],
    warnings: ["浏览器预览全部为模拟数据"],
}
