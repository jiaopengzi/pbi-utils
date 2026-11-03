# FilePath    : pbi-utils\scripts\tests\mcp-write-positive.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 顺序执行 MCP 写入正向用例并将真实 TRX 展开结果逐工具绑定到持久清单.

<#
.SYNOPSIS 验证全部写工具的隔离正向业务, 保留每工具结果和本轮唯一证据.
.PARAMETER Configuration 测试构建配置; 默认 Release, 不还原或升级依赖.
.PARAMETER NoBuild 仅在同一源码已经完成相同配置构建时显式跳过编译.
.PARAMETER EvidenceRoot 证据父目录; 每轮创建唯一子目录, 不读取历史报告替代新执行.
.OUTPUTS 输出本轮证据路径并返回首个失败退出码; 缺失/跳过/失败展开均不能算正向成功.
.NOTES 外部连接、认证、HTTP、选择器和浏览器采用受控适配器; 不代表真实 Desktop/云验收.
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$EvidenceRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runId = 'mcp-write-positive-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$evidence = Join-Path ([IO.Path]::GetFullPath($EvidenceRoot)) $runId
$null = New-Item -ItemType Directory -Path $evidence
$manifestPath = Join-Path $root 'tests/contracts/mcp-write-cases.json'
$coveragePath = Join-Path $root 'tests/contracts/mcp-tool-cases.json'
$projects = @(
    @{ project = 'tests/PbiUtils.UnitTests/PbiUtils.UnitTests.csproj'; id = 'unit'; filter = 'FullyQualifiedName~PbiUtils.UnitTests.McpWrite' },
    @{ project = 'tests/PbiUtils.Cli.Tests/PbiUtils.Cli.Tests.csproj'; id = 'cli'; filter = 'FullyQualifiedName~PbiUtils.Cli.Tests.McpBusinessTests|FullyQualifiedName~PbiUtils.Cli.Tests.McpToolCoverageTests|FullyQualifiedName~PbiUtils.Cli.Tests.McpCatalogWriteTests' }
)

<#
.SYNOPSIS 验证正向清单与权威写集合完全相等, 每项绑定固定项目和明确的测试展开.
.PARAMETER Manifest 写入正向清单对象.
.PARAMETER Coverage 全工具权限清单对象.
.PARAMETER Projects 本脚本固定运行的项目列表.
.OUTPUTS 无输出; 重复、缺失、越界或空证据声明抛出错误.
#>
function Assert-WriteManifest($Manifest, $Coverage, $Projects) {
    if ($Manifest.schemaVersion -ne 1 -or $Coverage.schemaVersion -ne 1) { throw '不支持的 MCP 测试清单版本' }
    $cases = @($Manifest.cases)
    $names = @($cases.name | Sort-Object -CaseSensitive)
    $writes = @($Coverage.cases | Where-Object { $_.readOnly -eq $false } | ForEach-Object name | Sort-Object -CaseSensitive)
    if (!$names.Count -or @($names | Select-Object -Unique).Count -ne $names.Count -or
        @($writes | Select-Object -Unique).Count -ne $writes.Count -or (Compare-Object $writes $names -CaseSensitive)) {
        throw '正向清单必须与全工具清单的写集合逐项相等且无重复'
    }
    foreach ($case in $cases) {
        if ($case.readOnly -ne $false -or $case.project -cnotin @($Projects.project) -or
            $case.testMethod -cnotmatch '^PbiUtils\.(UnitTests\.McpWrite|Cli\.Tests\.Mcp(Business|ToolCoverage|CatalogWrite))\w*\.\w+$' -or
            @($case.expectedCases).Count -eq 0 -or @($case.assertions).Count -eq 0 -or [string]::IsNullOrWhiteSpace($case.validationLevel)) {
            throw ('正向清单缺少有效的项目、测试、展开或断言: ' + $case.name)
        }
        $prefix = if ($case.project -ceq $Projects[0].project) { 'PbiUtils.UnitTests.' } else { 'PbiUtils.Cli.Tests.' }
        if (!$case.testMethod.StartsWith($prefix, [StringComparison]::Ordinal)) { throw ('项目与测试类不匹配: ' + $case.name) }
        foreach ($suffix in $case.expectedCases) {
            if ($suffix -isnot [string] -or ($suffix -ne '' -and $suffix -cnotmatch '^\([^\r\n]+\)$')) { throw ('测试展开必须为明确的显示名尾缀: ' + $case.name) }
        }
        if (@($case.expectedCases | Select-Object -Unique).Count -ne @($case.expectedCases).Count) { throw ('重复测试展开: ' + $case.name) }
    }
}

