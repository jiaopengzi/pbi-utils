# FilePath    : pbi-utils\scripts\setup.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 检查固定工具链、还原依赖和显式同步许可, 只读检测桌面运行环境.

<# .SYNOPSIS 还原所选依赖或独立同步许可, 独立脚本默认 setupAll, 不安装工具. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    # 真实 Ctrl+C 会中止赋值并跳过 catch: 预设的取消码 130 由本脚本 finally 退出, 模块只返回退出码而不调用 exit.
    $pbiExitCode = 130
    try { $pbiExitCode = Invoke-PbiEntry -Arguments $args -Action setupAll }
    catch { $pbiExitCode = 4; [Console]::Error.WriteLine($_.ToString()) }
    finally { exit $pbiExitCode }
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if ($action -eq 'setupLicense') {
    Import-Module (Join-Path $PSScriptRoot 'LicenseCatalogUpdate.psm1') -Force
    $updated = Update-PbiLicenseCatalog -RepositoryRoot (Get-Location).Path
    if ($updated) { Write-PbiMessage '已更新 licenses/catalog.json, 请随依赖变更审阅并提交.' 'Success' }
    else { Write-PbiMessage '第三方许可清单已是最新.' 'Success' }
    return
}
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
