# FilePath    : pbi-utils\scripts\setup.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 检查固定工具链并还原所选范围依赖, 只读检测桌面运行环境.

<# .SYNOPSIS 还原 C#、Vue 或全部依赖, 独立脚本默认 setupAll, 不安装工具. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    exit (Invoke-PbiEntry -Arguments $args -Action setupAll)
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if (Invoke-PbiGroup 'setup' $action $options) { return }
if ($action -eq 'setupVue') {
    Invoke-PbiNative pnpm @('--dir', 'web', 'install', '--frozen-lockfile')
    return
}
# 完整范围一次还原解决方案, 避免逐项目重复遍历同一依赖图; Core 保持项目隔离.
$restoreTargets = if ($options.Scope -eq 'All') { @('pbi-utils.slnx') } else { @(Get-PbiProjects 'Core') }
foreach ($project in $restoreTargets) {
    Invoke-PbiNative dotnet @('restore', $project, '--locked-mode', '--nologo')
}
if ($options.Scope -eq 'All') {
    $runtime = Get-PbiWebViewRuntime
    Write-PbiMessage "WebView2 Evergreen Runtime=$runtime; 官方来源见 .docs/toolchain.md." $(if ($runtime -eq 'missing') { 'Warning' } else { 'Success' })
}
