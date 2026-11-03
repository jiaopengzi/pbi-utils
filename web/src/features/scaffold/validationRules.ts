/**
 * FilePath    : pbi-utils\web\src\features\scaffold\validationRules.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 配置校验的宿主兼容文本、数字和字段问题构造规则.
 */
import { dateMillis } from "@/features/sample/presentation"
import type { ScaffoldValidationFields, ScaffoldValidationIssue, ScaffoldValidationStep } from "./validationTypes"
type Message =
    | "Template"
    | "ProjectName"
    | "Output"
    | "Name"
    | "Range"
    | "Text"
    | "Count"
    | "Duplicate"
    | "Category"
    | "Date"
    | "DateGap"
    | "YearOrder"
    | "CalendarKind"
    | "OverrideRange"
    | "Boolean"
    | "Binding"
    | "PeriodTemplate"
    | "PeriodName"
    | "Source"
    | "SourceBaseline"
    | "Package"
    | "Version"
    | "Prerelease"
    | "Baseline"
    | "Seed"
    | "CalendarFields"
    | "CalendarWorkdays"
    | "CalendarBudget"
    | "WorkdayName"
    | "QueryGroup"
export const controls = /\p{Cc}/u
// 与 .NET Char.IsWhiteSpace 对齐, 不把 JavaScript 额外 trim 的 BOM 当成宿主禁止的空白.
const whiteSpace = /^\p{White_Space}*$/u
export const boundarySpace = /^\p{White_Space}|\p{White_Space}$/u
export const semver = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$/

/** 生成步骤与字段受类型约束的固定消息, 不包含用户目录、表达式或来源身份. */
export function issue<Step extends ScaffoldValidationStep>(
    step: Step,
    field: ScaffoldValidationFields[Step],
    message: Message,
    row?: number,
    params?: Record<string, string | number>,
): Extract<ScaffoldValidationIssue, { step: Step }> {
    return { step, field, key: `scaffold.validation${message}`, ...(row === undefined ? {} : { row }), ...(params === undefined ? {} : { params }) } as Extract<
        ScaffoldValidationIssue,
        { step: Step }
    >
}
/** 按宿主空白定义检查文本, 非字符串也作为缺失处理. */
export function blank(value: unknown): boolean {
    return typeof value !== "string" || whiteSpace.test(value)
}
/** null 与省略字段沿用宿主可选参数默认值, 不将空字符串或零当成缺失. */
export function absent(value: unknown): value is null | undefined {
    return value === null || value === undefined
}
/** 按模型对象名称边界检查 UTF-16 长度与控制字符, 不禁止已由 DAX 转义支持的标点. */
export function objectName(value: unknown): value is string {
    return typeof value === "string" && !blank(value) && value.length <= 200 && !controls.test(value)
}
/** 分组允许显式空串, 非空值按宿主200个UTF-16字符、无控制字符及非全空白边界校验, 不裁剪业务空格. */
export function queryGroupName(value: unknown): value is string {
    return typeof value === "string" && value.length <= 200 && !controls.test(value) && (value === "" || !blank(value))
}
/** 按宿主 UTF-8 字节预算检查文本, 保留合法换行和制表符. */
export function text(value: unknown, maximum: number): value is string {
    return (
        typeof value === "string" &&
        new TextEncoder().encode(value).length <= maximum &&
        ![...value].some((character) => controls.test(character) && character !== "\r" && character !== "\n" && character !== "\t")
    )
}
/** 输入数字须为有界整数, 不把 NaN、Infinity 或空值转换成零. */
export function integer(value: unknown, min: number, max: number): boolean {
    return typeof value === "number" && Number.isInteger(value) && value >= min && value <= max
}
/** 摘要只验证已读身份的形状, 不把它作为任何文件或来源授权. */
export function digest(value: unknown): boolean {
    return typeof value === "string" && /^[a-f0-9]{64}$/.test(value)
}
/** 只做不扩展字符数量的大小写归一, 避免把不同名称的 Unicode 展开结果误判为重复. */
export function nameKey(value: string): string {
    return [...value]
        .map((character) => {
            const upper = character.toUpperCase()
            return upper.length === character.length && character !== "ı" ? upper : character
        })
        .join("")
}
/** 读取真实日历日期, 非字符串与非法日期都返回 NaN. */
export function date(value: unknown): number {
    return typeof value === "string" ? dateMillis(value) : NaN
}
