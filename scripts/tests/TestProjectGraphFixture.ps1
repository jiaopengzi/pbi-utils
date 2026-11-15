# FilePath    : pbi-utils\scripts\tests\TestProjectGraphFixture.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 验证模拟 SDK 收到的测试项目图参数, 解析其私有回执路径.

<# .SYNOPSIS 核对原生模拟边界收到的测试图、有限并发和唯一 TRX 参数, 返回项目和报告名, 不运行工具. #>
function Get-TestProjectGraphFixture([string]$File, [string[]]$Arguments) {
    if ($File -cne 'dotnet' -or $Arguments[0] -cne 'msbuild' -or [IO.Path]::GetFileName($Arguments[1]) -cne 'CSharpTestProjects.proj' -or
        @($Arguments -cmatch '^-maxcpucount:[12]$').Count -ne 1 -or $Arguments -cnotcontains '-nodeReuse:false' -or $Arguments -match '(?i)restore') {
        throw 'C# 测试必须使用受限的 SDK 测试项目图, 不执行依赖还原'
    }
    $report = @($Arguments -clike '-p:PbiTestLogFile=*')
    if ($report.Count -ne 1 -or $report[0] -cnotmatch '^-p:PbiTestLogFile=(test-[0-9a-f]{32}\.trx)$') { throw '测试必须请求本轮唯一 TRX 文件名' }
    $reportName = $Matches[1]
    $projects = @($Arguments -clike '-p:PbiTestProjects=*')
    if ($projects.Count -ne 1 -or $projects[0] -cnotmatch '^-p:PbiTestProjects=([A-Za-z0-9.]+(?:\|[A-Za-z0-9.]+)*)$') { throw '测试项目未传给 SDK 测试图' }
    $projectNames = $Matches[1].Split('|')
    return [pscustomobject]@{ Name = $reportName; Projects = $projectNames; Root = Join-Path (Get-Location).Path 'artifacts/test-results' }
}

<# .SYNOPSIS 在每个测试项目目录写本轮合成回执, 解析和写入失败直接传播. #>
function Write-TestProjectGraphFixtureReports([pscustomobject]$Graph, [string]$Xml) {
    foreach ($project in $Graph.Projects) {
        $directory = Join-Path $Graph.Root $project
        $null = New-Item -ItemType Directory -Path $directory -Force
        [IO.File]::WriteAllText((Join-Path $directory $Graph.Name), $Xml)
    }
}
