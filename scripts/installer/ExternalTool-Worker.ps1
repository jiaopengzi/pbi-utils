# FilePath    : pbi-utils\scripts\installer\ExternalTool-Worker.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 只对两种固定 Power BI 外部工具登记执行严格比较替换的管理员入口, 不接受任意目标路径.

<# .SYNOPSIS 通过固定类别和完整前后描述执行一次登记事务, 成功退出 0, 参数或事务失败退出非零; 不执行描述中的程序. #>
param([Parameter(Mandatory = $true)][ValidateSet('Development', 'Installed')][string]$Kind,
    [Parameter(Mandatory = $true)][ValidateLength(1, 65536)][string]$Expected,
    [Parameter(Mandatory = $true)][ValidateLength(1, 65536)][string]$Desired)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    # PS7 父进程的模块路径不能影响系统 PowerShell 5.1 的内置模块加载.
    $env:PSModulePath = [Environment]::GetEnvironmentVariable('PSModulePath', 'Machine')
    Import-Module (Join-Path $PSScriptRoot 'ExternalToolFiles.psm1') -Force
    Invoke-PbiExternalToolFileChange $Kind $Expected $Desired
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 4
}
