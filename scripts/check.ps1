# FilePath    : pbi-utils\scripts\check.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 只读检查 C# 格式、风格和质量建议, 前端格式、lint 和类型.

<# .SYNOPSIS 只读检查 C#、Vue 或全部, 独立脚本默认 checkAll, 不自动修复. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    exit (Invoke-PbiEntry -Arguments $args -Action checkAll)
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if (Invoke-PbiGroup 'check' $action $options) { return }
if ($action -eq 'checkVue') {
    # lint 共用格式、脚本、模板、样式与类型门禁; all 内仅在输入一致时复用本轮成功.
    Invoke-PbiFrontendCommand $action
    return
}
foreach ($target in (Get-PbiFormatTargets $options.Scope)) {
    $lintReport = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-lint-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        # 风格与质量规则都有 suggestion 级别, 显式纳入 IDE0305、CA1873、SYSLIB1045 等建议.
        Invoke-PbiNative dotnet @('format', $target, '--verify-no-changes', '--no-restore', '--severity', 'info', '--report', $lintReport)
        # 没有自动修复器的诊断也必须阻止通过, 不只依赖 format 的退出码.
        $findings = @(Get-Content -LiteralPath $lintReport -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop | ForEach-Object { $_.FileChanges } | Where-Object { $_.DiagnosticId })
        if ($findings.Count) { Stop-Pbi 1 ('C# lint 仍有诊断: ' + (($findings.DiagnosticId | Sort-Object -Unique) -join ', ')) }
    } finally {
        if (Test-Path -LiteralPath $lintReport) { Remove-Item -LiteralPath $lintReport -Force }
    }
}
