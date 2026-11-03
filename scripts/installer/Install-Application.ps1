# FilePath    : pbi-utils\scripts\installer\Install-Application.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : NSIS 调用的 Windows PowerShell 5.1 安装和卸载入口, 以退出码及错误文本报告真实结果.

<#
.SYNOPSIS 执行当前用户安装事务, 输出结构化结果并通过非零退出码报告参数或事务错误.
.PARAMETER Operation 安装或卸载动作.
.PARAMETER InstallRoot 已由安装器确定的产品目录.
.PARAMETER PayloadRoot 安装载荷目录, 卸载不使用.
.PARAMETER CreateDesktopShortcut 桌面入口选择, 仅接受 0 或 1, 默认 1; 卸载忽略创建选择.
.PARAMETER CreateStartMenuShortcut 开始菜单入口选择, 仅接受 0 或 1, 默认 1.
.PARAMETER RegistrationNonInteractive 静默安装或卸载使用 1, 登记权限不足直接失败, 不请求 UAC.
.NOTES 使用整数跨越 Windows PowerShell 5.1 -File 原生参数边界, 进入事务前转换为布尔值.
#>
param([Parameter(Mandatory = $true)][ValidateSet('Install', 'Uninstall')][string]$Operation,
    [Parameter(Mandatory = $true)][string]$InstallRoot, [string]$PayloadRoot,
    [ValidateSet(0, 1)][int]$CreateDesktopShortcut = 1, [ValidateSet(0, 1)][int]$CreateStartMenuShortcut = 1,
    [ValidateSet(0, 1)][int]$RegistrationNonInteractive = 0)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'Installation.psm1') -Force
    $result = Invoke-PbiApplicationInstallation -Operation $Operation -InstallRoot $InstallRoot -PayloadRoot $PayloadRoot `
        -CreateDesktopShortcut ([bool]$CreateDesktopShortcut) -CreateStartMenuShortcut ([bool]$CreateStartMenuShortcut) -RegistrationNonInteractive ([bool]$RegistrationNonInteractive)
    Write-Output ($result | ConvertTo-Json -Compress)
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if ($_.Exception.Data.Contains('ExitCode') -and $_.Exception.Data['ExitCode'] -in @(2, 3, 4, 130)) { exit $_.Exception.Data['ExitCode'] }
    exit 4
}
