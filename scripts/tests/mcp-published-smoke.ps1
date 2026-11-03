# FilePath    : pbi-utils\scripts\tests\mcp-published-smoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 用无开发工具 PATH 的正式发布进程验证运行时来源、MCP 发现、业务调用、只读权限与 EOF 退出.

<#
.SYNOPSIS 验证本地 MCP 的正式发布入口; 不修改模型、客户端配置或系统设置.
.PARAMETER PublishRoot 正式发布目录, 以 schemaVersion=3 清单决定运行时验收模式.
.PARAMETER EvidenceRoot 调用方提供的本轮证据目录; 省略时创建唯一目录.
.OUTPUTS PASS 与证据路径; 清单、启动、协议、权限或排空失败时抛出错误.
#>
param(
    [string]$PublishRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/build/release/win-x64/without-dotnet'),
    [string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceUserDataLease.ps1')
$acceptanceLease = Enter-AcceptanceUserDataLease
try {
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $root 'scripts/PublishArtifacts.psm1') -Force
. (Join-Path $PSScriptRoot 'PublishedRuntimeSmoke.ps1')
$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$manifest = Assert-PbiPublishedArtifacts $PublishRoot
$modeDirectory = if ($manifest.deploymentMode -ceq 'self-contained') { 'with-dotnet' } else { 'without-dotnet' }
$evidence = if ($EvidenceRoot) { [IO.Path]::GetFullPath($EvidenceRoot) } else { Join-Path $root ('artifacts/evidence/mcp-published-' + $modeDirectory + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')) }
$null = New-Item -ItemType Directory -Path $evidence -Force
$executable = Join-Path $PublishRoot 'pbi-utils.exe'
if (!(Test-Path -LiteralPath (Join-Path $PublishRoot 'ModelContextProtocol.Core.dll') -PathType Leaf)) { throw '发布包缺少 MCP SDK' }
foreach ($removed in @('PbiUtils.Agent.dll', 'Microsoft.Agents.AI.dll', 'Microsoft.Extensions.AI.OpenAI.dll', 'OpenAI.dll')) {
    if (Test-Path -LiteralPath (Join-Path $PublishRoot $removed)) { throw "发布包残留内嵌 AI 运行库: $removed" }
}
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.WorkingDirectory = $root
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
$tracePath = Join-Path $evidence 'mcp-published-host-trace.txt'
Set-PbiSmokeRuntimeEnvironment $start $manifest.deploymentMode $tracePath
$start.ArgumentList.Add('mcp')
$start.ArgumentList.Add('serve')
$process = [Diagnostics.Process]::Start($start)
$errors = $process.StandardError.ReadToEndAsync()
$script:McpSequence = 0
$script:McpMethod = ''
$operationFailure = $null

<#
.SYNOPSIS 发送完整 JSON-RPC 帧并读取同身份终态, 通知不冒充结果.
.PARAMETER Method 官方 MCP 方法.
.PARAMETER Parameters 结构化参数, 仅本验收的只读操作.
.PARAMETER ExpectedProtocolError 负向协议用例的精确错误码, 为空时任何协议错误均失败.
.PARAMETER Handshake 当前进程使用 initialize 协商入口, 结果不要求新版逐请求附加字段.
.OUTPUTS 精确请求的结果; JSON污染、EOF、超时或协议错误抛出失败.
#>
function Invoke-McpSmoke([string]$Method, [hashtable]$Parameters, [Nullable[int]]$ExpectedProtocolError = $null, [switch]$Handshake) {
    $script:McpSequence++
    $script:McpMethod = $Method
    $id = $script:McpSequence
    $request = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 12 -Compress
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $process.StandardOutput.ReadLineAsync()
        if (!$line.Wait([TimeSpan]::FromSeconds(30))) { throw 'MCP 发布进程响应超时' }
        $text = $line.GetAwaiter().GetResult()
        if ($null -eq $text) { throw 'MCP 发布进程提前退出' }
        $response = $text | ConvertFrom-Json -AsHashtable
        if ($response.jsonrpc -ne '2.0') { throw 'MCP stdout 包含非协议内容' }
        if ($response.ContainsKey('id') -and $response.id -eq $id) {
            if ($null -ne $ExpectedProtocolError) {
                if (!$response.ContainsKey('error') -or $response.error.code -ne $ExpectedProtocolError) { throw "MCP 未返回预期协议错误: $Method" }
                return $response.error
            }
            if ($response.ContainsKey('error')) { throw "MCP 协议错误: $($response.error.code)" }
            if (!$Handshake -and ($response.result.resultType -cne 'complete' -or $response.result._meta.'io.modelcontextprotocol/serverInfo'.name -cne 'pbi-utils')) { throw 'MCP 新版完成结果或服务身份缺失' }
            return $response.result
        }
    }
    throw 'MCP 发布进程没有返回当前请求终态'
}

try {
    $meta = @{
        'io.modelcontextprotocol/protocolVersion' = '2026-07-28'
        'io.modelcontextprotocol/clientInfo' = @{ name = 'pbi-utils-published-smoke'; version = '1' }
        'io.modelcontextprotocol/clientCapabilities' = @{}
    }
    $discovery = Invoke-McpSmoke 'server/discover' @{ _meta = $meta }
    if (@($discovery.supportedVersions).Count -ne 1 -or $discovery.supportedVersions[0] -cne '2026-07-28') { throw 'MCP 发布包没有广告支持的逐请求协议版本' }
    if (!$discovery.capabilities.ContainsKey('tools') -or $discovery.capabilities.ContainsKey('resources') -or $discovery.capabilities.ContainsKey('prompts')) { throw 'MCP 发布包能力声明不符合实际工具服务' }
    $unsupportedMeta = $meta.Clone()
    $unsupportedMeta['io.modelcontextprotocol/protocolVersion'] = '2099-01-01'
    $unsupported = Invoke-McpSmoke 'tools/list' @{ _meta = $unsupportedMeta } -ExpectedProtocolError -32022
    if ($unsupported.data.requested -cne '2099-01-01' -or @($unsupported.data.supported).Count -ne 1 -or $unsupported.data.supported[0] -cne '2026-07-28') { throw 'MCP 版本拒绝缺少准确的支持版本信息' }
    $tools = Invoke-McpSmoke 'tools/list' @{ _meta = $meta }
    if (!$tools.ContainsKey('ttlMs') -or $tools.ttlMs -ne 0 -or $tools.cacheScope -cne 'private') { throw 'MCP 工具列表缺少安全缓存提示' }
    $names = @($tools.tools | ForEach-Object name)
    foreach ($method in @('resources/list', 'resources/templates/list', 'prompts/list', 'tasks/list', 'roots/list', 'ping')) {
        $null = Invoke-McpSmoke $method @{ _meta = $meta } -ExpectedProtocolError -32601
    }
    foreach ($required in @('server_info', 'updates_check', 'format_code', 'scaffold_templates', 'scaffold_plan', 'scaffold_create', 'configuration_plan', 'configuration_apply', 'calendar_preview', 'sample_generate', 'desktop_sessions', 'desktop_model_edit', 'desktop_model_exportTables', 'desktop_configuration_apply')) {
        if ($names -notcontains $required) { throw "MCP 发布包缺少业务工具: $required" }
    }
    # 更新能力只允许显式查询固定来源; smoke 不依赖真实外网且不允许打开/执行安装包.
    $updateTool = @($tools.tools | Where-Object name -CEQ 'updates_check')[0]
    if (!$updateTool.annotations.readOnlyHint -or $names -contains 'updates_open' -or $names -contains 'updates_install') { throw 'MCP 软件更新工具超出只读查询范围' }
    $invalidUpdate = Invoke-McpSmoke 'tools/call' @{ _meta = $meta; name = 'updates_check'; arguments = @{ source = 'https://example.invalid' } }
    if (!$invalidUpdate.isError -or $invalidUpdate.structuredContent.error.code -cne 'INVALID_UPDATE_SOURCE') { throw 'MCP 更新工具没有拒绝任意来源' }
    $format = Invoke-McpSmoke 'tools/call' @{ _meta = $meta; name = 'format_code'; arguments = @{ language = 'dax'; text = 'SUM(Sales[Amount])' } }
    if ($format.isError -or $format.structuredContent.text -cne 'SUM ( Sales[Amount] )') { throw 'MCP 正式业务调用失败' }
    $hostInfo = Invoke-McpSmoke 'tools/call' @{ _meta = $meta; name = 'desktop_app_info'; arguments = @{ parameters = @{} } }
    if ($hostInfo.isError -or [string]::IsNullOrWhiteSpace($hostInfo.structuredContent.version)) { throw 'MCP 共享后台真实 IPC 调用失败' }
    $denied = Invoke-McpSmoke 'tools/call' @{ _meta = $meta; name = 'configuration_apply'; arguments = @{ planId = 'unapproved'; approve = 'unapproved' } }
    if (!$denied.isError -or $denied.structuredContent.error.code -cne 'MCP_WRITE_DISABLED') { throw 'MCP 默认只读未强制执行' }
    $desktopDenied = Invoke-McpSmoke 'tools/call' @{ _meta = $meta; name = 'desktop_model_edit'; arguments = @{ parameters = @{} } }
    if (!$desktopDenied.isError -or $desktopDenied.structuredContent.error.code -cne 'MCP_WRITE_DISABLED') { throw 'MCP 桌面模型写入未强制校验启动权限' }
    $process.StandardInput.Close()
    if (!$process.WaitForExit(30000)) { throw 'MCP EOF 后未及时排空退出' }
    if ($process.ExitCode -ne 0) { throw 'MCP 正常关闭返回非零退出码' }
    $stderr = $errors.GetAwaiter().GetResult()
    Set-Content -LiteralPath (Join-Path $evidence 'mcp-published-smoke.stderr.txt') -Value $stderr -Encoding utf8
    $runtimeResolution = Assert-PbiSmokeRuntimeResolution $tracePath $PublishRoot $manifest.deploymentMode
    $process.Dispose()
    $process = $null
    # 独立进程验证当前 Codex 使用的握手入口, 不在同一连接混用两种协议状态.
    $handshakeTracePath = Join-Path $evidence 'mcp-published-handshake-host-trace.txt'
    Set-PbiSmokeRuntimeEnvironment $start $manifest.deploymentMode $handshakeTracePath
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $initialized = Invoke-McpSmoke 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'pbi-utils-handshake-smoke'; version = '1' } } -Handshake
    if ($initialized.protocolVersion -cne '2025-11-25' -or $initialized.serverInfo.name -cne 'pbi-utils') { throw 'MCP initialize 协商或服务身份错误' }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}')
    $process.StandardInput.Flush()
    $handshakeTools = Invoke-McpSmoke 'tools/list' @{} -Handshake
    if (@($handshakeTools.tools).Count -ne $names.Count -or (Compare-Object $names @($handshakeTools.tools.name) -CaseSensitive)) { throw 'MCP 两种入口工具集合不一致' }
    $handshakeFormat = Invoke-McpSmoke 'tools/call' @{ name = 'format_code'; arguments = @{ language = 'dax'; text = 'SUM(Sales[Amount])' } } -Handshake
    if ($handshakeFormat.isError -or $handshakeFormat.structuredContent.text -cne 'SUM ( Sales[Amount] )') { throw 'MCP 握手入口正式业务调用失败' }
    $handshakeDenied = Invoke-McpSmoke 'tools/call' @{ name = 'desktop_model_edit'; arguments = @{ parameters = @{} } } -Handshake
    if (!$handshakeDenied.isError -or $handshakeDenied.structuredContent.error.code -cne 'MCP_WRITE_DISABLED') { throw 'MCP 握手入口绕过默认只读' }
    $process.StandardInput.Close()
    if (!$process.WaitForExit(30000) -or $process.ExitCode -ne 0) { throw 'MCP 握手入口 EOF 未正常退出' }
    $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $evidence 'mcp-published-handshake-smoke.stderr.txt') -Encoding utf8
    $handshakeRuntimeResolution = Assert-PbiSmokeRuntimeResolution $handshakeTracePath $PublishRoot $manifest.deploymentMode
    @{ status = 'PASS'; package = $modeDirectory; deploymentMode = $manifest.deploymentMode; runtimeResolution = $runtimeResolution; handshakeRuntimeResolution = $handshakeRuntimeResolution; realHostIpc = $true; exitCode = $process.ExitCode; protocol = '2026-07-28'; handshakeProtocol = $initialized.protocolVersion; handshake = 'PASS'; supportedVersions = $discovery.supportedVersions; protocolVersionEnforced = $true; resultMetadata = $true; cacheHints = $true; toolCount = $names.Count; tools = $names; developmentToolsRemovedFromPath = $true; sdkBundled = $true; embeddedAiRemoved = $true; discovery = $true; realFormatter = $true; readOnlyEnforced = $true; eofDrained = $true; thirdPartyAiClient = 'UNRUN'; physicalCleanMachine = 'UNRUN' } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'mcp-published-smoke.json') -Encoding utf8
    Set-Content -LiteralPath (Join-Path $evidence 'mcp-published-smoke.stderr.txt') -Value $stderr -Encoding utf8
    Write-Output "PASS $modeDirectory MCP 逐请求/握手发现、共享后台 IPC、真实格式化、运行时来源、只读拒绝与 EOF 排空."
}
catch {
    $operationFailure = $_
    throw
}
finally {
    if ($null -ne $process) {
        try {
            if (!$process.HasExited) {
                # 先用本客户端 EOF 排空, 超时也只结束该只读 CLI, 共享 Host 不属于进程树清理范围.
                try { $process.StandardInput.Close() }
                catch { [Console]::Error.WriteLine('MCP 标准输入关闭失败, 继续等待本客户端退出: ' + $_.Exception.Message) }
                if (!$process.WaitForExit(30000)) {
                    $process.Kill()
                    if (!$process.WaitForExit(10000)) { throw '只读 MCP 客户端清理超时; 未结束共享后台' }
                }
            }
        }
        catch {
            if ($null -eq $operationFailure) { throw }
            [Console]::Error.WriteLine('MCP 客户端清理失败, 保留原验收错误: ' + $_.Exception.Message)
        }
        finally {
            # 失败也保存本次只读客户端的安全诊断与精确请求阶段, 避免提前退出丢失定位证据.
            if ($null -ne $operationFailure -and $process.HasExited) {
                $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $evidence 'mcp-published-failure.stderr.txt') -Encoding utf8
                @{ exitCode = $process.ExitCode; requestSequence = $script:McpSequence; method = $script:McpMethod } |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'mcp-published-failure.json') -Encoding utf8
            }
            $process.Dispose(); $process = $null
        }
    }
}

}
finally { $acceptanceLease.Dispose() }