<#
.SYNOPSIS 将真实 TRX 测试 ID 映射为精确类/方法和显示名, 不用子串匹配认定正向执行.
.PARAMETER Report 本轮新生成的 TRX 文件.
.OUTPUTS 每个真实结果的完整方法、展开显示名及 outcome; 重复定义或孤立结果明确失败.
#>
function Read-WriteTestRows([string]$Report) {
    [xml]$document = Get-Content -LiteralPath $Report -Raw
    $definitions = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($definition in @($document.TestRun.TestDefinitions.UnitTest)) {
        $id = [string]$definition.id
        if (!$id -or $definitions.ContainsKey($id)) { throw 'TRX 测试定义缺少唯一 ID' }
        $definitions.Add($id, $definition)
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in @($document.TestRun.Results.UnitTestResult)) {
        $id = [string]$result.testId
        if (!$definitions.ContainsKey($id) -or !$seen.Add($id)) { throw 'TRX 存在无定义或重复执行的结果' }
        $definition = $definitions[$id]
        $qualified = [string]$definition.TestMethod.className + '.' + [string]$definition.TestMethod.name
        if ([string]$definition.name -cne [string]$result.testName) { throw 'TRX 定义与结果显示名不一致' }
        [pscustomobject]@{ testMethod = $qualified; displayName = [string]$result.testName; outcome = [string]$result.outcome }
    }
}

<#
.SYNOPSIS 逐工具核验事实或理论展开, 一个方法共享多个工具时只认定各自明确列出的参数分支.
.PARAMETER Case 正向清单中的单个工具映射.
.PARAMETER Rows 本项目当前 TRX 的全部结果.
.PARAMETER Executed 是否实际启动过本项目测试.
.OUTPUTS 具有 PASS/FAIL/UNRUN、实际匹配及失败原因的逐工具对象.
#>
function Get-WriteCaseResult($Case, $Rows, [bool]$Executed) {
    $methodRows = @($Rows | Where-Object { $_.testMethod -ceq $Case.testMethod })
    $expected = @($Case.expectedCases | ForEach-Object { $Case.testMethod + $_ })
    $matched = @($methodRows | Where-Object { $_.displayName -cin $expected })
    $missing = @($expected | Where-Object { $_ -cnotin @($matched | ForEach-Object displayName) })
    $bad = @($methodRows | Where-Object { $_.outcome -cne 'Passed' })
    $status = if (!$Executed) { 'UNRUN' } elseif ($missing.Count -or $matched.Count -ne $expected.Count -or $bad.Count) { 'FAIL' } else { 'PASS' }
    [pscustomobject][ordered]@{
        name = $Case.name; status = $status; project = $Case.project; testMethod = $Case.testMethod
        expectedCases = $expected; results = $matched; missing = $missing; nonPassingMethodCases = $bad
        assertions = $Case.assertions; validationLevel = $Case.validationLevel
    }
}

