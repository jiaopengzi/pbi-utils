/**
 * FilePath    : pbi-utils\web\src\features\code\measureExpression.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 在度量值编辑正文与 Desktop 定义头保存包装之间转换, 保留词元内部原文.
 */
/**
 * 将 Measure.Expression 的定义头换行投影为编辑正文, 不重新格式化或调整用户缩进.
 * @param expression 权威保存正文或普通编辑正文, 字面量与注释内容保持逐字符一致.
 * @returns 只移除首个 LF/CRLF 的正文; 无首换行或空白输入原样返回, 不抛词法错误.
 */
export function measureExpressionForEditor(expression: string): string {
    if (!/\S/.test(expression)) return expression
    return expression.replace(/^\r?\n/, "")
}

/**
 * 为编辑正文保留 Desktop 定义头所需的单个 LF 包装, 不添加任何代码缩进或验证计算.
 * @param expression 编辑正文或已包装保存正文, 可含未完成的字面量或注释.
 * @returns 可进入真实保存大小校验的正文; 空白输入原样返回, 正文内部空白保持, 不抛词法错误.
 */
export function measureExpressionForSave(expression: string): string {
    if (!/\S/.test(expression)) return expression
    return "\n" + measureExpressionForEditor(expression)
}
