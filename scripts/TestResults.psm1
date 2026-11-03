# FilePath    : pbi-utils\scripts\TestResults.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 有界读取本轮 VSTest TRX 回执, 验证真实执行及结构化失败, 保留跳过事实.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

<#
.SYNOPSIS 只接受唯一结构节点, 不以缺失或重复结构当成零错误.
.PARAMETER Parent 已有 XML 文档或父节点, 不加载外部资源.
.PARAMETER XPath 固定的受信结构路径, 不包含报告正文.
.PARAMETER Namespaces 当前 TRX 的固定命名空间映射.
.OUTPUTS 唯一的 XML 节点; 缺失或重复时抛 InvalidDataException.
#>
function Get-PbiTrxNode([Xml.XmlNode]$Parent, [string]$XPath, [Xml.XmlNamespaceManager]$Namespaces) {
    $nodes = $Parent.SelectNodes($XPath, $Namespaces)
    if ($nodes.Count -ne 1) { throw [IO.InvalidDataException]::new('TRX 缺少唯一的必需节点或包含重复结构') }
    return $nodes[0]
}

<#
.SYNOPSIS 解析 TRX 非负十进制计数, 不静默转换为零.
.PARAMETER Node 已核验的唯一 Counters 节点.
.PARAMETER Name 固定计数属性名称, 不读取其他数据.
.OUTPUTS Int64 计数; 缺失、负数、溢出或非数字时抛 InvalidDataException.
#>
function Get-PbiTrxCount([Xml.XmlNode]$Node, [string]$Name) {
    $attribute = $Node.Attributes[$Name]
    [long]$value = 0
    if ($null -eq $attribute -or ![long]::TryParse($attribute.Value, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
        throw [IO.InvalidDataException]::new('TRX 测试计数缺失或无效')
    }
    return $value
}

<#
.SYNOPSIS 核验本轮指定报告的真实执行事实, 不搜索 stdout/stderr 中的业务文字.
.PARAMETER Path 本次工具调用的唯一 TRX 路径, 不扫描或挑选历史 artifact.
.PARAMETER StartedAt 发起本次工具调用前的 UTC 时间, 允许两秒时钟精度差异.
.OUTPUTS Total、Executed、Passed、Skipped 计数, 其中 Skipped 按真实 NotExecuted 结果计算.
.NOTES 拒绝无报告、损坏、DTD、过期、零发现、全跳过、失败、未完成或不一致回执; 部分跳过由调用方标为 UNRUN.
#>
function Assert-PbiTestResults([string]$Path, [DateTimeOffset]$StartedAt) {
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -eq 0 -or $stream.Length -gt 64MB) { throw [IO.InvalidDataException]::new('TRX 文件为空或超过 64 MiB') }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 64MB
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
    if ($document.DocumentElement.LocalName -cne 'TestRun' -or $document.DocumentElement.NamespaceURI -cne 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') {
        throw [IO.InvalidDataException]::new('TRX 根节点或命名空间无效')
    }
    $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('r', $document.DocumentElement.NamespaceURI)
    $times = Get-PbiTrxNode $document '/r:TestRun/r:Times' $namespaces
    [DateTimeOffset]$runStart = [DateTimeOffset]::MinValue
    [DateTimeOffset]$runFinish = [DateTimeOffset]::MinValue
    if (![DateTimeOffset]::TryParse($times.GetAttribute('start'), [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$runStart) -or
        ![DateTimeOffset]::TryParse($times.GetAttribute('finish'), [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$runFinish) -or
        $runStart -lt $StartedAt.AddSeconds(-2) -or $runFinish -lt $runStart -or $runFinish -gt [DateTimeOffset]::UtcNow.AddSeconds(2)) {
        throw [IO.InvalidDataException]::new('TRX 不是本轮完成的测试报告或时间无效')
    }
    $summary = Get-PbiTrxNode $document '/r:TestRun/r:ResultSummary' $namespaces
    if ($summary.GetAttribute('outcome') -cnotin @('Completed', 'Passed')) { throw [IO.InvalidDataException]::new('TRX 测试运行未成功完成') }
    $counterNode = Get-PbiTrxNode $summary 'r:Counters' $namespaces
    $counts = @{}
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')) {
        $counts[$name] = Get-PbiTrxCount $counterNode $name
    }
    if ($counts.total -eq 0 -or $counts.executed -eq 0) { throw [IO.InvalidDataException]::new('TRX 没有发现或实际执行任何测试, 不能作为通过') }
    foreach ($name in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')) {
        if ($counts[$name] -ne 0) { throw [IO.InvalidDataException]::new('TRX 含失败、错误或未完成的测试计数') }
    }
    # xUnit 将正当跳过原因写入 NotExecuted 的 ErrorInfo; 只排除这一精确结果, 其他结构化错误仍拒绝.
    if ($document.SelectNodes('//r:ErrorInfo[not(ancestor::r:UnitTestResult[@outcome="NotExecuted"])]', $namespaces).Count) {
        throw [IO.InvalidDataException]::new('TRX 含结构化错误信息')
    }
    $null = Get-PbiTrxNode $document '/r:TestRun/r:Results' $namespaces
    $results = $document.SelectNodes('/r:TestRun/r:Results/r:UnitTestResult', $namespaces)
    [long]$passed = 0
    [long]$skipped = 0
    $skippedNames = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($result in $results) {
        switch -CaseSensitive ($result.GetAttribute('outcome')) {
            'Passed' { $passed++ }
            'NotExecuted' {
                $skipped++
                $testName = $result.GetAttribute('testName')
                if ($testName.Length -gt 0) {
                    if (!$skippedNames.ContainsKey($testName)) { $skippedNames[$testName] = 0 }
                    $skippedNames[$testName]++
                }
            }
            default { throw [IO.InvalidDataException]::new('TRX 含失败或未知的测试结果') }
        }
    }
    foreach ($info in $summary.SelectNodes('r:RunInfos/r:RunInfo', $namespaces)) {
        $outcome = $info.GetAttribute('outcome')
        if ($outcome -ceq 'Warning') {
            # 当前锁定的 xUnit runner 将 SKIP 记为平台 Warning, 仅按真实 NotExecuted 名称逐条关联放行.
            $text = Get-PbiTrxNode $info 'r:Text' $namespaces
            $match = [Text.RegularExpressions.Regex]::Match($text.InnerText,
                '\A\[xUnit\.net [0-9]+:[0-9]{2}:[0-9]{2}\.[0-9]{2}\][ \t]+(?<name>.+) \[SKIP\]\z',
                [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromSeconds(1))
            $name = $match.Groups['name'].Value
            if (!$match.Success -or !$skippedNames.ContainsKey($name) -or $skippedNames[$name] -eq 0) {
                throw [IO.InvalidDataException]::new('TRX 含未关联真实跳过结果的运行器或适配器警告')
            }
            $skippedNames[$name]--
        } elseif ($outcome -cnotin @('Information', 'Passed', 'Completed')) {
            throw [IO.InvalidDataException]::new('TRX 含运行器或适配器错误')
        }
    }
    # xUnit 跳过结果存在, 但 VSTest 的 notExecuted 计数可能仍为 0; 按结果而非该计数证明覆盖.
    if ($results.Count -ne $counts.total -or $passed -ne $counts.passed -or $passed -ne $counts.executed -or
        $passed + $skipped -ne $counts.total -or $counts.notExecuted -gt $skipped) {
        throw [IO.InvalidDataException]::new('TRX 汇总计数与实际测试结果不一致')
    }
    return [pscustomobject]@{ Total = $counts.total; Executed = $counts.executed; Passed = $passed; Skipped = $skipped }
}

Export-ModuleMember -Function Assert-PbiTestResults
