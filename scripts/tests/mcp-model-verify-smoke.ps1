# FilePath    : pbi-utils\scripts\tests\mcp-model-verify-smoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在独占 MCP 写入夹具上核验真实计算及保存重开后的定义, 不重放模型写入.

<#
.SYNOPSIS 对 mcp-model-write-smoke 创建并成功提交的夹具做只读计算和重开验收.
.PARAMETER FixtureRecord 成功写入夹具的 fixture.json, 不接受任意用户工程.
.PARAMETER Stage AfterWrite 要求原进程仍存在; AfterReopen 要求原进程已退出且先前计算已通过.
.PARAMETER PublishRoot 正式自包含发布目录.
.OUTPUTS 每轮独立 evidence 目录和状态; 失败返回4, 不把打开/点击保存视为持久化成功.
.NOTES AfterWrite 后由操作者在 Desktop 保存并关闭; AfterReopen 通过 MCP 打开同一夹具再查询.
#>
param(
    [Parameter(Mandatory)][string]$FixtureRecord,
    [Parameter(Mandatory)][ValidateSet('AfterWrite', 'AfterReopen')][string]$Stage,
    [string]$PublishRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/build/release/win-x64/without-dotnet')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceUserDataLease.ps1')
$acceptanceLease = Enter-AcceptanceUserDataLease
try {
    $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
    $FixtureRecord = [IO.Path]::GetFullPath($FixtureRecord)
    $fixtureRoot = Split-Path $FixtureRecord -Parent
    if ((Split-Path $fixtureRoot -Parent) -ine (Join-Path $root 'artifacts/evidence') -or (Split-Path $fixtureRoot -Leaf) -cnotmatch '^mcp-write-model-[0-9a-f]{32}$' -or (Split-Path $FixtureRecord -Leaf) -cne 'fixture.json') { throw '仅接受独占写入测试的夹具记录' }
    $record = Get-Content -LiteralPath $FixtureRecord -Raw | ConvertFrom-Json -AsHashtable
    $write = Get-Content -LiteralPath (Join-Path $fixtureRoot 'result.json') -Raw | ConvertFrom-Json -AsHashtable
    $project = Join-Path $fixtureRoot 'fixture/model/blank.pbip'
    if ($record.schemaVersion -ne 1 -or $record.project -ine $project -or $write.status -cne 'PASS' -or $write.writeState -cne 'committed' -or $write.modelWrites -ne 1 -or $record.fixtureMarker -cne ('MCP isolated ' + (Split-Path $fixtureRoot -Leaf))) { throw '没有成功提交的本轮夹具身份, 不打开模型' }
    if ((@([Diagnostics.Process]::GetProcessesByName('PbiUtils.Desktop')).Count -or @([Diagnostics.Process]::GetProcessesByName('PbiUtils.Host')).Count)) { throw '存在产品宿主, 不接管用户工作区' }
    $oldProcess = Get-Process -Id $record.desktopPid -ErrorAction SilentlyContinue
    try {
        $originalAlive = $null -ne $oldProcess -and $oldProcess.StartTime.ToUniversalTime().Ticks.ToString() -ceq $record.desktopStartTicks
        if ($Stage -ceq 'AfterWrite' -and !$originalAlive) { throw '原测试 Desktop 已退出, 不能核验提交后的原引擎' }
        if ($Stage -ceq 'AfterReopen' -and $originalAlive) { throw '请先在 Desktop 保存并关闭该测试工程, 不能把原进程当作重开' }
    } finally { if ($null -ne $oldProcess) { $oldProcess.Dispose() } }
    $before = $null
    if ($Stage -ceq 'AfterReopen') {
        $before = @(Get-ChildItem -LiteralPath $fixtureRoot -Filter 'verify-AfterWrite-*' -Directory | ForEach-Object {
            $path = Join-Path $_.FullName 'result.json'
            if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable }
        } | Where-Object { $_.status -ceq 'PASS' -and $_.calculatedValue -eq 42 })
        if (!$before.Count) { throw '需要先完成 AfterWrite 计算验收, 不推定原模型已计算成功' }
    }
    $evidence = Join-Path $fixtureRoot ('verify-' + $Stage + '-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $evidence
    $clients = [Collections.Generic.List[object]]::new()
    $hosts = [Collections.Generic.List[object]]::new()
    $initialHostIds = @()
    . (Join-Path $PSScriptRoot 'McpProbeClient.ps1')
    $result = [ordered]@{ status = 'FAIL'; stage = $Stage; simulated = $false; modelWrites = 0; fileSavedByScript = $false; cleanupErrors = @() }
    try {
        $client = Start-ProbeClient 'verify' -AllowWrite
        $session = Get-ProbeSession $client
        $opened = Invoke-ProbeTool $client 'desktop_project_openPath' @{ parameters = @{ path = $project } }
        if ($opened.connection.filePath -ine $project -or $opened.connection.fileIdentityStatus -cne 'verified') { throw '夹具文件身份不符' }
        $instances = Invoke-ProbeTool $client 'desktop_powerbi_instances' @{ parameters = @{} }
        $instance = @($instances.instances | Where-Object instanceId -CEQ $opened.connection.instanceId)
        if ($instance.Count -ne 1) { throw '引擎身份不唯一' }
        $process = Get-Process -Id $instance[0].processId
        try { $result.desktop = @{ pid = $process.Id; startTicks = $process.StartTime.ToUniversalTime().Ticks.ToString(); instanceId = $instance[0].instanceId } }
        finally { $process.Dispose() }
        if ($Stage -ceq 'AfterWrite' -and ($result.desktop.pid -ne $record.desktopPid -or $result.desktop.startTicks -cne $record.desktopStartTicks)) { throw '原测试进程身份改变' }
        if ($Stage -ceq 'AfterReopen' -and $result.desktop.startTicks -ceq $record.desktopStartTicks) { throw '没有获得重开后的新引擎' }
        $read = @{ projectId = $opened.projectId; baselineId = $opened.baselineId; table = 'McpWriteFixture'; section = 'tables' }
        $tables = Invoke-ProbeTool $client 'desktop_project_inspect' @{ parameters = @{}; read = $read }
        if ($tables.items.Count -ne 1 -or $tables.items[0].description -cne $record.fixtureMarker) { throw '夹具唯一标记不符' }
        $read.section = 'measures'; $read.includeExpressions = $true
        $measures = Invoke-ProbeTool $client 'desktop_project_inspect' @{ parameters = @{}; read = $read }
        if ($measures.items.Count -ne 1 -or $measures.items[0].name -cne 'MCP Probe' -or !$measures.items[0].expression.Contains('VAR Result = 42')) { throw '度量表达式不符' }
        # 独立 ADOMD 连接只计算固定探针, 不刷新业务数据或发送 processing 命令.
        [void][Reflection.Assembly]::LoadFrom((Join-Path $PublishRoot 'Microsoft.AnalysisServices.AdomdClient.dll'))
        $connection = [Microsoft.AnalysisServices.AdomdClient.AdomdConnection]::new('Data Source=localhost:' + $instance[0].port + ';Connect Timeout=15')
        try {
            $connection.Open()
            $command = $connection.CreateCommand()
            try {
                $command.CommandText = 'EVALUATE ROW ( "Probe", [MCP Probe] )'
                $command.CommandTimeout = 15
                $reader = $command.ExecuteReader()
                try {
                    if (!$reader.Read()) { throw '无计算结果' }
                    $value = $reader.GetValue(0)
                    if ($reader.Read() -or $value -ne 42) { throw '计算结果不是唯一42' }
                    $result.calculatedValue = $value
                } finally { $reader.Dispose() }
            } finally { $command.Dispose() }
        } finally { $connection.Dispose() }
        $final = Invoke-ProbeTool $client 'desktop_project_inspect' @{ parameters = @{} }
        if ($final.projectId -cne $opened.projectId -or $final.baselineId -cne $opened.baselineId) { throw '查询前后权威基线不同' }
        $result.baselineId = $final.baselineId
        $result.expression = $measures.items[0].expression
        if ($Stage -ceq 'AfterReopen' -and !@($before | Where-Object { $_.baselineId -ceq $result.baselineId -and $_.expression -ceq $result.expression }).Count) { throw '重开后的完整模型基线或表达式与提交后不一致' }
        $null = Invoke-ProbeTool $client 'desktop_project_disconnect' @{ parameters = @{} }
        $result.status = 'PASS'
    } catch { $result.failure = $_.Exception.Message }
    finally {
        foreach ($client in $clients) {
            try { if (!$client.Process.HasExited) { Close-ProbeClient $client }; Wait-ProbeTask $client.Errors 10 | Set-Content (Join-Path $evidence 'stderr.txt') }
            catch { $result.cleanupErrors += $_.Exception.Message }
            finally { $client.Process.Dispose() }
        }
        foreach ($hostEntry in $hosts) { $hostEntry.Process.Dispose() }
        if ($result.cleanupErrors.Count) { $result.status = 'FAIL' }
        $result | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $evidence 'result.json')
    }
    Write-Output ($result.status + ' ' + $Stage + ' 独立计算/重开; 证据: ' + $evidence)
    if ($result.status -cne 'PASS') { exit 4 }
} finally { $acceptanceLease.Dispose() }
