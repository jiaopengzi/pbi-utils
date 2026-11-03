# FilePath    : pbi-utils\scripts\format.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 显式修复 C# 格式、风格和质量建议, 或格式化前端文件.

<# .SYNOPSIS 格式化 C#、Vue 或全部, 独立脚本默认 formatAll, 不由 all 隐式执行. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    exit (Invoke-PbiEntry -Arguments $args -Action formatAll)
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if (Invoke-PbiGroup 'format' $action $options) { return }
if ($action -eq 'formatVue') { Invoke-PbiNative pnpm @('--dir', 'web', 'format'); return }
foreach ($target in (Get-PbiFormatTargets $options.Scope)) {
    Invoke-PbiNative dotnet @('format', $target, '--no-restore', '--severity', 'info')
}
