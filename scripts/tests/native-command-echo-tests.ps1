# FilePath    : pbi-utils\scripts\tests\native-command-echo-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 用真实原生子进程验证 pnpm 正常脚本回显的显示通道, 保留其他诊断和原始退出码.

<# .SYNOPSIS 执行隔离输出场景, 不依赖 Node/pnpm 或网络, 不修改真实项目 package.json. #>
param([ValidateSet('success', 'failure', 'unknown', 'other', 'invalid', 'cancel')][string]$Scenario = 'success')

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../CommandRegistry.psm1') -Force
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-native-echo-' + [Guid]::NewGuid().ToString('N'))
$originalPwsh = $env:PBI_NATIVE_ECHO_PWSH
$originalCode = $env:PBI_NATIVE_ECHO_CODE
$code = 4
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'web') -Force
    $package = if ($Scenario -eq 'invalid') { '{invalid' } elseif ($Scenario -eq 'unknown') {
        '{"scripts":{"test":"another command"}}'
    } else { '{"scripts":{"lint:styles":"node scripts/check-styles.ts","typecheck":"vue-tsc --noEmit"}}' }
    [IO.File]::WriteAllText((Join-Path $sandbox 'web/package.json'), $package)
    @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::Out.WriteLine('OUT-before')
[Console]::Error.WriteLine('$ node scripts/check-styles.ts')
[Console]::Error.WriteLine('real native stderr diagnostic 1')
[Console]::Error.WriteLine('$ vue-tsc --noEmit')
[Console]::Error.WriteLine('real native stderr diagnostic 2')
[Console]::Error.WriteLine('$ unknown stderr text')
[Console]::Out.WriteLine('OUT-after')
exit ([int]$env:PBI_NATIVE_ECHO_CODE)
'@ | Set-Content -LiteralPath (Join-Path $sandbox 'native-output.ps1') -Encoding utf8
    $batch = "@echo off`r`n`"%PBI_NATIVE_ECHO_PWSH%`" -NoProfile -File `"%~dp0native-output.ps1`"`r`nexit /b %errorlevel%`r`n"
    $tool = Join-Path $sandbox $(if ($Scenario -eq 'other') { 'other.cmd' } else { 'pnpm.cmd' })
    [IO.File]::WriteAllText($tool, $batch, [Text.Encoding]::ASCII)
    $env:PBI_NATIVE_ECHO_PWSH = (Get-Process -Id $PID).Path
    $env:PBI_NATIVE_ECHO_CODE = if ($Scenario -in @('failure', 'invalid')) { '47' } else { '0' }
    $code = & (Get-Module CommandRegistry) {
        param($FixtureRoot, $NativeTool, $FixtureScenario)
        $script:Root = $FixtureRoot
        if ($FixtureScenario -eq 'cancel') {
            # 模拟读取可选回显配置时取消, 不能吞掉取消后继续启动原生工具.
            function script:Get-Content { throw [OperationCanceledException]::new('fixture cancellation') }
        }
        # 仅替换本隔离进程中的注册动作, 仍经过真实入口、原生流处理及退出边界.
        function Invoke-PbiAction([string]$Name, [hashtable]$Options) {
            Invoke-PbiNative $NativeTool @('--dir', 'web', 'lint:styles')
        }
        Invoke-PbiEntry -Arguments @('-Choice', 'help')
    } $sandbox $tool $Scenario
} finally {
    [Environment]::SetEnvironmentVariable('PBI_NATIVE_ECHO_PWSH', $originalPwsh, 'Process')
    [Environment]::SetEnvironmentVariable('PBI_NATIVE_ECHO_CODE', $originalCode, 'Process')
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($absolute) -ine $temporaryRoot -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-native-echo-[0-9a-f]{32}$') { throw '原生输出测试目录越界' }
    if (Test-Path -LiteralPath $absolute) {
        $items = @((Get-Item -LiteralPath $absolute -Force)) + @(Get-ChildItem -LiteralPath $absolute -Force -Recurse)
        if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw '原生输出测试目录含链接' }
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
exit $code
