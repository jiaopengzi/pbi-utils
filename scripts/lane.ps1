# FilePath    : pbi-utils\scripts\lane.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : verifyAndBuild 与 release 的内部并行通道入口, 只执行允许列表中的单个检查或测试阶段并写回结果.

<# .SYNOPSIS 由并行门禁启动的子进程入口; 接受 -Lane、-Result 及适用的 -Scope/-Configuration, 无效参数返回 2. #>
Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
# 真实 Ctrl+C 会中止赋值并跳过 catch: 预设的取消码 130 由本脚本 finally 退出, 取消结果由模块 finally 写回.
$pbiExitCode = 130
try { $pbiExitCode = Invoke-PbiEntry -Arguments $args -Lane }
catch { $pbiExitCode = 4; [Console]::Error.WriteLine($_.ToString()) }
finally { exit $pbiExitCode }
