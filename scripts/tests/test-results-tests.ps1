# FilePath    : pbi-utils\scripts\tests\test-results-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 以隔离 TRX 回执验证 C# 测试真实执行, 失败保护和非零退出码优先.

<# .SYNOPSIS 只替换原生测试工具, 真实执行 test.ps1 与报告解析, 不编译或运行产品测试. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-test-results-' + [Guid]::NewGuid().ToString('N'))
$savedLocation = Get-Location
$savedEncoding = [Console]::OutputEncoding
$script:Scenario = 'pass'
$script:Messages = [Collections.Generic.List[object]]::new()
$script:ReportNames = [Collections.Generic.List[string]]::new()
$script:Failures = [Collections.Generic.List[string]]::new()

<# .SYNOPSIS 固定断言失败保留原因, 不把预期失败误算为通过. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 模拟唯一注册测试项目, 避免调用真实 SDK 或其他测试. #>
function Get-PbiProjects([string]$Scope) {
    if ($script:Scenario -eq 'emptyProjects') { return @() }
    return 'tests/Audit.Tests'
}
<# .SYNOPSIS 让真实 test.ps1 执行 C# 单语言分支. #>
function Invoke-PbiGroup { return $false }
<# .SYNOPSIS 保留实际消息等级和覆盖事实. #>
function Write-PbiMessage([string]$Message, [string]$Level = 'Info') { $script:Messages.Add(@{ Message = $Message; Level = $Level }) }
<# .SYNOPSIS 复用正式异常退出码契约, 原生非零必须优先. #>
function Stop-Pbi([int]$Code, [string]$Message) {
    $failure = [InvalidOperationException]::new($Message)
    $failure.Data['ExitCode'] = $Code
    throw $failure
}

<# .SYNOPSIS 构造与实际 VSTest TRX 一致的最小回执; 仅固定场景可改变结构和计数. #>
function New-ProbeReport([string]$Scenario) {
    $time = [DateTimeOffset]::UtcNow
    if ($Scenario -eq 'stale') { $time = $time.AddHours(-1) }
    $start = $time.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    $finish = $time.AddMilliseconds(1).ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    $total = if ($Scenario -eq 'zero') { 0 } elseif ($Scenario -in @('mixed', 'mismatchedSkipWarning')) { 2 } else { 1 }
    $executed = if ($Scenario -in @('zero', 'skip')) { 0 } else { 1 }
    $passed = if ($Scenario -in @('zero', 'skip', 'failed')) { 0 } else { 1 }
    $failed = if ($Scenario -eq 'failed') { 1 } else { 0 }
    $outcome = if ($Scenario -eq 'failed') { 'Failed' } else { 'Completed' }
    $counters = "<Counters total=`"$total`" executed=`"$executed`" passed=`"$passed`" failed=`"$failed`" error=`"0`" timeout=`"0`" aborted=`"0`" inconclusive=`"0`" passedButRunAborted=`"0`" notRunnable=`"0`" notExecuted=`"0`" disconnected=`"0`" warning=`"0`" completed=`"0`" inProgress=`"0`" pending=`"0`" />"
    $result = if ($Scenario -eq 'zero') { '' } elseif ($Scenario -eq 'skip') { '<UnitTestResult outcome="NotExecuted" />' }
        elseif ($Scenario -eq 'failed') { '<UnitTestResult outcome="Failed" />' } else { '<UnitTestResult outcome="Passed" />' }
    if ($Scenario -in @('mixed', 'mismatchedSkipWarning')) { $result += '<UnitTestResult testName="GateAudit.Skipped" outcome="NotExecuted"><Output><ErrorInfo><Message>legitimate xUnit skip reason</Message></ErrorInfo></Output></UnitTestResult>' }
    $info = switch ($Scenario) {
        'adapter' { '<RunInfos><RunInfo outcome="Error"><Text>adapter diagnostic</Text></RunInfo></RunInfos>' }
        'platformWarning' { '<RunInfos><RunInfo outcome="Warning"><Text>test adapter could not be loaded</Text></RunInfo></RunInfos>' }
        'fakeSkipWarning' { '<RunInfos><RunInfo outcome="Warning"><Text>[xUnit.net 00:00:00.12]     GateAudit.Skipped [SKIP]</Text></RunInfo></RunInfos>' }
        'mismatchedSkipWarning' { '<RunInfos><RunInfo outcome="Warning"><Text>[xUnit.net 00:00:00.12]     GateAudit.Other [SKIP]</Text></RunInfo></RunInfos>' }
        'mixed' { '<RunInfos><RunInfo outcome="Warning"><Text>[xUnit.net 00:00:00.12]     GateAudit.Skipped [SKIP]</Text></RunInfo></RunInfos>' }
        default { '' }
    }
    $output = if ($Scenario -eq 'textError') { '<Output><StdOut>error is an asserted business value</StdOut></Output>' } else { '' }
    $summary = "<ResultSummary outcome=`"$outcome`">$counters$output$info</ResultSummary>"
    $xml = "<TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Times start=`"$start`" finish=`"$finish`" /><Results>$result</Results>$summary</TestRun>"
    switch ($Scenario) {
        'malformed' { return '<TestRun>' }
        'wrongNamespace' { return $xml.Replace('http://microsoft.com/schemas/VisualStudio/TeamTest/2010', 'urn:unknown') }
        'missingSummary' { return $xml.Replace($summary, '') }
        'duplicateSummary' { return $xml.Replace($summary, $summary + $summary) }
        'duplicateCounters' { return $xml.Replace($counters, $counters + $counters) }
        'negative' { return $xml.Replace('total="1"', 'total="-1"') }
        'notNumber' { return $xml.Replace('executed="1"', 'executed="one"') }
        'inconsistent' { return $xml.Replace('total="1"', 'total="2"') }
        'badOutcome' { return $xml.Replace('outcome="Passed"', 'outcome="Unknown"') }
        'summaryFailure' { return $xml.Replace('outcome="Completed"', 'outcome="Failed"') }
        'inProgress' { return $xml.Replace('inProgress="0"', 'inProgress="1"') }
        'resultError' { return $xml.Replace('<UnitTestResult outcome="Passed" />', '<UnitTestResult outcome="Passed"><Output><ErrorInfo><Message>structured error</Message></ErrorInfo></Output></UnitTestResult>') }
        'dtd' { return '<!DOCTYPE TestRun [<!ENTITY probe "forbidden">]>' + $xml }
        default { return $xml }
    }
}

