/**
 * FilePath    : pbi-utils\web\src\ui\forms.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 复用 Element Plus 表单校验和年份范围规则, 后端仍为权威校验边界.
 */
import { translate as t } from "@/i18n"
import type { FormInstance, FormItemRule } from "element-plus"

/** 校验已挂载表单, 字段失败由组件显示并返回 false, 不发送宿主请求. */
export async function validateForm(form: FormInstance | undefined): Promise<boolean> {
    if (!form) return false
    return form.validate().catch(() => false)
}

/** 字符串标识多选只保留字符串, 不将控件允许的数字转换为模型或属性身份. */
export function stringSelection(values: (string | number)[]): string[] {
    return values.filter((value): value is string => typeof value === "string")
}

/** 年份必须为 1900-2100 的整数且起止有序, getter 在校验时读取最新范围. */
export function yearRule(range: () => [number, number]): FormItemRule {
    return {
        validator: (_rule, _value, callback) => {
            const [start, end] = range()
            callback(
                Number.isInteger(start) && Number.isInteger(end) && start >= 1900 && end <= 2100 && start <= end ? undefined : new Error(t("model.yearBounds")),
            )
        },
        trigger: "change",
    }
}
