# FilePath    : pbi-utils\scripts\tests\encoding.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 用真实 PowerShell 子进程验证旧代码页下的输出通道, 原生 ANSI 与入口编码恢复.

param([string]$Scenario, [int]$CodePage)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../CommandRegistry.psm1') -Force
$pwsh = (Get-Process -Id $PID).Path
$originalInput = [Console]::InputEncoding
$originalOutput = [Console]::OutputEncoding
$originalPipe = $OutputEncoding
try {
    [Console]::InputEncoding = [Text.Encoding]::GetEncoding($CodePage)
    [Console]::OutputEncoding = [Text.Encoding]::GetEncoding($CodePage)
    $OutputEncoding = [Text.Encoding]::ASCII
    $code = & (Get-Module CommandRegistry) {
        param($Pwsh, $Scenario)
        <# .SYNOPSIS 注入测试动作, 真实执行原生输出并模拟取消, 保留入口及其错误边界. #>
        function Invoke-PbiAction([string]$Name, [hashtable]$Options) {
            # 非 dotnet 工具输出相同句子仍原样保留, 不对真实无法还原诊断做替换.
            $command = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::Out.WriteLine("已成功生成。0 个警告。`n    0 个错误`n    2 个警告`n所有项目均是最新的，无法还原。`n  `e[32m➜`e[39m Local: 世界")
[Console]::Error.WriteLine("诊断：中文错误流`nBuild succeeded. 中文原始错误流`n无法还原依赖: 中文原始诊断")
'@
            $command += "`nexit " + $(if ($Scenario -eq 'failure') { '47' } else { '0' })
            Invoke-PbiNative $Pwsh @('-NoProfile', '-Command', $command)
            if ($Scenario -eq 'cancel') { throw [OperationCanceledException]::new('测试取消') }
        }
        Invoke-PbiEntry -Arguments @('-Choice', $(if ($Scenario -eq 'invalid') { 'unknown' } else { 'help' }))
    } $pwsh $Scenario
    if ([Console]::InputEncoding.CodePage -ne $CodePage -or [Console]::OutputEncoding.CodePage -ne $CodePage -or $OutputEncoding.CodePage -ne 20127) {
        throw '入口未恢复调用者编码'
    }
}
finally {
    [Console]::InputEncoding = $originalInput
    [Console]::OutputEncoding = $originalOutput
    $OutputEncoding = $originalPipe
}
[Console]::Error.WriteLine('ENCODING_RESTORED')
exit $code