<# .SYNOPSIS 模拟工具 exit0 并写本轮指定报告, 可控注入缺失、过期和结构化失败; 非零不产回执. #>
function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
    Assert ($File -eq 'dotnet' -and $Arguments[0] -eq 'test' -and $Arguments -contains '--no-restore') '测试不得改用其他工具或隐式还原'
    if ($script:Scenario -eq 'nativeFailure') { Stop-Pbi 47 'native failure wins' }
    $directory = [IO.Path]::GetFullPath($Arguments[[Array]::IndexOf($Arguments, '--results-directory') + 1], (Get-Location).Path)
    $logger = $Arguments[[Array]::IndexOf($Arguments, '--logger') + 1]
    $name = if ($logger -match '^trx;LogFileName=(.+\.trx)$') { $Matches[1] } else { 'baseline.trx' }
    $script:ReportNames.Add($name)
    if ($script:Scenario -eq 'missing') { return }
    $null = New-Item -ItemType Directory -Path $directory -Force
    [IO.File]::WriteAllText((Join-Path $directory $name), (New-ProbeReport $script:Scenario), [Text.UTF8Encoding]::new($false))
}

<# .SYNOPSIS 执行真实测试脚本并比较是否失败, 不以错误文字搜索代替结构化回执. #>
function Test-Scenario([string]$Scenario, [bool]$Rejected) {
    $script:Scenario = $Scenario
    $script:Messages.Clear()
    $PbiInvocation = @{ Action = 'testC#'; Options = @{ Scope = 'Core'; Configuration = 'Release' } }
    $caught = $null
    try { . (Join-Path $sourceRoot 'scripts/test.ps1') } catch { $caught = $_.Exception }
    if ($Rejected -and $null -eq $caught) { $script:Failures.Add("$Scenario 未被门禁拒绝") }
    elseif (!$Rejected -and $null -ne $caught) { $script:Failures.Add("$Scenario 正常回执被拒绝: $($caught.Message)") }
    if ($Scenario -eq 'nativeFailure') {
        if ($null -eq $caught -or $caught.Data['ExitCode'] -ne 47 -or $caught.Message -ne 'native failure wins') { $script:Failures.Add('原生非零退出码或原因被报告检查覆盖') }
    }
    if ($Scenario -eq 'mixed' -and @($script:Messages | Where-Object { $_.Level -eq 'Warning' -and $_.Message -match 'UNRUN' -and $_.Message -match '1' }).Count -ne 1) {
        $script:Failures.Add('部分跳过必须保留为 UNRUN 并明确报告数量')
    }
}

try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = New-Item -ItemType Directory -Path $sandbox
    Set-Location -LiteralPath $sandbox
    foreach ($scenario in @('pass', 'textError', 'mixed')) { Test-Scenario $scenario $false }
    foreach ($scenario in @('zero', 'skip', 'failed', 'adapter', 'platformWarning', 'fakeSkipWarning', 'mismatchedSkipWarning', 'missing', 'stale', 'malformed', 'wrongNamespace', 'missingSummary', 'duplicateSummary', 'duplicateCounters', 'negative', 'notNumber', 'inconsistent', 'badOutcome', 'summaryFailure', 'inProgress', 'resultError', 'dtd', 'nativeFailure', 'emptyProjects')) {
        Test-Scenario $scenario $true
    }
    if (@($script:ReportNames | Select-Object -Unique).Count -ne $script:ReportNames.Count) { $script:Failures.Add('每次工具调用必须使用唯一报告名称, 不得消费旧 artifact') }
    if ($script:Failures.Count) { throw ($script:Failures -join '; ') }
    Write-Output 'PASS C# 测试回执与实际执行门禁'
}
finally {
    [Console]::OutputEncoding = $savedEncoding
    Set-Location -LiteralPath $savedLocation.Path
    $absolute = [IO.Path]::GetFullPath($sandbox)
    if ($absolute.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($absolute) -match '^pbi-utils-test-results-[0-9a-f]{32}$' -and [IO.Directory]::Exists($absolute)) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
