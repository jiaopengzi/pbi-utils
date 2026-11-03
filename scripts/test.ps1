# FilePath    : pbi-utils\scripts\test.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 执行前端, 共享核心, CLI, 脚本和所选 Windows 范围测试.

<# .SYNOPSIS 测试 C#、Vue 或全部, 独立脚本默认 testAll, C# 测试无需 Node/pnpm. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    exit (Invoke-PbiEntry -Arguments $args -Action testAll)
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if (Invoke-PbiGroup 'test' $action $options) { return }
if ($action -eq 'testVue') { Invoke-PbiFrontendCommand $action; return }
Import-Module (Join-Path $PSScriptRoot 'TestResults.psm1') -Force
$testProjects = @(Get-PbiProjects $options.Scope | Where-Object { $_ -like 'tests/*' })
if (!$testProjects.Count) { Stop-Pbi 1 '当前范围没有注册 C# 测试项目, 未执行测试' }
foreach ($project in $testProjects) {
    $resultsDirectory = [IO.Path]::GetFullPath("artifacts/test-results/$($project.Split('/')[-1])", (Get-Location).Path)
    $reportName = 'test-' + [Guid]::NewGuid().ToString('N') + '.trx'
    $reportPath = Join-Path $resultsDirectory $reportName
    if (Test-Path -LiteralPath $reportPath) { Stop-Pbi 1 '本轮 C# 测试报告路径已经存在, 未消费历史报告' }
    $startedAt = [DateTimeOffset]::UtcNow
    # 原生非零先由统一边界失败, 不读回执或覆盖原始退出码与所属动作.
    Invoke-PbiNative dotnet @('test', $project, '--no-restore', '--nologo', '-c', $options.Configuration, '--logger', "trx;LogFileName=$reportName", '--results-directory', $resultsDirectory)
    try { $facts = Assert-PbiTestResults $reportPath $startedAt }
    catch { Stop-Pbi 1 "C# 测试报告未通过核验: $($_.Exception.Message)" }
    if ($facts.Skipped -gt 0) {
        Write-PbiMessage "$project`: 实际执行 $($facts.Executed)/$($facts.Total), 通过 $($facts.Passed); 跳过 $($facts.Skipped) 项 (UNRUN)" 'Warning'
    } else {
        Write-PbiMessage "$project`: 已核验本轮实际执行并通过 $($facts.Passed) 项" 'Success'
    }
}
