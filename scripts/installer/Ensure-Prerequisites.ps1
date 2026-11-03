# FilePath    : pbi-utils\scripts\installer\Ensure-Prerequisites.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : NSIS 调用的 Windows PowerShell 5.1 前置条件入口, 保留检测与安装退出码.

<#
.SYNOPSIS 读取随安装器分发的前置清单, 检测并按需安装微软运行时.
.PARAMETER ManifestPath 构建生成的前置 JSON; 不访问源码、开发 SDK 或产品 .NET 程序.
.PARAMETER CheckOnly 仅检测, 缺失退出 3; 正常模式补齐后退出 0 或 3010 表示需重启.
.NOTES 失败退出非零; 诊断不输出原始异常中的用户路径或网络信息. 不更改执行策略.
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ManifestPath, [switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try {
    Import-Module (Join-Path $PSScriptRoot 'Prerequisites.psm1') -Force
    exit (Invoke-PbiInstallerPrerequisites -ManifestPath $ManifestPath -CheckOnly:$CheckOnly)
} catch {
    $code = 4
    if ($_.Exception -is [ComponentModel.Win32Exception] -and $_.Exception.NativeErrorCode -eq 1223) { $code = 1223 }
    $message = [string]$_.Exception.Message
    if ($message -notmatch '^PREREQUISITE_[A-Z0-9_]+$') { $message = 'PREREQUISITE_OPERATION_FAILED' }
    Write-Host $message
    exit $code
}
