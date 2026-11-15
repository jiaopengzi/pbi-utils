# FilePath    : pbi-utils\scripts\GateWatchdog.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 并行门禁快速失败看护: 首个失败后立即终止本轮全部在途通道的进程树, 并核验通道结果记录.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS 读取通道子进程写入的结果记录.
.PARAMETER Path 主流程为该通道分配的结果文件.
.OUTPUTS 含 Code、Action、Message 的记录; 文件缺失或损坏返回 null, 由调用方按失败处理.
.NOTES 主流程汇总与看护判定共用此实现, 退出码 0 但没有有效结果时两者都按失败处理.
#>
function Read-PbiLaneResult([string]$Path) {
    if (![IO.File]::Exists($Path)) { return $null }
    try {
        $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
        try {
            $element = $document.RootElement
            return [pscustomobject]@{ Code = $element.GetProperty('code').GetInt32(); Action = $element.GetProperty('action').GetString(); Message = $element.GetProperty('message').GetString() }
        } finally { $document.Dispose() }
    }
    catch [Text.Json.JsonException], [Collections.Generic.KeyNotFoundException], [InvalidOperationException], [FormatException] {
        # 损坏结果不能证明通道成功, 返回空记录让成功退出码也按失败处理.
        return $null
    }
}

<#
.SYNOPSIS 终止本轮门禁启动的一个进程及其仍挂在其下的全部后代.
.PARAMETER Process 由本轮门禁启动并持有句柄的通道进程.
.NOTES 根进程已退出时仍调用: 持有的句柄保证 PID 不被复用, .NET 按父进程与启动时间找到其仍存活的子进程并终止.
       部分后代无法终止时只写诊断, 不替换门禁的首个失败.
#>
function Stop-PbiGateProcessTree([Diagnostics.Process]$Process) {
    try { $Process.Kill($true) }
    catch {
        $failure = $_.Exception
        while ($failure -is [Management.Automation.MethodInvocationException] -and $null -ne $failure.InnerException) { $failure = $failure.InnerException }
        [Console]::Error.WriteLine('并行门禁未能完全终止进程 ' + $Process.Id + ': ' + $failure.Message)
    }
}

<#
.SYNOPSIS 记录首个失败来源并立即终止全部通道进程树.
.PARAMETER State 门禁快速失败状态.
.PARAMETER Source 首个失败的通道名称; 主流程自身以非取消失败结束时为 main.
.OUTPUTS 本次调用首次记录失败来源时返回 $true; 已记录或取消排空已开始时返回 $false.
.NOTES 主流程与看护 runspace 可能同时调用, 以状态锁串行. 取消排空与终止在同一把锁中决定先后, 排空先开始时看护不再终止.
       仍在运行的通道先登记为已终止再发出终止; 已退出的通道保留真实结果, 只清理其仍存活的子进程.
#>
function Invoke-PbiGateAbort([hashtable]$State, [string]$Source) {
    [Threading.Monitor]::Enter($State.Lock)
    try {
        if ($Source -cne 'main' -and $State.Draining) { return $false }
        $first = $null -eq $State.Trigger
        if ($first) { $State.Trigger = $Source }
        foreach ($lane in $State.Lanes.ToArray()) {
            if (!$lane.Process.HasExited) { $State.Terminated[$lane.Name] = $true }
            Stop-PbiGateProcessTree $lane.Process
        }
        return $first
    }
    finally { [Threading.Monitor]::Exit($State.Lock) }
}

<#
.SYNOPSIS 看护循环, 每 100 毫秒检查通道进程; 首个失败立即终止其余通道.
.PARAMETER State 门禁快速失败状态; Stopping 置位后退出循环.
.NOTES 在独立 runspace 运行, 主流程执行 PowerShell 步骤时仍能及时终止通道; 主流程原生命令由主流程按持有的句柄自行终止.
       退出码 130 表示控制台取消, 由主流程按取消排空; 退出码 0 须有一致的结果记录, 否则与主流程汇总一样按失败处理.
