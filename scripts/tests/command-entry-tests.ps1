# FilePath    : pbi-utils\scripts\tests\command-entry-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在一个 PowerShell 进程内验证每组真实开发入口的拒绝边界.

$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$rootEntry = Join-Path $repository 'run.ps1'

<#
.SYNOPSIS 调用真实脚本入口并逐次核对脚本退出码, 可额外要求 stdout 为空.
.PARAMETER Entry 待验证的根入口或独立动作脚本路径.
.PARAMETER Arguments 原始位置参数, 保留空字符串和重复参数.
.PARAMETER RequireEmptyOutput 要求脚本的 stdout 和 PowerShell 输出均为空.
.OUTPUTS 无. 违反拒绝契约时抛出包含具体入口与参数的错误.
#>
function Assert-RejectedEntry([string]$Entry, [string[]]$Arguments, [switch]$RequireEmptyOutput) {
    $originalOutput = [Console]::Out
    $originalError = [Console]::Error
    $originalCode = $global:LASTEXITCODE
    $output = [IO.StringWriter]::new()
    $errorOutput = [IO.StringWriter]::new()
    try {
        [Console]::SetOut($output)
        [Console]::SetError($errorOutput)
        # 脚本 exit 只结束本次调用. 清空上一条退出码, 避免未执行 exit 的入口误通过.
        $global:LASTEXITCODE = $null
        $pipelineOutput = @(& $Entry @Arguments 6>&1)
        $code = $global:LASTEXITCODE
        if ($code -ne 2 -or ($RequireEmptyOutput -and ($output.GetStringBuilder().Length -or $pipelineOutput.Count))) {
            $case = ConvertTo-Json -InputObject $Arguments -Compress
            throw "拒绝契约失败 ($(Split-Path $Entry -Leaf) $case, 实际退出码 $code): $output $pipelineOutput $errorOutput"
        }
    } finally {
        [Console]::SetOut($originalOutput)
        [Console]::SetError($originalError)
        $global:LASTEXITCODE = $originalCode
        $output.Dispose()
        $errorOutput.Dispose()
    }
}

$invalidArguments = @(
    @('-NonInteractive'),
    @('-Choice', ''),
    @('-Choice', 'unknown'),
    @('-Choice', 'help', '-Scope', 'Core'),
    @('-Choice', 'testCore', '-Scope', 'Core'),
    @('-Choice', 'setupC#', '-Configuration', 'Debug'),
    @('-Choice', 'buildVue', '-Scope', 'Core'),
    @('-Choice', 'testVue', '-Configuration', 'Debug'),
    @('-Choice', 'buildC#', '-Configuration'),
    @('-Choice', 'setupAll', '-Scope', '-Unknown'),
    @('-Choice', 'hello', '-Name'),
    @('-Choice', 'publish', '-Runtime', 'win-x64'),
    @('-List', '-Choice', 'help'),
    @('-Help', '-Json'),
    @('-Choice', 'help', '-Unknown'),
    @('-Choice', 'buildC#', '-AfterCompile', 'callback'),
    @('-Choice', 'buildC#', '-BeforeDelivery', 'callback'),
    @('-Choice', 'verifyAndBuild', '-DotNetMode', 'both'),
    @('-Choice', 'buildC#', '-DotNetMode'),
    @('-Choice', 'testC#', '-DotNetMode', 'without-dotnet'),
    @('-Choice', 'testAll', '-DotNetMode', 'with-dotnet'),
    @('-Choice', 'buildVue', '-DotNetMode', 'with-dotnet'),
    @('-Choice', 'package', '-Scope', 'All'),
    @('-Choice', 'release', '-Configuration', 'Debug')
)
foreach ($argumentSet in $invalidArguments) {
    Assert-RejectedEntry $rootEntry $argumentSet
}

$retiredActions = @('setup', 'build', 'testCore', 'format', 'check', 'hello', 'agentHello', 'doctor', 'publish', '-1', '22', '28')
foreach ($action in $retiredActions) {
    Assert-RejectedEntry $rootEntry @('-Choice', $action) -RequireEmptyOutput
}

$lowerScripts = @('setup', 'build', 'test', 'check', 'format', 'clean', 'dev', 'package', 'lane')
foreach ($scriptName in $lowerScripts) {
    Assert-RejectedEntry (Join-Path $repository "scripts/$scriptName.ps1") @('-Unknown')
}
Assert-RejectedEntry (Join-Path $repository 'scripts/test.ps1') @('-Scope', 'Core', '-Scope', 'All')
Assert-RejectedEntry (Join-Path $repository 'scripts/build.ps1') @('-AfterCompile', 'callback')

# 内部并行通道只接受允许列表阶段和本轮门禁目录内的新结果文件; 每例至少违反一项, 不会启动真实工具.
$gateDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-gate-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $gateDirectory
try {
    $result = Join-Path $gateDirectory ([Guid]::NewGuid().ToString('N') + '.json')
    $laneArguments = @(
        @('-Lane', 'bogus', '-Result', $result),
        @('-Lane', 'CHECKVUE', '-Result', $result),
        @('-Lane', 'checkVue'),
        @('-Lane', 'checkVue', '-Result', (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.json'))),
        @('-Lane', 'checkVue', '-Result', (Join-Path $gateDirectory 'result.json')),
        @('-Lane', 'checkVue', '-Result', $result, '-Scope', 'All'),
        @('-Lane', 'checkC#', '-Result', $result),
        @('-Lane', 'checkC#', '-Result', $result, '-Scope', 'All', '-Configuration', 'Release'),
        @('-Lane', 'testC#', '-Result', $result, '-Scope', 'All'),
        @('-Lane', 'testC#', '-Result', $result, '-Scope', 'Other', '-Configuration', 'Release'),
        @('-Lane', 'checkVue', '-Result', $result, '-Lane', 'testVue'),
        @('-Choice', 'checkVue')
    )
    foreach ($argumentSet in $laneArguments) {
        Assert-RejectedEntry (Join-Path $repository 'scripts/lane.ps1') $argumentSet -RequireEmptyOutput
    }
    if (Test-Path -LiteralPath $result) { throw '被拒绝的并行通道不得写入结果文件' }
} finally {
    Remove-Item -LiteralPath $gateDirectory -Recurse -Force
}

Write-Output ("PASS command entry rejections: {0} invalid arguments, {1} retired actions, {2} lower entries, {3} lane arguments" -f $invalidArguments.Count, $retiredActions.Count, ($lowerScripts.Count + 2), $laneArguments.Count)
