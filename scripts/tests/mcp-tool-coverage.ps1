# FilePath    : pbi-utils\scripts\tests\mcp-tool-coverage.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 供隐藏宿主验收加载的全工具用例执行器, 区分业务成功、前置拒绝及写权限拦截.

<#
.SYNOPSIS 对已核验的独占隐藏宿主运行显式用例清单, 保存逐项结果并验证夹具没有变化.
.PARAMETER Client mcp-background-smoke 创建的只读 CLI, 禁止传入开启写入的进程.
.PARAMETER Session 已通过进程/父子身份核验的测试会话, 每个桌面调用显式绑定.
.OUTPUTS 计数摘要; 发现/权限漂移、协议或业务不符合预期均失败, 不修改预期来容纳任意错误.
#>
function Invoke-AllToolCases($Client, $Session) {
    $manifest = Get-Content -LiteralPath (Join-Path $root 'tests/contracts/mcp-tool-cases.json') -Raw | ConvertFrom-Json -AsHashtable
    $cases = @($manifest.cases)
    $discovery = Invoke-ProbeTool $Client '' @{} -Method 'tools/list'
    $expectedNames = @($cases.name | Sort-Object -CaseSensitive)
    $actualNames = @($discovery.tools.name | Sort-Object -CaseSensitive)
    if (($expectedNames | Select-Object -Unique).Count -ne $cases.Count -or
        (Compare-Object $expectedNames $actualNames -CaseSensitive)) { throw '工具集合与显式测试清单不一致; 新工具必须增加用例' }
    foreach ($case in $cases) {
        $tool = @($discovery.tools | Where-Object { $_.name -ceq $case.name })[0]
        if ($tool.annotations.readOnlyHint -ne $case.readOnly) { throw ('工具权限与预期不一致: ' + $case.name) }
    }
    $info = Invoke-ProbeTool $Client 'server_info' @{}
    if ($info.allowWrite -ne $false) { throw '全量安全验收要求默认只读客户端' }
    $templates = Invoke-ProbeTool $Client 'scaffold_templates' @{}
    $blank = @($templates.templates | Where-Object { $_.id -ceq 'builtin:blank' })
    if ($blank.Count -ne 1) { throw '缺少唯一内置空白模板' }
    $fixtureRoot = Join-Path $evidence 'fixture'
    $fixture = Get-Content -LiteralPath (Join-Path $root 'tests/contracts/mcp-fixture.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($relative in $fixture.Keys) {
        $path = Join-Path $fixtureRoot $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force
        $fixture[$relative] | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding utf8
    }
    $before = @(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse | Get-FileHash | Sort-Object Path | Select-Object Path, Hash | ConvertTo-Json -Compress)
    $results = [Collections.Generic.List[object]]::new()
    foreach ($case in $cases) {
        $row = [ordered]@{ name = $case.name; category = $case.category; status = 'FAIL'; expectedError = $case.errorCode; note = $case.note }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            # 用例 JSON 每次深拷贝, 占位符只能指向本次固定夹具路径, 不接受任意命令或表达式.
            $json = $case.arguments | ConvertTo-Json -Depth 30
            $arguments = $json.Replace('"$templateBaseline"', ($blank[0].baseline | ConvertTo-Json -Compress)) | ConvertFrom-Json -AsHashtable
            foreach ($key in @($arguments.Keys)) {
                if ($arguments[$key] -ceq '$fixture') { $arguments[$key] = Join-Path $fixtureRoot 'test.pbip' }
                if ($arguments[$key] -ceq '$empty') { $arguments[$key] = Join-Path $evidence 'empty' }
            }
            if ($case.name.StartsWith('desktop_') -and $case.name -cne 'desktop_sessions') { $arguments.sessionId = $Session.id }
            $value = Invoke-ProbeTool $Client $case.name $arguments $case.errorCode
            if (!$case.errorCode) {
                if ($case.resultKind -ceq 'object' -and $value -isnot [Collections.IDictionary]) { throw '成功结果必须为对象' }
                if ($case.resultKind -ceq 'array' -and $value -isnot [array]) { throw '成功结果必须为数组' }
            }
            foreach ($field in $case.fields) {
                if ($value -isnot [Collections.IDictionary] -or !$value.Contains($field)) { throw ('缺少业务结果字段: ' + $field) }
            }
            foreach ($field in $case.equals.Keys) {
                if (!$value.Contains($field) -or (ConvertTo-Json -InputObject $value[$field] -Depth 30 -Compress) -cne (ConvertTo-Json -InputObject $case.equals[$field] -Depth 30 -Compress)) {
                    throw ('业务结果不符合预期: ' + $field)
                }
            }
            foreach ($field in $case.nonEmpty) {
                $item = $value[$field]
                if (($item -isnot [string] -and $item -isnot [array]) -or ($item -is [string] -and [string]::IsNullOrWhiteSpace($item)) -or ($item -is [array] -and $item.Count -eq 0)) { throw ('业务结果字段必须为非空文本或数组: ' + $field) }
            }
            if (!$case.errorCode -and $null -eq $value) { throw '成功结果不能为空' }
            $row.status = 'PASS'
        }
        catch {
            $row.failure = $_.Exception.Message
            # 超时或断开后不继续向同一流叠加请求; 未执行项明确为 UNRUN.
            if (!$Client.Idle -or $Client.Process.HasExited) {
                $row.durationMs = $timer.ElapsedMilliseconds
                $results.Add([pscustomobject]$row)
                break
            }
        }
        $row.durationMs = $timer.ElapsedMilliseconds
        $results.Add([pscustomobject]$row)
        Write-Output ("{0} {1} ({2})" -f $row.status, $row.name, $row.category) | Out-Host
    }
    foreach ($case in $cases | Where-Object { $_.name -cnotin $results.name }) {
        $results.Add([pscustomobject]@{ name = $case.name; category = $case.category; status = 'UNRUN'; note = '协议流未恢复, 未继续调用' })
    }
    $after = @(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse | Get-FileHash | Sort-Object Path | Select-Object Path, Hash | ConvertTo-Json -Compress)
    $unchanged = ($before -join '') -ceq ($after -join '')
    $summary = [ordered]@{
        total = $cases.Count; passed = @($results | Where-Object status -eq 'PASS').Count
        failed = @($results | Where-Object status -eq 'FAIL').Count; unrun = @($results | Where-Object status -eq 'UNRUN').Count
        fixtureUnchanged = $unchanged; modelWrites = 'UNRUN'; liveCloud = 'UNRUN'
        categories = @($results | Group-Object category | ForEach-Object { @{ name = $_.Name; count = $_.Count; passed = @($_.Group | Where-Object status -eq 'PASS').Count } })
    }
    if (!$unchanged -or $summary.unrun -gt 0) { $summary.failed++ }
    @{ summary = $summary; cases = $results } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $evidence 'tool-coverage.json') -Encoding utf8
    return $summary
}
