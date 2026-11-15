# FilePath    : pbi-utils\scripts\tests\entry-stop-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 以 PowerShell 管道停止验证根入口、全部下层入口与并行通道入口跳过 catch 时仍以 130 结束并写回取消结果.

<# .SYNOPSIS 私有副本中的动作阻塞后停止管道; PowerShell.Stop 与控制台 Ctrl+C 走同一停止路径, 不依赖控制台信号. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-entry-stop-' + [Guid]::NewGuid().ToString('N'))
$gate = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-gate-' + [Guid]::NewGuid().ToString('N'))
$failures = [Collections.Generic.List[string]]::new()
$savedOutputEncoding = [Console]::OutputEncoding
# 与命令入口矩阵相同的下层入口; 每个入口脚本自行在 finally 中退出, 需逐个核验.
$lowerEntries = [ordered]@{ setup = @(); build = @(); test = @(); check = @(); format = @(); clean = @(); dev = @('-Target', 'Web'); package = @() }
$boundary = @'
<# .SYNOPSIS 写入就绪标记后阻塞, 等待测试停止管道; 不调用任何真实工具. #>
function Wait-PbiStopFixture {
    [IO.File]::WriteAllText($env:PBI_STOP_READY, 'ready')
    while ($true) { Start-Sleep -Milliseconds 50 }
}
<# .SYNOPSIS 根入口动作在开始后阻塞. #>
function Invoke-PbiAction([string]$Name, [hashtable]$Options, [scriptblock]$AfterCompile, [scriptblock]$BeforeDelivery) { Wait-PbiStopFixture }
<# .SYNOPSIS 通道主体在开始后阻塞. #>
function Invoke-PbiGateLaneBody([string]$Lane, [hashtable]$Options) { Wait-PbiStopFixture }
'@

<# .SYNOPSIS 收集断言失败, 不让第一个错误遮住其余边界.
   .PARAMETER Condition 应满足的事实.
   .PARAMETER Message 失败定位.
#>
function Assert-Stop([bool]$Condition, [string]$Message) {
    if (!$Condition) { $failures.Add($Message); [Console]::Error.WriteLine("FAIL $Message") }
}

<# .SYNOPSIS 在进程内运行空间调用入口脚本, 动作阻塞后停止管道.
   .PARAMETER Script 私有副本中的入口脚本.
   .PARAMETER Arguments 原样传给入口的参数.
   .OUTPUTS 停止后的 LASTEXITCODE 与该入口写入错误流的文本.
#>
function Invoke-StoppedEntry([string]$Script, [string[]]$Arguments) {
    $ready = Join-Path $sandbox ([Guid]::NewGuid().ToString('N') + '.ready')
    $env:PBI_STOP_READY = $ready
    $savedError = [Console]::Error
    $captured = [IO.StringWriter]::new()
    $powerShell = [PowerShell]::Create()
    try {
        [Console]::SetError($captured)
        $null = $powerShell.AddScript('param($Script, $Arguments) & $Script @Arguments').AddArgument($Script).AddArgument($Arguments)
        $invocation = $powerShell.BeginInvoke()
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (![IO.File]::Exists($ready) -and !$invocation.IsCompleted -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 20 }
        Assert-Stop ([IO.File]::Exists($ready)) "$([IO.Path]::GetFileName($Script)) 必须在停止前进入阻塞动作"
        $powerShell.Stop()
        return @{ Code = $powerShell.Runspace.SessionStateProxy.GetVariable('LASTEXITCODE'); Error = $captured.ToString() }
    }
    finally {
        [Console]::SetError($savedError)
        $powerShell.Dispose()
        Remove-Item Env:PBI_STOP_READY -ErrorAction SilentlyContinue
    }
}

try {
    # C# 包装器按 UTF-8 读取输出; CreateNoWindow 的全新 pwsh 可能初始使用系统代码页.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'scripts') -Force
    $null = New-Item -ItemType Directory -Path $gate -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'run.ps1') -Destination $sandbox
    foreach ($name in @($lowerEntries.Keys) + 'lane') { Copy-Item -LiteralPath (Join-Path $sourceRoot "scripts/$name.ps1") -Destination (Join-Path $sandbox 'scripts') }
    $registry = Get-Content -LiteralPath (Join-Path $sourceRoot 'scripts/CommandRegistry.psm1') -Raw
    [IO.File]::WriteAllText((Join-Path $sandbox 'scripts/CommandRegistry.psm1'), $registry.Replace('Export-ModuleMember -Function *-Pbi*', $boundary + "`nExport-ModuleMember -Function *-Pbi*"))
    $consoleEncoding = [Console]::OutputEncoding.WebName
    $checked = 0

    $root = Invoke-StoppedEntry (Join-Path $sandbox 'run.ps1') @('-Choice', 'checkVue', '-NonInteractive')
    Assert-Stop ($root.Code -eq 130) "根入口被停止后必须以 130 结束, 实际 $($root.Code)"
    Assert-Stop ($root.Error.Contains('已取消 (退出码 130).')) '根入口被停止后必须输出取消提示'
    Assert-Stop ([Console]::OutputEncoding.WebName -ceq $consoleEncoding) '停止后必须恢复控制台编码'
    $checked++

    foreach ($entry in $lowerEntries.GetEnumerator()) {
        $lower = Invoke-StoppedEntry (Join-Path $sandbox "scripts/$($entry.Key).ps1") $entry.Value
        Assert-Stop ($lower.Code -eq 130) "下层入口 $($entry.Key).ps1 被停止后必须以 130 结束, 实际 $($lower.Code)"
        Assert-Stop ($lower.Error.Contains('已取消 (退出码 130).')) "下层入口 $($entry.Key).ps1 被停止后必须输出取消提示"
        $checked++
    }

    $result = Join-Path $gate ([Guid]::NewGuid().ToString('N') + '.json')
    $lane = Invoke-StoppedEntry (Join-Path $sandbox 'scripts/lane.ps1') @('-Lane', 'testVue', '-Result', $result)
    Assert-Stop ($lane.Code -eq 130) "通道被停止后必须以 130 结束, 实际 $($lane.Code)"
    Assert-Stop ($lane.Error.Contains('[testVue] 已取消 (退出码 130).')) '通道取消提示必须带阶段前缀'
    $record = if ([IO.File]::Exists($result)) { Get-Content -LiteralPath $result -Raw | ConvertFrom-Json } else { $null }
    Assert-Stop ($null -ne $record -and $record.code -eq 130 -and $record.action -ceq 'testVue') '通道被停止后必须写回与退出码一致的取消结果'
    $checked++
    if (!$failures.Count) { [Console]::Out.WriteLine("PASS $checked 个入口脚本在管道停止时以 130 结束, 并行通道写回取消结果") }
}
finally {
    foreach ($path in @($sandbox, $gate)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
    [Console]::OutputEncoding = $savedOutputEncoding
}
if ($failures.Count) { exit 1 }