$exitCode = 0
$failure = $null
$manifest = $null
$projectRuns = [Collections.Generic.List[object]]::new()
$toolResults = [Collections.Generic.List[object]]::new()
Push-Location -LiteralPath $root
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $coverage = Get-Content -LiteralPath $coveragePath -Raw | ConvertFrom-Json -AsHashtable
    Assert-WriteManifest $manifest $coverage $projects
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $evidence 'mcp-write-cases.json')
    Import-Module (Join-Path $root 'scripts/CommandRegistry.psm1') -Force
    Import-Module (Join-Path $root 'scripts/TestResults.psm1') -Force
    Assert-PbiTools @('dotnet')
    Assert-PbiRestoreAssets 'Core'
    foreach ($project in $projects) {
        $run = [ordered]@{ project = $project.project; status = 'UNRUN'; exitCode = $null; rows = @(); failure = $null; report = $null; executed = $false }
        if ($exitCode -eq 0) {
            $reportName = $project.id + '.trx'
            $report = Join-Path $evidence $reportName
            if (Test-Path -LiteralPath $report) { throw '本轮 TRX 已存在, 禁止读取历史结果' }
            $run.report = $report
            $arguments = @('test', $project.project, '--no-restore', '--nologo', '-c', $Configuration,
                '--filter', $project.filter, '--logger', "trx;LogFileName=$reportName", '--results-directory', $evidence)
            if ($NoBuild) { $arguments += '--no-build' }
            $started = [DateTimeOffset]::UtcNow
            $run.executed = $true
            & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence ($project.id + '.log')) | Out-Host
            $run.exitCode = $LASTEXITCODE
            if ($run.exitCode -ne 0) { $exitCode = $run.exitCode }
            try {
                $run.rows = @(Read-WriteTestRows $report)
                $facts = Assert-PbiTestResults $report $started
                if ($facts.Skipped -ne 0) { throw '正向专项不接受跳过的测试展开' }
                # 理论完整集合必须等于所有相关工具声明的展开并集, 避免遗漏一个分支而仍宣布覆盖.
                foreach ($group in @($manifest.cases | Where-Object { $_.project -ceq $project.project } | Group-Object testMethod)) {
                    $expected = @($group.Group | ForEach-Object { $entry = $_; $entry.expectedCases | ForEach-Object { $entry.testMethod + $_ } } | Sort-Object -Unique -CaseSensitive)
                    $actual = @($run.rows | Where-Object { $_.testMethod -ceq $group.Name } | ForEach-Object displayName | Sort-Object -CaseSensitive)
                    if (!$actual.Count -or (Compare-Object $expected $actual -CaseSensitive)) { throw ('测试展开集合漂移或缺失: ' + $group.Name) }
                }
                if ($run.exitCode -ne 0) { throw ('测试进程返回非零退出码: ' + $run.exitCode) }
                $run.status = 'PASS'
            }
            catch {
                $run.status = 'FAIL'
                $run.failure = $_.Exception.Message
                if ($exitCode -eq 0) { $exitCode = 1 }
            }
        }
        $projectRuns.Add([pscustomobject]$run)
    }
}
catch {
    $failure = $_.Exception.Message
    if ($exitCode -eq 0) { $exitCode = if ($_.Exception.Data.Contains('ExitCode')) { [int]$_.Exception.Data['ExitCode'] } else { 1 } }
}
finally {
    Pop-Location
    if ($null -ne $manifest) {
        foreach ($case in $manifest.cases) {
            $runs = @($projectRuns | Where-Object { $_.project -ceq $case.project })
            $rows = if ($runs.Count -eq 1) { @($runs[0].rows) } else { @() }
            $executed = $runs.Count -eq 1 -and $runs[0].executed
            $toolResults.Add((Get-WriteCaseResult $case $rows $executed))
        }
    }
    $passed = @($toolResults | Where-Object status -eq 'PASS').Count
    $failed = @($toolResults | Where-Object status -eq 'FAIL').Count
    $unrun = @($toolResults | Where-Object status -eq 'UNRUN').Count
    if (($failed -gt 0 -or $unrun -gt 0 -or $toolResults.Count -eq 0) -and $exitCode -eq 0) { $exitCode = 1 }
    $summary = [ordered]@{
        schemaVersion = 1; runId = $runId; configuration = $Configuration; noBuild = [bool]$NoBuild
        status = $(if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }); exitCode = $exitCode; failure = $failure
        total = $toolResults.Count; passed = $passed; failed = $failed; unrun = $unrun
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        coverageSha256 = (Get-FileHash -LiteralPath $coveragePath -Algorithm SHA256).Hash
        realDesktopCommit = 'UNRUN'; realCloudAuthentication = 'UNRUN'; realExternalBrowser = 'UNRUN'; physicalOtherMachine = 'UNRUN'
        projects = $projectRuns; cases = $toolResults
    }
    $summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidence 'write-positive-summary.json') -Encoding utf8
    Write-Host ("MCP 写入正向: {0}; PASS={1}, FAIL={2}, UNRUN={3}; 证据: {4}" -f $summary.status, $passed, $failed, $unrun, $evidence)
    if ($failure) { Write-Host $failure }
}
exit $exitCode
