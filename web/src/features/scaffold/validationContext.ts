/**
 * FilePath    : pbi-utils\web\src\features\scaffold\validationContext.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 在新建表单与延迟挂载字段之间共享当前校验问题, 不持有业务权限.
 */
import type { InjectionKey, ShallowRef } from "vue"
import type { ScaffoldValidationIssue } from "./validation"

export const scaffoldValidationKey: InjectionKey<ShallowRef<ScaffoldValidationIssue | undefined>> = Symbol("scaffold-validation")
export const scaffoldValidationPrefixKey: InjectionKey<string> = Symbol("scaffold-validation-prefix")
