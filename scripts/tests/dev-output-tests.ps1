# FilePath    : pbi-utils\scripts\tests\dev-output-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 使用隔离 framework-dependent 构建夹具验证开发目录组装、依赖闭包、冲突与占用拒绝.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$savedOutputEncoding = [Console]::OutputEncoding
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $root 'scripts/DevelopmentOutputs.psm1') -Force
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-dev-output-' + [Guid]::NewGuid().ToString('N'))

<# .SYNOPSIS 断言真实文件与返回值事实, 失败保留具体场景. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 写测试自身的 JSON 配置或依赖描述, 文件错误直接失败. #>
function Write-TestJson([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8 }
<# .SYNOPSIS 创建三个独立 FDD 构建输出, 包含本机原生依赖/本地化资源和明确不可递归复制的额外文件. #>
function New-TestCase([string]$Name) {
    $case = Join-Path $sandbox $Name
    $null = New-Item -ItemType Directory -Path $case -Force
    Set-Content -LiteralPath (Join-Path $case 'Directory.Build.props') -Value '<Project><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>'
    foreach ($pair in @(@('Cli', 'pbi-utils'), @('Host', 'PbiUtils.Host'), @('Desktop', 'PbiUtils.Desktop'))) {
        $directory = Join-Path $case "src/PbiUtils.$($pair[0])/bin/Debug/net10.0-windows"
        $null = New-Item -ItemType Directory -Path $directory -Force
        $entry = $pair[1]
        foreach ($suffix in @('exe', 'dll', 'pdb')) { Set-Content -LiteralPath (Join-Path $directory "$entry.$suffix") -Value "$entry $suffix" }
        Set-Content -LiteralPath (Join-Path $directory 'Shared.dll') -Value 'identical shared dependency'
        foreach ($relative in @('zh-Hans/Shared.resources.dll', 'runtimes/win-x64/native/Native.dll', 'win-x64/unrelated.bin')) {
            $target = Join-Path $directory $relative
            $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
            Set-Content -LiteralPath $target -Value $relative
        }
        $libraries = @{ "$entry/1.0.0" = @{ type = 'project'; sha512 = '' }; 'Shared/1.0.0' = @{ type = 'package'; sha512 = 'same' } }
        $target = @{
            "$entry/1.0.0" = @{ dependencies = @{ Shared = '1.0.0' }; runtime = @{ "$entry.dll" = @{} } }
            'Shared/1.0.0' = @{
                runtime = @{ 'lib/net10.0/Shared.dll' = @{ assemblyVersion = '1.0.0.0'; fileVersion = '1.0.0.0' } }
                resources = @{ 'lib/net10.0/zh-Hans/Shared.resources.dll' = @{ locale = 'zh-Hans' } }
                runtimeTargets = @{ 'runtimes/win-x64/native/Native.dll' = @{ rid = 'win-x64'; assetType = 'native' }; 'runtimes/linux-x64/native/Unused.so' = @{ rid = 'linux-x64'; assetType = 'native' } }
            }
        }
        Write-TestJson (Join-Path $directory "$entry.deps.json") @{ runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0' }; targets = @{ '.NETCoreApp,Version=v10.0' = $target }; libraries = $libraries }
        $frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.0' })
        if ($pair[0] -ne 'Cli') { $frameworks += @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' } }
        Write-TestJson (Join-Path $directory "$entry.runtimeconfig.json") @{ runtimeOptions = @{ tfm = 'net10.0'; frameworks = $frameworks } }
    }
    return $case
}
<# .SYNOPSIS 取得已创建夹具中的固定入口文件, 不接受任意路径. #>
function EntryPath([string]$Case, [string]$Project, [string]$Name) { return Join-Path $Case "src/PbiUtils.$Project/bin/Debug/net10.0-windows/$Name" }
<# .SYNOPSIS 验证预检拒绝发生在首次复制入口之前, 源夹具保持供诊断. #>
function Assert-Rejected([string]$Case, [string]$Pattern) {
    $failure = $null
    try { $null = Set-PbiDevelopmentOutputs $Case } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.Exception.Message -match $Pattern) "未按预期拒绝开发输出: $Pattern; $failure"
    Assert (![IO.File]::Exists((EntryPath $Case 'Desktop' 'pbi-utils.exe'))) '预检失败不得复制 CLI 入口'
}

try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $case = New-TestCase 'complete'
    $cliConfig = Get-Content -LiteralPath (EntryPath $case 'Cli' 'pbi-utils.runtimeconfig.json') -Raw
    $executable = Set-PbiDevelopmentOutputs $case
    Assert ($executable -ceq (EntryPath $case 'Desktop' 'PbiUtils.Desktop.exe')) '必须返回既有 Desktop/bin 的真实 apphost'
    foreach ($name in @('pbi-utils.exe', 'pbi-utils.dll', 'pbi-utils.deps.json', 'pbi-utils.runtimeconfig.json', 'PbiUtils.Host.exe', 'PbiUtils.Host.dll', 'PbiUtils.Host.deps.json', 'PbiUtils.Host.runtimeconfig.json')) {
        Assert ([IO.File]::Exists((EntryPath $case 'Desktop' $name))) "缺少同目录入口: $name"
    }
    Assert ((Get-Content -LiteralPath (EntryPath $case 'Desktop' 'pbi-utils.runtimeconfig.json') -Raw) -ceq $cliConfig) 'FDD runtimeconfig 不得改成自包含框架集合'
    Assert (![IO.File]::Exists((EntryPath $case 'Desktop' 'unrelated.bin'))) '不得递归混入额外构建产物'
    Set-Content -LiteralPath (EntryPath $case 'Cli' 'pbi-utils.dll') -Value 'next CLI build'
    $null = Set-PbiDevelopmentOutputs $case
    Assert ((Get-Content -LiteralPath (EntryPath $case 'Desktop' 'pbi-utils.dll') -Raw).Trim() -ceq 'next CLI build') '重复组装必须更新明确入口'

    $case = New-TestCase 'content'
    Set-Content -LiteralPath (EntryPath $case 'Host' 'Shared.dll') -Value 'different dependency'
    Assert-Rejected $case '内容冲突'

    $case = New-TestCase 'version'
    $path = EntryPath $case 'Host' 'PbiUtils.Host.deps.json'
    (Get-Content -LiteralPath $path -Raw).Replace('Shared/1.0.0', 'Shared/2.0.0') | Set-Content -LiteralPath $path
    Assert-Rejected $case '版本或来源冲突|闭包缺失'

    $case = New-TestCase 'dependency-missing'
    $path = EntryPath $case 'Host' 'PbiUtils.Host.deps.json'
    $deps = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $deps.targets[$deps.runtimeTarget.name]['PbiUtils.Host/1.0.0'].dependencies['Missing'] = '1.0.0'
    Write-TestJson $path $deps
    Assert-Rejected $case '闭包缺失'

    $case = New-TestCase 'native-missing'
    Remove-Item -LiteralPath (EntryPath $case 'Host' 'runtimes/win-x64/native/Native.dll')
    Assert-Rejected $case '资产文件缺失'

    $case = New-TestCase 'framework'
    $path = EntryPath $case 'Host' 'PbiUtils.Host.runtimeconfig.json'
    (Get-Content -LiteralPath $path -Raw).Replace('10.0.0', '10.0.1') | Set-Content -LiteralPath $path
    Assert-Rejected $case '框架版本不一致'

    $case = New-TestCase 'locked'
    $handle = [IO.File]::Open((EntryPath $case 'Desktop' 'PbiUtils.Desktop.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Assert-Rejected $case '占用或不可写' } finally { $handle.Dispose() }
    Write-Output 'PASS 开发输出同目录入口、FDD闭包、冲突与占用拒绝'
}
finally {
    [Console]::OutputEncoding = $savedOutputEncoding
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-dev-output-[0-9a-f]{32}$') { throw '开发输出测试清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
