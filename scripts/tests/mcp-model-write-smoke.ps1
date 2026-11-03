# FilePath    : pbi-utils\scripts\tests\mcp-model-write-smoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 从内置模板建立独占测试模型, 核验进程和文件身份后通过正式 MCP 提交并权威回读.

<#
.SYNOPSIS 两阶段隔离 Desktop 写入验收, 不选择或修改已有用户模型.
.PARAMETER Prepare 新建本轮夹具并用已有 PBIP 文件关联启动 Desktop; 若有任何 Desktop 或产品宿主则停止.
.PARAMETER FixtureRecord Prepare 产生的 fixture.json 绝对路径; 核验原进程/文件/模型探针后才允许写入.
.PARAMETER PublishRoot 真实自包含产物目录.
.OUTPUTS 唯一证据目录及结果. Run 后保留测试 Desktop 供计算和手动保存验收, 不强杀宿主或关闭用户窗口.
#>
param(
    [switch]$Prepare,
    [string]$FixtureRecord,
    [string]$PublishRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/build/release/win-x64/without-dotnet')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceUserDataLease.ps1')
$acceptanceLease = Enter-AcceptanceUserDataLease
try {
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
if (!$IsWindows) { throw '仅支持 Windows 的隔离 Desktop 验收' }
if ($Prepare) {
    if ($FixtureRecord) { throw 'Prepare 不接受既有夹具路径' }
    if (@([Diagnostics.Process]::GetProcessesByName('PBIDesktop')).Count -or (@([Diagnostics.Process]::GetProcessesByName('PbiUtils.Desktop')).Count -or @([Diagnostics.Process]::GetProcessesByName('PbiUtils.Host')).Count)) {
        throw '存在用户 Desktop 或产品宿主; 本次不启动或复用任何模型'
    }
    $evidence = Join-Path $root ('artifacts/evidence/mcp-write-model-' + [Guid]::NewGuid().ToString('N'))
    $fixtureRoot = Join-Path $evidence 'fixture'
    $null = New-Item -ItemType Directory -Path $fixtureRoot -Force
    Copy-Item -LiteralPath (Join-Path $root 'src/PbiUtils.Infrastructure/Scaffolding/Assets/pbip/blank') -Destination (Join-Path $fixtureRoot 'model') -Recurse
    $project = Join-Path $fixtureRoot 'model/blank.pbip'
    $definition = Join-Path $fixtureRoot 'model/blank.SemanticModel/definition'
    $fixtureMarker = 'MCP isolated ' + (Split-Path $evidence -Leaf)
    $null = New-Item -ItemType Directory -Path (Join-Path $definition 'tables') -Force
    @'
table McpWriteFixture
    measure 'MCP Probe' = 1
        formatString: 0

    column ProbeValue
        dataType: int64
        summarizeBy: none
        isNameInferred
        sourceColumn: [ProbeValue]

    partition McpWriteFixture = calculated
        mode: import
        source = ROW ( "ProbeValue", 1 )
'@ | Set-Content -LiteralPath (Join-Path $definition 'tables/McpWriteFixture.tmdl') -Encoding utf8
    $tableFile = Join-Path $definition 'tables/McpWriteFixture.tmdl'
    $tableText = [IO.File]::ReadAllText($tableFile).Replace('table McpWriteFixture', "/// $fixtureMarker`ntable McpWriteFixture")
    [IO.File]::WriteAllText($tableFile, $tableText, [Text.UTF8Encoding]::new($false))
    Add-Content -LiteralPath (Join-Path $definition 'model.tmdl') -Value "`nref table McpWriteFixture" -Encoding utf8
    $start = [DateTime]::UtcNow
    Start-Process -FilePath $project -WorkingDirectory $root -WindowStyle Hidden
    $deadline = $start.AddSeconds(45)
    do {
        $candidates = @(Get-CimInstance Win32_Process -Filter "Name = 'PBIDesktop.exe'" | Where-Object {
            $_.CreationDate.ToUniversalTime() -ge $start.AddSeconds(-1) -and $_.CommandLine.Contains($project, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($candidates.Count -eq 1) { break }
        if ($candidates.Count -gt 1) { throw '出现多个测试模型进程, 不猜测目标' }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($candidates.Count -ne 1) { throw '未观察到绑定新夹具的唯一 Desktop 进程' }
    $desktop = [Diagnostics.Process]::GetProcessById($candidates[0].ProcessId)
    try {
        $record = [ordered]@{ schemaVersion = 1; project = $project; projectSha256 = (Get-FileHash -LiteralPath $project).Hash; fixtureMarker = $fixtureMarker; desktopPid = $desktop.Id; desktopStartTicks = $desktop.StartTime.ToUniversalTime().Ticks.ToString(); startedUtc = $start.ToString('o') }
        $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'fixture.json') -Encoding utf8
    }
    finally { $desktop.Dispose() }
    Write-Output ('夹具已启动. 待 Desktop 加载完成后执行: pwsh -NoProfile -File ./scripts/tests/mcp-model-write-smoke.ps1 -FixtureRecord "' + (Join-Path $evidence 'fixture.json') + '"')
    exit 0
}
if (!$FixtureRecord) { throw '需要 -Prepare 或其产生的 -FixtureRecord' }
$FixtureRecord = [IO.Path]::GetFullPath($FixtureRecord)
$evidence = Split-Path $FixtureRecord -Parent
$evidenceParent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/evidence'))
if ((Split-Path $evidence -Parent) -ine $evidenceParent -or (Split-Path $evidence -Leaf) -notmatch '^mcp-write-model-[0-9a-f]{32}$' -or (Split-Path $FixtureRecord -Leaf) -cne 'fixture.json') { throw '仅接受 Prepare 创建的本轮隔离夹具记录' }
if (Test-Path -LiteralPath (Join-Path $evidence 'result.json')) { throw '本轮夹具已有执行记录, 不覆盖证据或重放写入; 先核对原始结果' }
$record = Get-Content -LiteralPath $FixtureRecord -Raw | ConvertFrom-Json -AsHashtable
$expectedPath = Join-Path $evidence 'fixture/model/blank.pbip'
if ($record.schemaVersion -ne 1 -or $record.project -ine $expectedPath -or (Get-FileHash -LiteralPath $expectedPath).Hash -cne $record.projectSha256) { throw '夹具文件身份或基线不一致' }
$desktop = [Diagnostics.Process]::GetProcessById($record.desktopPid)
try {
    $observed = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $desktop.Id)
    if ($desktop.ProcessName -cne 'PBIDesktop' -or $desktop.StartTime.ToUniversalTime().Ticks.ToString() -cne $record.desktopStartTicks -or !$observed.CommandLine.Contains($expectedPath, [StringComparison]::OrdinalIgnoreCase)) { throw '原测试 Desktop 身份不匹配' }
}
finally { $desktop.Dispose() }
if ((@([Diagnostics.Process]::GetProcessesByName('PbiUtils.Desktop')).Count -or @([Diagnostics.Process]::GetProcessesByName('PbiUtils.Host')).Count)) { throw '存在其他产品宿主, 不复用用户工作区' }
$clients = [Collections.Generic.List[object]]::new()
$hosts = [Collections.Generic.List[object]]::new()
$initialHostIds = @()
. (Join-Path $PSScriptRoot 'McpProbeClient.ps1')
$result = [ordered]@{ status = 'FAIL'; exitCode = 4; modelWrites = 0; writeState = 'not_attempted'; authoritativeReadback = $false; cleanupErrors = @(); fixtureRecord = $FixtureRecord; simulated = $false; fileSaved = $false }
try {
    $client = Start-ProbeClient 'model-write' -AllowWrite
    $session = Get-ProbeSession $client
    $argsRoot = @{ sessionId = $session.id; parameters = @{} }
    $discovery = Invoke-ProbeTool $client 'desktop_powerbi_instances' $argsRoot
    $instance = @($discovery.instances | Where-Object { $_.processId -eq $record.desktopPid })
    if ($instance.Count -ne 1 -or [string]::IsNullOrWhiteSpace($instance[0].instanceId)) { throw '测试引擎未就绪或身份不唯一' }
    $connected = Invoke-ProbeTool $client 'desktop_project_connectLocal' @{ sessionId = $session.id; parameters = @{ processId = $record.desktopPid.ToString(); instanceId = $instance[0].instanceId } }
    # 文件关联与模型探针必须同时匹配, 不因名称或 PID 单独匹配就写入.
    if ($connected.connection.kind -cne 'desktop' -or $connected.connection.instanceId -cne $instance[0].instanceId) { throw '连接结果没有绑定隔离 Desktop 引擎, 不执行写入' }
    $tablePage = Invoke-ProbeTool $client 'desktop_project_inspect' @{ sessionId = $session.id; parameters = @{}; read = @{ section = 'tables'; table = 'McpWriteFixture'; projectId = $connected.projectId; baselineId = $connected.baselineId } }
    $table = @($tablePage.items | Where-Object { $_.name -ceq 'McpWriteFixture' -and $_.description -ceq $record.fixtureMarker })
    if ($table.Count -ne 1 -or $record.fixtureMarker -cne ('MCP isolated ' + (Split-Path $evidence -Leaf))) { throw '模型缺少本轮唯一夹具标记, 不执行写入' }
    $measurePage = Invoke-ProbeTool $client 'desktop_project_inspect' @{ sessionId = $session.id; parameters = @{}; read = @{ section = 'measures'; table = 'McpWriteFixture'; includeExpressions = $true; projectId = $connected.projectId; baselineId = $connected.baselineId } }
    $probe = @($measurePage.items | Where-Object { $_.name -ceq 'MCP Probe' -and $_.table -ceq 'McpWriteFixture' })
    if ($probe.Count -ne 1 -or $probe[0].expression.Trim() -cne '1') { throw '测试探针初始定义不匹配, 不执行写入' }
    $connected | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $evidence 'before.json') -Encoding utf8
    # 发出写请求后, 通信或回读失败不代表未提交; 不重放, 保留原始帧和可靠回执供人工核验.
    $result.writeState = 'unknown'; $result.modelWrites = $null
    $updated = Invoke-ProbeTool $client 'desktop_model_edit' @{ sessionId = $session.id; parameters = @{ operation = 'measure.set'; objectName = 'MCP Probe'; property = 'expression'; value = "`n    VAR Result = 42`n    RETURN Result"; table = 'McpWriteFixture'; pageId = '' } }
    $updated | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $evidence 'write-response.json') -Encoding utf8
    if ($updated.saveResult.modelSaved) { $result.writeState = 'committed'; $result.modelWrites = 1 }
    $updatedMeasures = Invoke-ProbeTool $client 'desktop_project_inspect' @{ sessionId = $session.id; parameters = @{}; read = @{ section = 'measures'; table = 'McpWriteFixture'; includeExpressions = $true; projectId = $updated.projectId; baselineId = $updated.baselineId } }
    $probe = @($updatedMeasures.items | Where-Object { $_.name -ceq 'MCP Probe' -and $_.table -ceq 'McpWriteFixture' })
    if ($probe.Count -ne 1 -or !$probe[0].expression.Contains('VAR Result = 42') -or !$updated.saveResult.modelSaved -or !$updated.saveResult.desktopSynchronized -or $updated.saveResult.fileSaved) { throw '模型提交或独立回读事实不完整' }
    $readback = Invoke-ProbeTool $client 'desktop_project_inspect' $argsRoot
    if ($readback.projectId -cne $connected.projectId -or $readback.baselineId -cne $updated.baselineId -or $updated.baselineId -ceq $connected.baselineId) { throw '独立模型回读或目标绑定不一致' }
    $readback | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $evidence 'after.json') -Encoding utf8
    $null = Invoke-ProbeTool $client 'desktop_project_disconnect' $argsRoot
    Close-ProbeClient $client
    $result.status = 'PASS'; $result.exitCode = 0
    $result.tools = @('desktop_project_connectLocal', 'desktop_model_edit', 'desktop_project_disconnect')
    $result.authoritativeReadback = $true
}
catch { $result.failure = $_.Exception.Message }
finally {
    foreach ($client in $clients) {
        try {
            if (!$client.Process.HasExited) { Close-ProbeClient $client }
            Wait-ProbeTask $client.Errors 10 | Set-Content -LiteralPath (Join-Path $evidence 'stderr.txt') -Encoding utf8
        }
        catch { $result.cleanupErrors += $_.Exception.Message }
        finally { $client.Process.Dispose() }
    }
    foreach ($hostEntry in $hosts) { $hostEntry.Process.Dispose() }
    if ($result.cleanupErrors.Count) { $result.status = 'FAIL'; $result.exitCode = 4 }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
}
Write-Output ($result.status + ' 隔离 Desktop MCP 写入; 证据: ' + $evidence + '; 测试 Desktop 保留, 计算/手动保存/关闭需另行验收')
exit $result.exitCode

}
finally { $acceptanceLease.Dispose() }
