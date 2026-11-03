# FilePath    : pbi-utils\scripts\tests\AcceptanceUserDataLease.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 为独立 PowerShell 验收加载与 xUnit 相同的跨进程协调租约.

<#
.SYNOPSIS 等待同用户其他测试完成, 返回必须由调用方 finally 释放的测试租约.
.OUTPUTS IDisposable 租约; 超时或加载错误终止本次验收, 不启动产品操作.
.NOTES 仅测试采用; 用户直接启动的产品不受此协调文件影响, 产品锁继续独立强制执行.
#>
function Enter-AcceptanceUserDataLease {
    if (!('PbiUtils.Testing.AcceptanceUserDataLease' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'AcceptanceUserDataLease.cs')
    }
    return [PbiUtils.Testing.AcceptanceUserDataLease]::new()
}
