# FilePath    : pbi-utils\scripts\tests\mcp-background-smoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 用正式只读 MCP 进程验证共享后台共存、上下文隔离、真实 IPC、EOF 和最后排空, 不操作用户模型.

<#
.SYNOPSIS 独立运行共享后台回归, 不注册到全量构建, 不关闭用户窗口或强杀宿主.
.PARAMETER PublishRoot 正式自包含发布目录, 必须包含 CLI 和无 UI 业务后台.
.PARAMETER AllTools 额外按机器契约逐项执行全部工具, 写工具仅验证只读拒绝, 不修改用户模型.
.OUTPUTS 唯一本轮证据目录及 PASS/FAIL/UNRUN; 前置条件失败返回 3, 验证或清理失败返回 4.
#>
param(
    [string]$PublishRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/build/release/win-x64/without-dotnet'),
    [switch]$AllTools
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceUserDataLease.ps1')
$acceptanceLease = Enter-AcceptanceUserDataLease
try {
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$evidence = Join-Path $root ('artifacts/evidence/mcp-background-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $evidence -Force
$clients = [Collections.Generic.List[object]]::new()
$hosts = [Collections.Generic.List[object]]::new()
$checks = [ordered]@{ sharedHost = 'UNRUN'; isolatedSessions = 'UNRUN'; realIpc = 'UNRUN'; crossClientRejected = 'UNRUN'; peerSurvivesEof = 'UNRUN'; readOnlyEnforced = 'UNRUN'; normalEof = 'UNRUN'; parentExit = 'UNRUN'; lastConnectionDrain = 'UNRUN'; freshGeneration = 'UNRUN' }
$report = [ordered]@{ status = 'UNRUN'; exitCode = 3; checks = $checks; cleanupErrors = @(); clients = @(); hosts = @(); simulated = $false; modelWrites = 'UNRUN'; thirdPartyAiClient = 'UNRUN' }
$initialHostIds = @()

. (Join-Path $PSScriptRoot 'McpProbeClient.ps1')

try {
    if (!$IsWindows) { throw '此验收仅支持 Windows' }
    foreach ($required in @('pbi-utils.exe', 'PbiUtils.Host.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $PublishRoot $required) -PathType Leaf)) { throw ('发布产物缺失: ' + $required) }
    }
    # 只读验收需要独占其测试后台, 发现既有同安装后台时不接管/关闭用户资源.
    $existing = [Diagnostics.Process]::GetProcessesByName('PbiUtils.Host')
    try {
        foreach ($process in $existing) {
            $initialHostIds += $process.Id
            if ($process.MainModule.FileName -ieq (Join-Path $PublishRoot 'PbiUtils.Host.exe')) {
                throw '存在同安装目录的业务后台, 本轮不接管或关闭其他客户端'
            }
        }
    }
    finally { foreach ($process in $existing) { $process.Dispose() } }
    $report.status = 'FAIL'
    $report.exitCode = 4
    $a = Start-ProbeClient 'a'
    $sessionA = Get-ProbeSession $a
    $b = Start-ProbeClient 'b'
    $sessionB = Get-ProbeSession $b
    if ($sessionA.id -ceq $sessionB.id) { throw '两个 MCP 串用了同一上下文' }
    if ($sessionA.processId -ne $sessionB.processId) { throw '两个 MCP 未复用同一个共享后台' }
    $checks.sharedHost = 'PASS'
    $checks.isolatedSessions = 'PASS'
    $null = Invoke-ProbeTool $a 'desktop_app_info' @{ sessionId = $sessionB.id; parameters = @{} } 'MCP_DESKTOP_SESSION_OWNED'
    $null = Invoke-ProbeTool $b 'desktop_app_info' @{ sessionId = $sessionA.id; parameters = @{} } 'MCP_DESKTOP_SESSION_OWNED'
    $checks.crossClientRejected = 'PASS'
    foreach ($client in @($a, $b)) {
        $info = Invoke-ProbeTool $client 'desktop_app_info' @{ parameters = @{} }
        if ([string]::IsNullOrWhiteSpace($info.version)) { throw '真实 IPC 缺少产品版本' }
        $permission = Invoke-ProbeTool $client 'server_info' @{}
        if ($permission.allowWrite) { throw '默认客户端意外取得写权限' }
        $null = Invoke-ProbeTool $client 'desktop_model_edit' @{ parameters = @{} } 'MCP_WRITE_DISABLED'
    }
    $checks.realIpc = 'PASS'
    $checks.readOnlyEnforced = 'PASS'
    Close-ProbeClient $a
    if ((Get-ProbeSession $b).id -cne $sessionB.id -or $hosts[0].Process.HasExited) { throw '一个客户端 EOF 结束或重建了其他客户端的上下文' }
    $checks.peerSurvivesEof = 'PASS'
    $checks.normalEof = 'PASS'
    if ($AllTools) {
        . (Join-Path $PSScriptRoot 'mcp-tool-coverage.ps1')
        $report.toolCoverage = Invoke-AllToolCases $b $sessionB
        if ($report.toolCoverage.failed -gt 0) { throw '全量工具用例失败, 请查看 tool-coverage.json' }
    }
    $c = Start-ProbeClient 'c'
    $sessionC = Get-ProbeSession $c
    if ($sessionC.processId -ne $sessionB.processId -or $sessionC.id -ceq $sessionB.id) { throw '第三客户端未取得独立共享后台上下文' }
    $null = Invoke-ProbeTool $c 'desktop_app_info' @{ parameters = @{} }
    # 仅结束本测试在只读请求终态后的空闲 CLI; 不结束进程树或共享后台.
    $c.Process.Kill()
    Wait-ProbeTask ($c.Process.WaitForExitAsync()) 10
    $c.ExitCode = $c.Process.ExitCode
    if ((Get-ProbeSession $b).id -cne $sessionB.id) { throw '客户端异常退出改变了其他上下文' }
    $null = Invoke-ProbeTool $b 'desktop_app_info' @{ parameters = @{} }
    $checks.parentExit = 'PASS'
    Close-ProbeClient $b
    if (!$hosts[0].Process.HasExited -or $hosts[0].ExitCode -ne 0) { throw '最后客户端结束后共享后台未排空退出' }
    $checks.lastConnectionDrain = 'PASS'
    $d = Start-ProbeClient 'd'
    $sessionD = Get-ProbeSession $d
    if ($sessionD.id -ceq $sessionA.id -or $sessionD.id -ceq $sessionB.id -or $hosts.Count -ne 2 -or $hosts[0].Identity -ceq $hosts[1].Identity) { throw '后台重启复用了失效上下文或进程身份' }
    Close-ProbeClient $d
    $checks.freshGeneration = 'PASS'
    $report.status = 'PASS'
    $report.exitCode = 0
}
catch {
    $report.failure = $_.Exception.Message
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'failure.txt') -Encoding utf8
}
finally {
    foreach ($client in $clients) {
        try {
            if (!$client.Process.HasExited) {
                $client.Process.StandardInput.Close()
                try { Wait-ProbeTask ($client.Process.WaitForExitAsync()) }
                catch {
                    # 仅回收已收到请求终态的本测试空闲 CLI; 仍在途时保留进程并报告清理失败.
                    if (!$client.Process.HasExited -and $client.Idle) {
                        $client.Process.Kill()
                        Wait-ProbeTask ($client.Process.WaitForExitAsync()) 10
                    }
                    throw
                }
            }
            $client.ExitCode = $client.Process.ExitCode
            if ($null -ne $client.PendingRead) {
                try { $line = Wait-ProbeTask $client.PendingRead 10; if ($null -ne $line) { $null = Read-ProbeFrame $client $line } }
                catch [OperationCanceledException] { }
            }
            $tail = Wait-ProbeTask ($client.Process.StandardOutput.ReadToEndAsync()) 10
            if ($tail.Length -gt 0) {
                Set-Content -LiteralPath (Join-Path $evidence ($client.Name + '.stdout-tail.txt')) -Value $tail -Encoding utf8
                foreach ($line in ($tail.TrimEnd("`r", "`n") -split '\r?\n')) { $null = Read-ProbeFrame $client $line }
            }
        }
        catch { $report.cleanupErrors += ($_ | Out-String) }
        finally {
            try { Wait-ProbeTask $client.Errors 10 | Set-Content -LiteralPath (Join-Path $evidence ($client.Name + '.stderr.txt')) -Encoding utf8 }
            catch { $report.cleanupErrors += ($_ | Out-String) }
            $report.clients += @{ name = $client.Name; processId = $client.Process.Id; startedUtc = $client.Started; exitCode = $client.ExitCode; exited = $client.Process.HasExited }
            $client.Process.Dispose()
        }
    }
    foreach ($hostEntry in $hosts) {
        try {
            Wait-ProbeTask ($hostEntry.Process.WaitForExitAsync())
            $hostEntry.ExitCode = $hostEntry.Process.ExitCode
        }
        catch { $report.cleanupErrors += ($_ | Out-String) }
        finally {
            $report.hosts += @{ contexts = $hostEntry.Contexts; identity = $hostEntry.Identity; processId = $hostEntry.Process.Id; exitCode = $hostEntry.ExitCode; exited = $hostEntry.Process.HasExited }
            $hostEntry.Process.Dispose()
        }
    }
    if ($report.cleanupErrors.Count -gt 0 -and $report.exitCode -eq 0) { $report.status = 'FAIL'; $report.exitCode = 4 }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
}
Write-Output ($report.status + ' MCP 共享后台验收; 证据: ' + $evidence)
exit $report.exitCode

}
finally { $acceptanceLease.Dispose() }
