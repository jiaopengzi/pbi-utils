# FilePath    : pbi-utils\scripts\tests\mcp-codex-smoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 通过本机 Codex 原生 app-server 验证正式 MCP 握手与完整工具发现, 不创建聊天或修改客户端配置.

<#
.SYNOPSIS 验证已安装 Codex 的真实 MCP 接入, 不调用模型、写工具或新建聊天.
.PARAMETER PublishRoot 正式自包含发布目录, 本轮进程配置覆盖不写入用户配置.
.PARAMETER CodexCommand 本机已安装的 Codex 命令, 不下载或升级客户端.
.PARAMETER AllowWrite 本次进程使用允许修改的启动配置, 仍只验证原生工具发现, 不调用业务工具.
.OUTPUTS 唯一证据目录及 PASS/FAIL; 客户端缺失、握手失败、工具遗漏或退出失败均非零.
#>
param(
    [string]$PublishRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/build/release/win-x64/without-dotnet'),
    [string]$CodexCommand = 'codex',
    [switch]$AllowWrite
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceUserDataLease.ps1')
$acceptanceLease = Enter-AcceptanceUserDataLease
try {
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$executable = (Resolve-Path (Join-Path $PublishRoot 'pbi-utils.exe')).Path
$codex = (Get-Command $CodexCommand -CommandType Application -ErrorAction Stop).Source
$evidence = Join-Path $root ('artifacts/evidence/mcp-codex-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $evidence -Force
$start = [Diagnostics.ProcessStartInfo]::new($codex)
$start.WorkingDirectory = $root
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
# 参数数组避免 shell 转义; 只覆盖该测试进程的服务入口, 默认只读, 不注册任何客户端.
$launchArguments = @('mcp', 'serve')
if ($AllowWrite) { $launchArguments += '--allow-write' }
foreach ($argument in @('app-server', '--stdio', '-c', ('mcp_servers.pbi-utils.command=' + ($executable | ConvertTo-Json -Compress)), '-c', ('mcp_servers.pbi-utils.args=' + (ConvertTo-Json -InputObject $launchArguments -Compress)), '-c', 'mcp_servers.pbi-utils.enabled=true')) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
$errors = $process.StandardError.ReadToEndAsync()
$script:CodexSequence = 0
$checks = [ordered]@{ nativeToolDiscovery = 'UNRUN'; runtimePermission = 'UNRUN'; backgroundBusinessCall = 'UNRUN'; modelWrite = 'UNRUN'; eof = 'UNRUN' }
$report = [ordered]@{
    status = 'FAIL'; validationScope = 'native-client-discovery'; launchAllowWrite = [bool]$AllowWrite; launchArguments = $launchArguments
    checks = $checks; modelCalls = 0; modelWrites = 0; clientConfigurationChanged = $false; cleanupErrors = @()
}
$operationFailure = $null

<#
.SYNOPSIS 发送原生 app-server 请求并读取对应响应, 排除异步通知且保留原始协议证据.
.PARAMETER Method 固定 initialize 或 mcpServerStatus/list 方法.
.PARAMETER Parameters 本脚本构造的只读请求参数.
.OUTPUTS 对应 result; 超时、提前 EOF、JSON 污染或协议错误抛出.
#>
function Invoke-CodexProbe([string]$Method, [hashtable]$Parameters) {
    $script:CodexSequence++
    $id = $script:CodexSequence
    $request = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 12 -Compress
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = $deadline - [DateTime]::UtcNow
        $line = $process.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Codex 在 MCP 发现完成前退出' }
        Add-Content -LiteralPath (Join-Path $evidence 'codex.stdout.jsonl') -Value $line -Encoding utf8
        $frame = $line | ConvertFrom-Json -AsHashtable
        if (!$frame.ContainsKey('id') -or $frame.id -ne $id) { continue }
        if ($frame.ContainsKey('error')) { throw ('Codex 请求失败: ' + $Method + ' / ' + $frame.error.code) }
        if (!$frame.ContainsKey('result')) { throw 'Codex 响应缺少 result' }
        return $frame.result
    }
    throw 'Codex MCP 发现等待超时'
}

try {
    $initialized = Invoke-CodexProbe 'initialize' @{ clientInfo = @{ name = 'pbi-utils-codex-smoke'; version = '1' } }
    $report.client = $initialized.userAgent
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"initialized","params":{}}')
    $process.StandardInput.Flush()
    $checks.nativeToolDiscovery = 'FAIL'
    $listed = Invoke-CodexProbe 'mcpServerStatus/list' @{ serverName = 'pbi-utils' }
    if ($listed.nextCursor -or @($listed.data).Count -ne 1 -or $listed.data[0].name -cne 'pbi-utils') { throw 'Codex 未返回唯一目标 MCP 服务' }
    $server = $listed.data[0]
    if ($server.toolsError) { throw ('Codex 原生 MCP 接入失败: ' + $server.toolsError) }
    $names = @($server.tools.Values | ForEach-Object { $_.name })
    $manifest = Get-Content -LiteralPath (Join-Path $root 'tests/contracts/mcp-tool-cases.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($names.Count -ne $manifest.cases.Count -or (Compare-Object @($manifest.cases.name) $names -CaseSensitive)) { throw 'Codex 原生工具发现集合与当前机器契约不一致' }
    $report.toolCount = $names.Count
    $checks.nativeToolDiscovery = 'PASS'
    $checks.eof = 'FAIL'
    $process.StandardInput.Close()
    $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult() | Out-Null
    if ($process.ExitCode -ne 0) { throw 'Codex EOF 后返回非零退出码' }
    $report.exitCode = $process.ExitCode
    $checks.eof = 'PASS'
    $report.status = 'PASS'
}
catch {
    $operationFailure = $_
    $report.failure = $_.Exception.Message
    throw
}
finally {
    # 仅结束本脚本创建且未执行写入的 app-server; 不结束其进程树, 共享 Host 可能仍服务其他客户端.
    try {
        if (!$process.HasExited) {
            $process.StandardInput.Close()
            if (!$process.WaitForExit(10000)) {
                $process.Kill()
                if (!$process.WaitForExit(10000)) { throw 'Codex 测试进程清理超时; 未结束共享后台' }
            }
        }
    }
    catch { $report.cleanupErrors += $_.Exception.Message }
    try { $errors.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $evidence 'codex.stderr.txt') -Encoding utf8 }
    catch { $report.cleanupErrors += $_.Exception.Message }
    if ($report.cleanupErrors.Count -gt 0) { $report.status = 'FAIL' }
    try { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8 }
    catch { $report.cleanupErrors += $_.Exception.Message }
    $process.Dispose()
    if ($report.cleanupErrors.Count -gt 0) {
        $cleanupMessage = 'Codex 验收清理失败: ' + ($report.cleanupErrors -join '; ')
        if ($null -eq $operationFailure) { throw $cleanupMessage }
        [Console]::Error.WriteLine($cleanupMessage)
    }
}
$report | ConvertTo-Json -Depth 6
Write-Output ('Codex 原生 MCP 证据: ' + $evidence)

}
finally { $acceptanceLease.Dispose() }
