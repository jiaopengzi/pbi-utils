# FilePath    : pbi-utils\scripts\tests\McpProbeClient.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 发布 MCP 验收共用有界 stdio 客户端与共享后台身份核验, 不提供自动选择用户模型能力.

<#
.SYNOPSIS 等待一个异步任务, 使用固定上限, 异常保留原始原因.
.PARAMETER Task 需要观察的任务.
.PARAMETER Seconds 最长等待秒数.
.OUTPUTS 任务结果; 超时和任务失败均抛出.
#>
function Wait-ProbeTask($Task, [int]$Seconds = 60) {
    $result = $Task.WaitAsync([TimeSpan]::FromSeconds($Seconds)).GetAwaiter().GetResult()
    # PowerShell 按运行时泛型选中内部 VoidTaskResult, 无返回值任务不能污染脚本输出或调用方赋值.
    if ($null -ne $result -and $result.GetType().FullName -ceq 'System.Threading.Tasks.VoidTaskResult') { return }
    return $result
}

<#
.SYNOPSIS 启动本测试拥有的 CLI, 默认只读, 保留准确进程身份并立即排空 stderr.
.PARAMETER Name 本轮唯一客户端名称.
.PARAMETER AllowWrite 仅隔离写入验收可显式启用; 默认只读.
.OUTPUTS 已登记的客户端状态; 启动失败抛出, 不更改系统或客户端配置.
#>
function Start-ProbeClient([string]$Name, [switch]$AllowWrite) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PublishRoot 'pbi-utils.exe'))
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
    $start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
    $start.Environment['DOTNET_ROOT'] = Join-Path $evidence 'nonexistent-runtime'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.ArgumentList.Add('mcp')
    $start.ArgumentList.Add('serve')
    if ($AllowWrite) { $start.ArgumentList.Add('--allow-write') }
    $process = [Diagnostics.Process]::Start($start)
    $client = @{ Name = $Name; Process = $process; Started = $process.StartTime.ToUniversalTime(); Sequence = 0; Errors = $process.StandardError.ReadToEndAsync(); PendingRead = $null; Idle = $true; ExitCode = $null }
    $clients.Add($client)
    return $client
}

<#
.SYNOPSIS 保留完整原始 stdout 帧后检查 JSON-RPC, 不把通知或非 JSON 当成业务成功.
.PARAMETER Client 当前客户端状态.
.PARAMETER Line 原始标准输出行.
.OUTPUTS 已解析的 JSON 对象; 非协议输出抛出, 原始行仍保留.
#>
function Read-ProbeFrame($Client, [string]$Line) {
    Add-Content -LiteralPath (Join-Path $evidence ($Client.Name + '.stdout.jsonl')) -Value $Line -Encoding utf8
    $frame = ConvertFrom-Json -InputObject $Line -AsHashtable
    if ($frame -isnot [Collections.IDictionary] -or $frame.jsonrpc -cne '2.0') { throw 'MCP stdout 包含非协议内容' }
    return $frame
}

