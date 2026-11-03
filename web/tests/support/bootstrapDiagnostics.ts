/**
 * FilePath    : pbi-utils\web\tests\support\bootstrapDiagnostics.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 在setup其余组件依赖求值前安装诊断守卫, 同时捕获后续模块导入时的意外诊断.
 */
import { installTestDiagnostics } from "./testDiagnostics"

// 此模块必须是setup首个运行时import; ES模块按依赖声明顺序求值, 不等待ElementPlus等依赖导入完成后才安装.
export const testDiagnosticsPlugin = installTestDiagnostics()
