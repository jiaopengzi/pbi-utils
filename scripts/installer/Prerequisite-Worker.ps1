# FilePath    : pbi-utils\scripts\installer\Prerequisite-Worker.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 安装向导后台运行环境检测和用户发起的缺失组件安装入口.
<#
.SYNOPSIS 在 NSIS 私有任务目录内运行固定工作流, 不接受自由 URL 或命令.
.PARAMETER ManifestPath 内置运行环境清单; WorkRoot 为本轮私有目录.
.PARAMETER InstallMissing 仅当用户点击安装时为 1; 其他值只读检查.
.OUTPUTS 原子状态及进程标识文件; 下载文件在工作结束后清理.
#>
param([Parameter(Mandatory=$true)][string]$ManifestPath, [Parameter(Mandatory=$true)][string]$WorkRoot, [ValidateSet('0','1')][string]$InstallMissing='0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$state = Join-Path $WorkRoot 'state.ini'
$cancel = Join-Path $WorkRoot 'cancel'
[IO.File]::WriteAllText((Join-Path $WorkRoot 'pid'), [string]$PID, [Text.Encoding]::ASCII)
try {
    Import-Module (Join-Path $PSScriptRoot 'Prerequisites.psm1') -Force
    Invoke-PbiPrerequisiteWorker $ManifestPath $state $cancel $WorkRoot ($InstallMissing -eq '1')
} finally {
    foreach ($name in @('dotnet.exe','webview2.exe')) {
        $file = Join-Path $WorkRoot $name
        if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
    }
}