<#
.SYNOPSIS 用新版每请求元数据发送业务请求并核验对应身份的结果.
.PARAMETER Client 本测试拥有的 CLI 状态.
.PARAMETER Tool 固定工具名.
.PARAMETER Arguments 工具参数, 由调用脚本限定只读用例或已经核验的独占写入夹具.
.PARAMETER ErrorCode 负向用例必须返回的准确业务错误码.
.PARAMETER Method tools/call 或 tools/list, 后者只返回发现结果并拒绝协议错误.
.OUTPUTS 结构化结果; 超时、协议失败或不符合预期的业务结果抛出.
#>
function Invoke-ProbeTool($Client, [string]$Tool, [hashtable]$Arguments, [string]$ErrorCode = '', [ValidateSet('tools/call', 'tools/list')][string]$Method = 'tools/call') {
    $Client.Sequence++
    $id = $Client.Sequence
    $meta = @{ 'io.modelcontextprotocol/protocolVersion' = '2026-07-28'; 'io.modelcontextprotocol/clientInfo' = @{ name = 'pbi-utils-background-smoke'; version = '1' }; 'io.modelcontextprotocol/clientCapabilities' = @{} }
    $parameters = if ($Method -eq 'tools/list') { @{ _meta = $meta } } else { @{ _meta = $meta; name = $Tool; arguments = $Arguments } }
    $request = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $parameters } | ConvertTo-Json -Depth 30 -Compress
    $Client.Idle = $false
    Wait-ProbeTask ($Client.Process.StandardInput.WriteLineAsync($request)) 10
    Wait-ProbeTask ($Client.Process.StandardInput.FlushAsync()) 10
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = $deadline - [DateTime]::UtcNow
        if ($remaining.TotalMilliseconds -le 0) { break }
        $cancellation = [Threading.CancellationTokenSource]::new($remaining)
        try {
            $Client.PendingRead = $Client.Process.StandardOutput.ReadLineAsync($cancellation.Token).AsTask()
            $line = Wait-ProbeTask $Client.PendingRead
        }
        finally { $cancellation.Dispose() }
        $Client.PendingRead = $null
        if ($null -eq $line) { throw 'MCP stdout 提前 EOF' }
        $frame = Read-ProbeFrame $Client $line
        if (!$frame.ContainsKey('id') -or $frame.id -ne $id) { continue }
        $Client.Idle = $true
        if ($frame.ContainsKey('error')) { throw ('MCP 协议错误: ' + $frame.error.code) }
        $result = $frame.result
        if ($Method -eq 'tools/list') {
            if ($null -eq $result -or !$result.ContainsKey('tools') -or $result.nextCursor) { throw '工具发现缺少完整列表' }
            return $result
        }
        if ($null -eq $result -or !$result.ContainsKey('structuredContent')) { throw '工具缺少结构化结果' }
        if ($ErrorCode) {
            if (!$result.isError -or $result.structuredContent.error.code -cne $ErrorCode) { throw "工具未返回预期错误: $Tool / expected=$ErrorCode actual=$($result.structuredContent.error.code)" }
        }
        elseif ($result.isError) { throw ('工具失败: ' + $Tool + ' / ' + $result.structuredContent.error.code) }
        return ,$result.structuredContent
    }
    throw ('MCP 工具等待超时: ' + $Tool)
}

<#
.SYNOPSIS 核验当前客户端的唯一上下文和共享后台真实身份, 跟踪原进程句柄供最后排空检查.
.PARAMETER Client 本测试创建的 CLI.
.OUTPUTS 会话对象; 列表歧义、窗口可见或安装身份不符均失败, 不依赖父进程且不终止后台.
#>
function Get-ProbeSession($Client) {
    $result = Invoke-ProbeTool $Client 'desktop_sessions' @{}
    $sessions = @($result.sessions)
    if ($sessions.Count -ne 1 -or !$sessions[0].background -or $sessions[0].id -cnotmatch '^[0-9a-f]{32}$') { throw '客户端没有恰好一个独立共享后台上下文' }
    $session = $sessions[0]
    if ($initialHostIds -contains $session.processId) { throw '工具返回了测试前已存在的用户后台' }
    $process = [Diagnostics.Process]::GetProcessById($session.processId)
    try {
        # GetProcessById 默认不保留内核句柄; 必须在退出前获取, 才能在原进程退出后读取真实 ExitCode.
        $null = $process.SafeHandle
        $identity = '{0}-{1:x}' -f $process.Id, $process.StartTime.ToUniversalTime().Ticks
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -or $process.MainModule.FileName -ine (Join-Path $PublishRoot 'PbiUtils.Host.exe')) { throw '共享后台安装身份或无 UI 状态核验失败' }
        $existing = @($hosts | Where-Object { $_.Identity -ceq $identity })
        if ($existing.Count -eq 0) {
            $hosts.Add(@{ Process = $process; Identity = $identity; Contexts = @($session.id); ExitCode = $null })
            $process = $null
        }
        elseif ($existing[0].Contexts -cnotcontains $session.id) { $existing[0].Contexts += $session.id }
        return $session
    }
    finally { if ($null -ne $process) { $process.Dispose() } }
}

<#
.SYNOPSIS 发送 EOF 并验证本 CLI 正常退出, 仅最后测试客户端结束后等待共享后台排空, 不强杀后台.
.PARAMETER Client 本测试拥有的只读 CLI.
.OUTPUTS 无; 未及时正常退出或退出码非零抛出.
#>
function Close-ProbeClient($Client) {
    $Client.Process.StandardInput.Close()
    Wait-ProbeTask ($Client.Process.WaitForExitAsync())
    $Client.ExitCode = $Client.Process.ExitCode
    if ($Client.ExitCode -ne 0) { throw ('CLI 正常 EOF 返回非零: ' + $Client.Name) }
    if (@($clients | Where-Object { !$_.Process.HasExited }).Count -gt 0) { return }
    foreach ($hostEntry in $hosts) {
        Wait-ProbeTask ($hostEntry.Process.WaitForExitAsync())
        $hostEntry.ExitCode = $hostEntry.Process.ExitCode
        if ($hostEntry.ExitCode -ne 0) { throw '最后客户端结束后共享后台未正常退出' }
    }
}

