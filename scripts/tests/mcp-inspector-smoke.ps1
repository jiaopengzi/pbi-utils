# FilePath    : pbi-utils\scripts\tests\mcp-inspector-smoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 以独立 Inspector 连接正式包, 验证严格发现与只读参数等价, 不修改模型.

<#
.SYNOPSIS 使用固定版本第三方 Inspector 验证严格 schema 和真实只读调用; 需要 Node/npx, 不注册客户端.
.PARAMETER PublishRoot 已构建的发布目录, 不修改模型或安装常驻服务.
.PARAMETER AllowWrite 本次进程使用允许修改的启动配置, 验收仍只调用读取/格式化/预览工具.
.OUTPUTS 本轮唯一证据目录和摘要; 非零退出、schema 错误/警告、业务失败均失败.
#>
param(
    [string]$PublishRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/build/release/win-x64/without-dotnet'),
    [switch]$AllowWrite
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceUserDataLease.ps1')
$acceptanceLease = Enter-AcceptanceUserDataLease
try {
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$executable = (Resolve-Path (Join-Path $PublishRoot 'pbi-utils.exe')).Path
$evidence = Join-Path $root ('artifacts/evidence/mcp-inspector-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $evidence -Force
$launchArguments = @('mcp', 'serve')
if ($AllowWrite) { $launchArguments += '--allow-write' }
$checks = [ordered]@{ modernDiscovery = 'UNRUN'; legacyDiscovery = 'UNRUN'; modernPermission = 'UNRUN'; legacyPermission = 'UNRUN'; modernBackgroundCall = 'UNRUN'; legacyBackgroundCall = 'UNRUN'; formatter = 'UNRUN'; calendarEquivalence = 'UNRUN'; modelWrite = 'UNRUN' }
$summary = [ordered]@{
    status = 'FAIL'; client = 'MCP Inspector CLI'; clientVersion = '2.13.0'; validationScope = 'third-party-client-read-only-business'
    launchAllowWrite = [bool]$AllowWrite; launchArguments = $launchArguments; checks = $checks; modelWrites = 0; clientConfigurationChanged = $false
}
$operationFailure = $null

<#
.SYNOPSIS 执行一个独立 Inspector 请求, 保存原始输出与退出码, 非零或工具错误立即失败.
.PARAMETER Name 本轮证据文件基名.
.PARAMETER Arguments Inspector 请求参数, 不含服务写入开关.
.PARAMETER Era 本次独立客户端的协议入口, modern 验证逐请求协议, legacy 验证 initialize 协商.
.OUTPUTS 客户端返回的 result 对象; 调用失败抛出错误.
#>
function Invoke-Inspector([string]$Name, [string[]]$Arguments, [ValidateSet('modern', 'legacy')][string]$Era = 'modern') {
    $stdoutPath = Join-Path $evidence "$Name.json"
    $stderrPath = Join-Path $evidence "$Name.stderr.txt"
    $clientArgs = @('--yes', '@modelcontextprotocol/inspector@2.13.0', '--cli', $executable) + $launchArguments + @('--protocol-era', $Era, '--format', 'json') + $Arguments
    & npx @clientArgs 1> $stdoutPath 2> $stderrPath
    $clientExit = $LASTEXITCODE
    Set-Content (Join-Path $evidence "$Name.exit.txt") $clientExit
    if ($clientExit -ne 0) { throw "Inspector $Name 返回 $clientExit, 详见 $stderrPath" }
    $response = Get-Content $stdoutPath -Raw | ConvertFrom-Json -AsHashtable
    if ($response.result.isError) { throw "Inspector $Name 返回工具错误" }
    return $response
}

try {
$checks.modernDiscovery = 'FAIL'
$listed = Invoke-Inspector 'tools-published' @('--method', 'tools/list', '--strict')
$manifest = Get-Content -LiteralPath (Join-Path $root 'tests/contracts/mcp-tool-cases.json') -Raw | ConvertFrom-Json -AsHashtable
if ((Compare-Object @($manifest.cases.name) @($listed.result.tools.name) -CaseSensitive) -or
    $listed.result.tools.Count -ne $manifest.cases.Count -or @($listed.schemaFindings).Where({ $null -ne $_ }).Count -ne 0) { throw '工具集合或 schema 诊断不符' }
if (![string]::IsNullOrWhiteSpace((Get-Content (Join-Path $evidence 'tools-published.stderr.txt') -Raw))) { throw '严格发现产生未预期 stderr' }
$connected = Invoke-Inspector 'connection-published' @('--method', 'initialize')
if ($connected.result.protocolVersion -cne '2026-07-28') { throw '协议版本不符' }
$checks.modernDiscovery = 'PASS'
$checks.legacyDiscovery = 'FAIL'
$handshake = Invoke-Inspector 'handshake-published' @('--method', 'initialize') -Era legacy
if ($handshake.result.protocolVersion -cne '2025-11-25') { throw 'initialize 未协商到支持握手的协议版本' }
$handshakeTools = Invoke-Inspector 'handshake-tools-published' @('--method', 'tools/list', '--strict') -Era legacy
if ((Compare-Object @($listed.result.tools.name) @($handshakeTools.result.tools.name) -CaseSensitive) -or
    @($handshakeTools.schemaFindings).Where({ $null -ne $_ }).Count -ne 0) { throw '握手入口工具集合或 schema 诊断与新版入口不一致' }
if (![string]::IsNullOrWhiteSpace((Get-Content (Join-Path $evidence 'handshake-tools-published.stderr.txt') -Raw))) { throw '握手入口严格发现产生未预期 stderr' }
$checks.legacyDiscovery = 'PASS'
$checks.formatter = 'FAIL'
$handshakeFormat = Invoke-Inspector 'handshake-format-published' @('--method', 'tools/call', '--tool-name', 'format_code', '--tool-arg', 'language=dax', 'text=SUM(Sales[Amount])') -Era legacy
if ($handshakeFormat.result.structuredContent.text -cne 'SUM ( Sales[Amount] )' -or $handshakeFormat.result.structuredContent.executed -ne $false) { throw '握手入口格式化调用不符' }
$checks.formatter = 'UNRUN'
$checks.modernPermission = 'FAIL'
$info = Invoke-Inspector 'server-info-published' @('--method', 'tools/call', '--tool-name', 'server_info')
if ($info.result.structuredContent.allowWrite -isnot [bool] -or $info.result.structuredContent.allowWrite -ne [bool]$AllowWrite) { throw '逐请求入口的实际启动权限与请求配置不一致' }
$checks.modernPermission = 'PASS'
$checks.legacyPermission = 'FAIL'
$handshakeInfo = Invoke-Inspector 'handshake-server-info-published' @('--method', 'tools/call', '--tool-name', 'server_info') -Era legacy
if ($handshakeInfo.result.structuredContent.allowWrite -isnot [bool] -or $handshakeInfo.result.structuredContent.allowWrite -ne [bool]$AllowWrite) { throw '握手入口的实际启动权限与请求配置不一致' }
$checks.legacyPermission = 'PASS'
# 每次 Inspector 调用均建立独立客户端上下文; 不携带另一进程返回的 sessionId.
$checks.modernBackgroundCall = 'FAIL'
$appInfo = Invoke-Inspector 'app-info-published' @('--method', 'tools/call', '--tool-name', 'desktop_app_info', '--tool-args-json', '{"parameters":{}}')
if ([string]::IsNullOrWhiteSpace($appInfo.result.structuredContent.version)) { throw '逐请求入口未返回真实后台产品版本' }
$checks.modernBackgroundCall = 'PASS'
$checks.legacyBackgroundCall = 'FAIL'
$handshakeAppInfo = Invoke-Inspector 'handshake-app-info-published' @('--method', 'tools/call', '--tool-name', 'desktop_app_info', '--tool-args-json', '{"parameters":{}}') -Era legacy
if ([string]::IsNullOrWhiteSpace($handshakeAppInfo.result.structuredContent.version)) { throw '握手入口未返回真实后台产品版本' }
$checks.legacyBackgroundCall = 'PASS'
$checks.formatter = 'FAIL'
$format = Invoke-Inspector 'format-code-published' @('--method', 'tools/call', '--tool-name', 'format_code', '--tool-arg', 'language=dax', 'text=SUM(Sales[Amount])')
if ($format.result.structuredContent.text -cne 'SUM ( Sales[Amount] )' -or $format.result.structuredContent.executed -ne $false) { throw '格式化调用结果不符' }
$checks.formatter = 'PASS'
$checks.calendarEquivalence = 'FAIL'
$numeric = Invoke-Inspector 'calendar-numeric-published' @('--method', 'tools/call', '--tool-name', 'calendar_preview', '--tool-args-json', '{"options":{"kind":"simple","startYear":2024,"endYear":2024}}')
$nullable = Invoke-Inspector 'calendar-string-null-published' @('--method', 'tools/call', '--tool-name', 'calendar_preview', '--tool-args-json', '{"options":{"kind":"simple","startYear":"2024","endYear":"2024","overrides":null,"fields":null,"numberedColumns":null,"workdays":null}}')
$numericResult = $numeric.result.structuredContent | ConvertTo-Json -Depth 30 -Compress
$nullableResult = $nullable.result.structuredContent | ConvertTo-Json -Depth 30 -Compress
if ($numericResult -cne $nullableResult) { throw '数字/数字字符串和省略/null 的日期只读结果不一致' }
$checks.calendarEquivalence = 'PASS'
$summary.status = 'PASS'
$summary.protocol = $connected.result.protocolVersion
$summary.handshakeProtocol = $handshake.result.protocolVersion
$summary.toolCount = $listed.result.tools.Count
$summary.schemaErrors = 0
$summary.schemaWarnings = 0
$summary.allowWrite = $info.result.structuredContent.allowWrite
$summary.handshakeAllowWrite = $handshakeInfo.result.structuredContent.allowWrite
$summary.backgroundVersion = $appInfo.result.structuredContent.version
$summary.handshakeBackgroundVersion = $handshakeAppInfo.result.structuredContent.version
$summary.daxExecuted = $format.result.structuredContent.executed
}
catch {
    $operationFailure = $_
    $summary.failure = $_.Exception.Message
    throw
}
finally {
    try { $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'inspector-summary.json') -Encoding utf8 }
    catch {
        if ($null -eq $operationFailure) { throw }
        [Console]::Error.WriteLine('Inspector 证据保存失败, 保留原验收错误: ' + $_.Exception.Message)
    }
}
$summary | ConvertTo-Json -Depth 6

Write-Output ("Inspector 证据: " + $evidence)

}
finally { $acceptanceLease.Dispose() }
