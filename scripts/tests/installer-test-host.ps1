# FilePath    : pbi-utils\scripts\tests\installer-test-host.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在真实 PowerShell 测试子进程中建立 UTF-8 通道, 不改变产品安装器或系统区域设置.

<#
.SYNOPSIS 运行原始安装器测试脚本, 保留其 BOM、PSScriptRoot、场景和退出语义.
.PARAMETER TestScript 原始测试脚本的完整路径, 不读取或重写其源文本.
.PARAMETER Scenario 原样传给目标测试脚本的可选隔离场景.
.PARAMETER InitialCodePage 仅编码回归使用的子进程初始代码页, 0 保留默认值.
.OUTPUTS 目标脚本的真实输出和退出结果; 参数、脚本与编码初始化错误直接失败.
#>
param(
    [Parameter(Mandatory = $true)][string]$TestScript,
    [string]$Scenario,
    [int]$InitialCodePage = 0
)

$ErrorActionPreference = 'Stop'
if ($InitialCodePage -ne 0) {
    $initialEncoding = [Text.Encoding]::GetEncoding($InitialCodePage)
    [Console]::InputEncoding = $initialEncoding
    [Console]::OutputEncoding = $initialEncoding
    $OutputEncoding = $initialEncoding
}

# 只设置当前测试子进程; 父进程按同一 UTF-8 读取, 不让 CP1252 在写出前将中文替换成问号.
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

$parameters = @{}
if ($Scenario) { $parameters.Scenario = $Scenario }
# 直接执行原文件, 不用 ScriptBlock 重建而丢失 PSScriptRoot; dot-source 保留 -File 的显式 exit 语义.
. $TestScript @parameters
