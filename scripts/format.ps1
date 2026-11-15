# FilePath    : pbi-utils\scripts\format.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 显式修复 C# 格式、风格和质量建议, 或格式化前端文件.

<# .SYNOPSIS 格式化 C#、Vue 或全部, 独立脚本默认 formatAll, 不由 all 隐式执行. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    # 真实 Ctrl+C 会中止赋值并跳过 catch: 预设的取消码 130 由本脚本 finally 退出, 模块只返回退出码而不调用 exit.
    $pbiExitCode = 130
    try { $pbiExitCode = Invoke-PbiEntry -Arguments $args -Action formatAll }
    catch { $pbiExitCode = 4; [Console]::Error.WriteLine($_.ToString()) }
    finally { exit $pbiExitCode }
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if (Invoke-PbiGroup 'format' $action $options) { return }
if ($action -eq 'formatVue') { Invoke-PbiNative pnpm @('--dir', 'web', 'format'); return }
foreach ($target in (Get-PbiFormatTargets $options.Scope)) {
    Invoke-PbiNative dotnet @('format', $target, '--no-restore', '--severity', 'info')
}
