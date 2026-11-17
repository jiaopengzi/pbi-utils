# FilePath    : pbi-utils\scripts\test.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 执行前端, 共享核心, CLI, 脚本和所选 Windows 范围测试.

<# .SYNOPSIS 测试 C#、Vue、开发脚本或全部, 独立脚本默认 testAll. C# 与脚本回归无需 Node/pnpm. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    # 真实 Ctrl+C 会中止赋值并跳过 catch: 预设的取消码 130 由本脚本 finally 退出, 模块只返回退出码而不调用 exit.
    $pbiExitCode = 130
    try { $pbiExitCode = Invoke-PbiEntry -Arguments $args -Action testAll }
    catch { $pbiExitCode = 4; [Console]::Error.WriteLine($_.ToString()) }
    finally { exit $pbiExitCode }
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if (Invoke-PbiGroup 'test' $action $options) { return }
if ($action -eq 'testVue') { Invoke-PbiFrontendCommand $action; return }
Import-Module (Join-Path $PSScriptRoot 'TestResults.psm1') -Force
$scriptTestsOnly = $action -eq 'testScripts'
$testProjects = @(
    if ($scriptTestsOnly) { $script:CliTestProject }
    else { Get-PbiProjects $options.Scope | Where-Object { $_ -like 'tests/*' } }
)
if (!$testProjects.Count) { Stop-Pbi 1 '当前范围没有注册 C# 测试项目, 未执行测试' }
$reportName = 'test-' + [Guid]::NewGuid().ToString('N') + '.trx'
$reportRoot = [IO.Path]::GetFullPath('artifacts/test-results', (Get-Location).Path)
$testRuns = foreach ($project in $testProjects) {
    $projectName = $project.Split('/')[-1]
    $reportPath = Join-Path $reportRoot "$projectName/$reportName"
    if (Test-Path -LiteralPath $reportPath) { Stop-Pbi 1 '本轮 C# 测试报告路径已经存在, 未消费历史报告' }
    @{ Project = $project; Name = $projectName; ReportPath = $reportPath }
}
$nodes = [Math]::Min(2, [Environment]::ProcessorCount)
$startedAt = [DateTimeOffset]::UtcNow
try {
    # 相同配置的依赖只由一个构建图编译, 单元和 CLI 可并行, Windows 桌面验证独立执行.
    $testArguments = @('msbuild', (Join-Path $PSScriptRoot 'CSharpTestProjects.proj'), '-nologo', '-tl:off', '-verbosity:minimal', "-maxcpucount:$nodes", '-nodeReuse:false',
        "-p:Configuration=$($options.Configuration)", "-p:PbiTestProjects=$($testRuns.Name -join '|')", "-p:PbiTestLogFile=$reportName")
    # 定向入口复用 CLI 项目的脚本用例和本轮 TRX, 完整测试仍执行项目中的全部用例.
    if ($scriptTestsOnly) { $testArguments += '-p:VSTestTestCaseFilter=FullyQualifiedName~PbiUtils.ScriptTests.' }
    Invoke-PbiNative dotnet $testArguments
}
catch {
    # 子节点的测试诊断可从其独立 TRX 查看; 原生非零仍直接传播, 不由报告裁决.
    foreach ($run in $testRuns) {
        if ([IO.File]::Exists($run.ReportPath)) { [Console]::Error.WriteLine($script:LinePrefix + "C# 测试回执: $($run.ReportPath)") }
    }
    throw
}
foreach ($run in $testRuns) {
    $project = $run.Project
    try { $facts = Assert-PbiTestResults $run.ReportPath $startedAt }
    catch { Stop-Pbi 1 "C# 测试报告未通过核验 ($project): $($_.Exception.Message)" }
    Write-PbiMessage "C# 测试回执 [$project]: $($run.ReportPath)"
    if ($facts.Skipped -gt 0) {
        Write-PbiMessage "$project`: 实际执行 $($facts.Executed)/$($facts.Total), 通过 $($facts.Passed); 跳过 $($facts.Skipped) 项 (UNRUN)" 'Warning'
    } else {
        Write-PbiMessage "$project`: 已核验本轮实际执行并通过 $($facts.Passed) 项" 'Success'
    }
}
