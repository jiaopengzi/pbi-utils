#!/usr/bin/env pwsh
# FilePath    : pbi-utils\run.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 提供 C#/Vue/All 开发命令菜单与单次执行入口, 返回统一退出码.

<#
.SYNOPSIS
显示声明式菜单或一次执行指定动作, 返回产品约定退出码.
.DESCRIPTION
使用 -List 或 -Help 查看动作与参数. 构建通过 -DotNetMode 选择一种部署模式, Debug 默认 with-dotnet, Release 默认 without-dotnet. package 从已有 Release 产物生成安装包, release 完整验证后打包; 不安装工具或执行任意字符串.
#>
Import-Module (Join-Path $PSScriptRoot 'scripts/CommandRegistry.psm1') -Force
exit (Invoke-PbiEntry -Arguments $args)
