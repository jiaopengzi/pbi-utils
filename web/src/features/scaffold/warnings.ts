/**
 * FilePath    : pbi-utils\web\src\features\scaffold\warnings.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 仅翻译本轮宿主固定警告, 用户表名和未知诊断始终保留原文.
 */
import { hostText, t } from "@/i18n"
const fixed: Record<string, string> = {
    TEMPLATE_SOURCE_REQUIRES_LOCALIZATION: "sourceLocalization",
    TEMPLATE_NAME_CONFLICT: "templateNameConflict",
    TEMPLATE_EXTERNAL_SOURCES_UNVERIFIED: "warningUserSource",
    SCAFFOLD_CLEANUP_PENDING: "cleanupPending",
    "模型定义回读不表示 M/DAX 已执行": "warningNoExecution",
    "项目已创建, 定义回读未完成, 请核对后继续; 不会重新发布": "warningReadbackPending",
    "项目已创建, Git 初始化状态需核验, 不会自动重试": "warningGitVerification",
    "项目已创建, Git 未初始化": "warningGitMissing",
    "项目已创建, Git 未初始化; 请安装或检查 Git 后在已有工程的 pbip 目录中手动初始化, 不要重复创建工程": "warningGitManual",
    "项目已创建, 发布回执未能保存, 下次将先核验现有产物": "warningReceiptPending",
    "用户模板外部源仅复制, 未执行或核验; 仅明确映射的表会接入本地数据": "warningUserSource",
    "移动工程后需更新 PbiUtilsProjectRoot 参数为新的项目绝对目录": "warningProjectRoot",
    "零售样例新增独立 PbiUtilsRegionId/PbiUtilsStoreId 并精确重绑5条业务关系; 原组织ID与组织关系保持, 未虚构组织记录": "warningRetailRelations",
    "零售产品成本/销售价格及订单单价三列按模拟精度提升为小数, 保留原对象名称和关系": "warningRetailDecimals",
    "UDF 输出副本兼容级别至少 1702; 元数据已解析不表示函数已在 Desktop 执行": "warningUdfExecution",
}
const tableWarnings: Record<string, string> = {
    "同名表已接入本轮模拟数据, 保留的模板额外数据列没有样例映射, 值为空": "warningSampleCompatibility",
    "为保留现有关系和报表引用, 保留必要兼容列": "warningCalendarColumns",
    "示例没有组织或员工身份, 对应关联字段保持空值": "warningMissingIdentity",
    "门店源仅有城市信息, 区县无法唯一确定时保持空值": "warningDistrict",
}
/** 已知消息按固定字典翻译, 仅对精确已知后缀解析动态表名前缀. */
export function scaffoldWarning(value: string): string {
    const key = fixed[value]
    if (key) return t(`scaffold.${key}`)
    const sampleRelations = /^模拟数据已复用 (\d+) 条关系; 缺少已选日期字段或日期表的 (\d+) 条关系未生成, 可配置日期字段后再建立$/.exec(value)
    if (sampleRelations) return t("scaffold.warningSampleRelationships", { installed: sampleRelations[1]!, skipped: sampleRelations[2]! })
    for (const [suffix, name] of Object.entries(tableWarnings))
        if (value.endsWith(`: ${suffix}`)) return t(`scaffold.${name}`, { table: value.slice(0, -(suffix.length + 2)) })
    return hostText(value)
}