#>
function Invoke-PbiGateWatchdog([hashtable]$State) {
    $passed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $reported = $null
    while (!$State.Stopping) {
        try {
            if ($null -eq $State.Trigger -and !$State.Draining) {
                foreach ($lane in $State.Lanes.ToArray()) {
                    if ($passed.Contains($lane.Name) -or !$lane.Process.HasExited) { continue }
                    $code = $lane.Process.ExitCode
                    if ($code -eq 130) { continue }
                    if ($code -eq 0) {
                        $record = Read-PbiLaneResult $lane.Result
                        if ($null -ne $record -and $record.Code -eq 0) { $null = $passed.Add($lane.Name); continue }
                    }
                    if (Invoke-PbiGateAbort $State $lane.Name) {
                        [Console]::Error.WriteLine("并行阶段 [$($lane.Name)] 失败 (退出码 $code), 立即终止其余在途阶段.")
                    }
                    break
                }
            }
        }
        catch {
            # 同一异常每轮都会重现, 只报告一次, 不刷屏也不掩盖门禁结果.
            if ($_.Exception.Message -cne $reported) {
                $reported = $_.Exception.Message
                [Console]::Error.WriteLine('并行门禁看护异常: ' + $reported)
            }
        }
        [Threading.Thread]::Sleep(100)
    }
}

<#
.SYNOPSIS 在独立 runspace 启动看护循环.
.PARAMETER State 门禁快速失败状态, 由主流程与看护共享.
.OUTPUTS 看护句柄, 含 PowerShell、Runspace 与异步结果; 创建失败时释放已分配资源并抛出.
.NOTES 异步启动后的模块加载失败由主流程检查点按看护未运行处理.
#>
function Start-PbiGateWatchdog([hashtable]$State) {
    $runspace = [runspacefactory]::CreateRunspace()
    $powershell = $null
    try {
        $runspace.Open()
        $powershell = [powershell]::Create()
        $powershell.Runspace = $runspace
        $null = $powershell.AddCommand('Import-Module').AddParameter('Name', (Join-Path $PSScriptRoot 'GateWatchdog.psm1')).AddStatement()
        $null = $powershell.AddCommand('Invoke-PbiGateWatchdog').AddParameter('State', $State)
        return [pscustomobject]@{ PowerShell = $powershell; Runspace = $runspace; Handle = $powershell.BeginInvoke() }
    }
    catch {
        if ($null -ne $powershell) { $powershell.Dispose() }
        $runspace.Dispose()
        throw
    }
}

<#
.SYNOPSIS 停止看护循环并释放 runspace.
.PARAMETER Watchdog Start-PbiGateWatchdog 返回的句柄.
.PARAMETER State 同一门禁快速失败状态.
.NOTES 正常在一个轮询周期内结束; 终止进程树期间最多等待 15 秒后强制停止. 看护未能运行时报告原因.
       失败只写诊断, 不替换门禁结果.
#>
function Stop-PbiGateWatchdog([pscustomobject]$Watchdog, [hashtable]$State) {
    $State.Stopping = $true
    try {
        if (!$Watchdog.Handle.AsyncWaitHandle.WaitOne(15000)) { $Watchdog.PowerShell.Stop() }
        $null = $Watchdog.PowerShell.EndInvoke($Watchdog.Handle)
        if ($Watchdog.PowerShell.HadErrors -and $Watchdog.PowerShell.Streams.Error.Count) {
            [Console]::Error.WriteLine('并行门禁看护未正常运行: ' + $Watchdog.PowerShell.Streams.Error[0].ToString())
        }
    }
    catch { [Console]::Error.WriteLine('并行门禁看护未正常结束: ' + $_.Exception.Message) }
    finally {
        $Watchdog.PowerShell.Dispose()
        $Watchdog.Runspace.Dispose()
    }
}

Export-ModuleMember -Function *-Pbi*
