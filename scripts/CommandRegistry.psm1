# FilePath    : pbi-utils\scripts\CommandRegistry.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 维护动作允许列表, 参数校验, 工具检查与统一执行边界.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$script:ValidatedTools = $null
$script:FrontendValidation = $null
$script:Projects = @('src/PbiUtils.Core', 'src/PbiUtils.Application', 'src/PbiUtils.Infrastructure', 'src/PbiUtils.Windows', 'src/PbiUtils.Host', 'src/PbiUtils.Cli', 'tests/PbiUtils.UnitTests', 'tests/PbiUtils.Cli.Tests')
$script:WindowsProjects = @('src/PbiUtils.Desktop', 'tests/PbiUtils.Desktop.Tests')
$script:Registry = @(
    @{ Name = 'help'; Parameters = @(); Script = ''; Description = '查看帮助'; Defaults = @{}; Requires = @() }
    @{ Name = 'devDesktop'; Parameters = @(); Script = 'dev.ps1'; Description = '真实桌面开发'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'devWeb'; Parameters = @(); Script = 'dev.ps1'; Description = '浏览器 mock 预览'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'setupC#'; Parameters = @('Scope'); Script = 'setup.ps1'; Description = '锁定还原 C#'; Defaults = @{}; Requires = @('dotnet') }
    @{ Name = 'setupVue'; Parameters = @(); Script = 'setup.ps1'; Description = '锁定还原 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'setupAll'; Parameters = @('Scope'); Script = 'setup.ps1'; Description = '锁定还原 All'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'buildC#'; Parameters = @('Scope', 'Configuration', 'DotNetMode'); Script = 'build.ps1'; Description = '构建 C# (按配置和 .NET 模式输出)'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet') }
    @{ Name = 'buildVue'; Parameters = @(); Script = 'build.ps1'; Description = '构建 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'buildAll'; Parameters = @('Scope', 'Configuration', 'DotNetMode'); Script = 'build.ps1'; Description = '构建 All (按配置和 .NET 模式输出)'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'testC#'; Parameters = @('Scope', 'Configuration'); Script = 'test.ps1'; Description = '测试 C#'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet') }
    @{ Name = 'testVue'; Parameters = @(); Script = 'test.ps1'; Description = '测试 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'testAll'; Parameters = @('Scope', 'Configuration'); Script = 'test.ps1'; Description = '测试 All'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'formatC#'; Parameters = @('Scope'); Script = 'format.ps1'; Description = '格式化 C#'; Defaults = @{}; Requires = @('dotnet') }
    @{ Name = 'formatVue'; Parameters = @(); Script = 'format.ps1'; Description = '格式化 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'formatAll'; Parameters = @('Scope'); Script = 'format.ps1'; Description = '格式化 All'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'checkC#'; Parameters = @('Scope'); Script = 'check.ps1'; Description = '只读检查 C# 格式/分析器'; Defaults = @{}; Requires = @('dotnet') }
    @{ Name = 'checkVue'; Parameters = @(); Script = 'check.ps1'; Description = '只读检查 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'checkAll'; Parameters = @('Scope'); Script = 'check.ps1'; Description = '只读检查 All'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'clean'; Parameters = @('WhatIf'); Script = 'clean.ps1'; Description = '白名单生成目录清理'; Defaults = @{ WhatIf = $false }; Requires = @() }
    @{ Name = 'all'; Parameters = @('Scope', 'Configuration', 'DotNetMode'); Script = ''; Description = 'C# 检查/测试/构建 → Vue 检查/测试/构建 → 交付选中模式并注册开发外部工具, 首错停止'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'package'; Parameters = @('DotNetMode'); Script = 'package.ps1'; Description = '从已有 Release All 产物生成 NSIS 安装包'; Defaults = @{ DotNetMode = 'without-dotnet' }; Requires = @('makensis') }
    @{ Name = 'release'; Parameters = @('DotNetMode'); Script = ''; Description = '完整 Release 门禁后生成 NSIS 安装包'; Defaults = @{ DotNetMode = 'without-dotnet' }; Requires = @('dotnet', 'node', 'pnpm', 'makensis') }
)
# 菜单编号按注册顺序自动生成, 插入或删除动作后仍从 0 连续递增.
for ($index = 0; $index -lt $script:Registry.Count; $index++) { $script:Registry[$index].Id = $index }

<# .SYNOPSIS 返回单一动作注册表, 菜单和分发共享此数据. #>
function Get-PbiRegistry { return $script:Registry }

<# .SYNOPSIS 返回选定范围的固定项目清单, 不扫描文件系统. #>
function Get-PbiProjects([string]$Scope) {
    $script:Projects
    if ($Scope -eq 'All') { $script:WindowsProjects }
}

<# .SYNOPSIS 按微软公开注册位置只读检测 Evergreen Runtime, 不安装或修改注册表. #>
function Get-PbiWebViewRuntime {
    $client = 'Microsoft/EdgeUpdate/Clients/{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $machine = if ([Environment]::Is64BitOperatingSystem) { 'HKLM:/SOFTWARE/WOW6432Node/' } else { 'HKLM:/SOFTWARE/' }
    foreach ($path in @(($machine + $client), ('HKCU:/SOFTWARE/' + $client))) {
        $value = Get-ItemProperty -LiteralPath $path -Name pv -ErrorAction SilentlyContinue
        $version = $null
        if ($value -and [Version]::TryParse([string]$value.pv, [ref]$version) -and $version -gt [Version]'0.0.0.0') { return $version.ToString() }
    }
    return 'missing'
}

<# .SYNOPSIS 抛出带稳定退出码的边界错误, 不退出调用 shell. #>
function Stop-Pbi([int]$Code, [string]$Message) {
    $exception = [InvalidOperationException]::new($Message)
    $exception.Data['ExitCode'] = $Code
    throw $exception
}

<# .SYNOPSIS 从注册表生成动作、配置及发布目录帮助, 无工具链依赖, 无返回值. #>
function Show-PbiHelp {
    Write-Host 'pbi-utils 开发入口: -Choice <编号|动作名> [-NonInteractive] | -List | -Help'
    foreach ($item in $script:Registry) {
        Write-Host ("{0,2}  {1,-12} {2}  参数: {3}" -f $item.Id, $item.Name, $item.Description, ($item.Parameters -join ', '))
    }
    Write-Host 'Scope: Windows C# 项目范围, 默认 All; Vue 动作不接 Scope. Configuration 默认 Debug; WhatIf 默认 false. C# 名称建议加引号, 菜单 q 退出.'
    Write-Host 'DotNetMode: with-dotnet 或 without-dotnet, 每次只生成选中模式. Debug 默认 with-dotnet; Release、package、release 默认 without-dotnet.'
    Write-Host 'Debug: .\run.ps1 -Choice all -Configuration Debug → artifacts/build/debug/win-x64.'
    Write-Host 'Release: .\run.ps1 -Choice all -Configuration Release [-DotNetMode with-dotnet] → artifacts/build/release/win-x64/<DotNetMode>.'
    Write-Host 'all -Scope All 成功交付后注册 Power BI 外部工具 pbi-utils (开发); 点击后连接当前报表模型. Core 不注册; buildAll/buildC#/release/package 不修改开发登记.'
    Write-Host '注册到系统 External Tools 目录, 必要时仅此步骤请求管理员授权; -NonInteractive 无写权限会明确失败, 请在管理员终端重跑. Power BI Desktop 重启后加载入口.'
    Write-Host '安装包: .\run.ps1 -Choice package 从已有 Release All 产物打包; -Choice release 先运行完整 Release 门禁再打包, 两者均可显式传 -DotNetMode.'
    Write-Host 'with-dotnet 包含 .NET 运行时; without-dotnet 需要目标电脑安装匹配的 x64 .NET Desktop Runtime. 桌面两包均需 Evergreen WebView2 Runtime.'
}

<# .SYNOPSIS 严格解析参数数组, 未知、缺值和重复参数返回 2. #>
function ConvertTo-PbiOptions([object[]]$Arguments) {
    $options = @{}
    $switches = @('List', 'Help', 'NonInteractive', 'WhatIf')
    $values = @('Choice', 'Scope', 'Configuration', 'DotNetMode')
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $token = [string]$Arguments[$i]
        if ($token -notmatch '^-(\w+)$') { Stop-Pbi 2 '未知参数格式, 请使用 -Help' }
        $key = $Matches[1]
        if ($options.ContainsKey($key)) { Stop-Pbi 2 '参数不可重复' }
        if ($key -in $switches) { $options[$key] = $true; continue }
        if ($key -notin $values) { Stop-Pbi 2 '未知参数, 请使用 -Help' }
        $i++
        if ($i -ge $Arguments.Count -or [string]$Arguments[$i] -match '^-') { Stop-Pbi 2 '参数缺少值' }
        $options[$key] = [string]$Arguments[$i]
    }
    return $options
}

<#
.SYNOPSIS 校验动作参数, 补充默认值并规范构建配置与部署模式大小写.
.PARAMETER Options 已解析的动作参数, 原位补充配置及模式默认值, 保证目录与清单命名一致.
.OUTPUTS 注册表动作; 参数无效返回边界错误 2, 平台不支持返回 3.
#>
function Resolve-PbiAction([hashtable]$Options) {
    $item = $script:Registry | Where-Object { $_.Name -eq $Options.Choice -or [string]$_.Id -eq $Options.Choice } | Select-Object -First 1
    if (!$item) { Stop-Pbi 2 '未知或空 Choice, 请使用 -List' }
    foreach ($key in $Options.Keys) {
        if ($key -notin @('Choice', 'NonInteractive') -and $key -notin $item.Parameters) { Stop-Pbi 2 "动作 $($item.Name) 不接受参数 $key" }
    }
    foreach ($key in $item.Defaults.Keys) { if (!$Options.ContainsKey($key)) { $Options[$key] = $item.Defaults[$key] } }
    if ('Scope' -in $item.Parameters -and !$Options.ContainsKey('Scope')) { $Options.Scope = 'All' }
    if ($Options.ContainsKey('Scope') -and $Options.Scope -notin @('Core', 'All')) { Stop-Pbi 2 'Scope 必须为 Core 或 All' }
    if ($Options.ContainsKey('Configuration')) {
        if ($Options.Configuration -notin @('Debug', 'Release')) { Stop-Pbi 2 'Configuration 必须为 Debug 或 Release' }
        $Options.Configuration = if ($Options.Configuration -eq 'Release') { 'Release' } else { 'Debug' }
    }
    if ('DotNetMode' -in $item.Parameters -and !$Options.ContainsKey('DotNetMode')) {
        $Options.DotNetMode = if ($Options.ContainsKey('Configuration') -and $Options.Configuration -eq 'Debug') { 'with-dotnet' } else { 'without-dotnet' }
    }
    if ($Options.ContainsKey('DotNetMode')) {
        if ($Options.DotNetMode -notin @('with-dotnet', 'without-dotnet')) { Stop-Pbi 2 'DotNetMode 必须为 with-dotnet 或 without-dotnet' }
        $Options.DotNetMode = $Options.DotNetMode.ToLowerInvariant()
    }
    if (!$IsWindows -and $item.Name -ne 'help') { Stop-Pbi 3 'pbi-utils 仅支持 Windows' }
    return $item
}

<#
.SYNOPSIS 按结果类别显示消息, 正常输出与错误输出保持各自通道.
.PARAMETER Message 要显示的开发诊断, 不包含凭据.
.PARAMETER Kind Info、Success、Warning 或 Error, 交互终端分别使用青、绿、黄、红色.
.PARAMETER ErrorStream 是否写 stderr, 默认仅 Error 使用, 原生输出可按来源指定.
.NOTES 重定向时不添加 ANSI 控制字符, 不改变工具的输出通道.
#>
function Write-PbiMessage([string]$Message, [string]$Kind = 'Info', [bool]$ErrorStream = ($Kind -eq 'Error')) {
    $writer = if ($ErrorStream) { [Console]::Error } else { [Console]::Out }
    $redirected = if ($ErrorStream) { [Console]::IsErrorRedirected } else { [Console]::IsOutputRedirected }
    $color = switch ($Kind) { 'Success' { 32 }; 'Warning' { 33 }; 'Error' { 31 }; default { 36 } }
    if (!$redirected -and !$env:NO_COLOR) { $writer.WriteLine("`e[${color}m$Message`e[0m") }
    else { $writer.WriteLine($Message) }
}

<#
.SYNOPSIS 精确修正 .NET 中文无需还原提示, 其他原生文本原样保留.
.PARAMETER File 工具名称, 仅 dotnet 的 restore 动作处理此提示.
.PARAMETER Arguments 原生命令参数数组.
.PARAMETER Line 原始输出行.
.OUTPUTS 修正后的提示或原始文本, 不影响工具退出码.
#>
function ConvertTo-PbiNativeText([string]$File, [string[]]$Arguments, [string]$Line) {
    if ($File -eq 'dotnet' -and $Arguments.Count -gt 0 -and $Arguments[0] -eq 'restore' -and $Line.Trim() -eq '所有项目均是最新的，无法还原。') {
        return '  所有项目依赖均为最新, 无需还原.'
    }
    return $Line
}

<#
.SYNOPSIS 按参数数组执行原生命令, 保留输出通道和原始非零退出码.
.PARAMETER File 固定工具名称或测试工具路径.
.PARAMETER Arguments 独立参数数组, 不拼接或执行任意命令字符串.
.NOTES 仅精确改写 .NET 的无需还原提示; 诊断不凭 stderr 或错误字样裁决成功.
#>
function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
    if (!(Get-Command $File -ErrorAction SilentlyContinue)) { Stop-Pbi 3 "缺少工具 $File, 请参阅 .docs/toolchain.md 官方安装来源" }
    # 原生命令的退出码在输出全部读完后裁决, 不受调用 shell 的原生错误偏好影响.
    $PSNativeCommandUseErrorActionPreference = $false
    & $File @Arguments 2>&1 | ForEach-Object {
        $isErrorStream = $_ -is [Management.Automation.ErrorRecord]
        $line = ConvertTo-PbiNativeText $File $Arguments ([string]$_)
        if ($line -match '^\s*(已成功生成[。.]|Build succeeded\.|0 个(警告|错误)\b|0 (Warning|Error)\(s\)|所有项目依赖均为最新|All projects are up-to-date for restore\.)') {
            Write-PbiMessage $line 'Success' $isErrorStream
        } elseif ($line -match '(?i)(\berror\s+[A-Z]+\d+\s*:|:\s*error\b|已失败生成|生成失败|Build FAILED|^\s*[1-9]\d* 个错误)') {
            Write-PbiMessage $line 'Error' $isErrorStream
        } elseif ($isErrorStream) {
            # 有些工具把正常进度或警告写 stderr, 保留原始诊断但不冒充失败.
            if (![Console]::IsErrorRedirected -and !$env:NO_COLOR) { [Console]::Error.WriteLine("`e[33m$line`e[0m") }
            else { [Console]::Error.WriteLine($line) }
        } elseif ($line -match '(?i)(\bwarning\b|警告|^\(!\))' -and $line -notmatch '^\s*0\s') {
            Write-PbiMessage $line 'Warning' $isErrorStream
        } else { [Console]::Out.WriteLine($line) }
    }
    $nativeCode = $LASTEXITCODE
    if ($nativeCode -ne 0) { Stop-Pbi $nativeCode "命令 $File 返回 $nativeCode" }
}

<#
.SYNOPSIS 从 package.json 的 engines.node 或 packageManager 读取工具精确版本.
.PARAMETER Tool 工具名称 node 或 pnpm, 仅由固定工具检查调用.
.OUTPUTS 工具的精确版本字符串.
.NOTES 配置缺失, 无法解析或不是精确版本时返回 3.
#>
function Get-PbiFrontendToolVersion([string]$Tool) {
    try { $package = Get-Content (Join-Path $script:Root 'web/package.json') -Raw | ConvertFrom-Json -AsHashtable }
    catch { Stop-Pbi 3 '无法读取 web/package.json 工具链版本配置' }
    if ($package -isnot [hashtable]) { Stop-Pbi 3 'web/package.json 工具链版本配置必须为 JSON 对象' }
    if ($Tool -eq 'pnpm') {
        if (!$package.ContainsKey('packageManager') -or $package.packageManager -isnot [string] -or $package.packageManager -notmatch '^pnpm@(\d+\.\d+\.\d+)$') {
            Stop-Pbi 3 'web/package.json 的 packageManager 必须声明 pnpm@精确版本'
        }
        return $Matches[1]
    }
    if (!$package.ContainsKey('engines') -or $package.engines -isnot [hashtable] -or !$package.engines.ContainsKey($Tool)) {
        Stop-Pbi 3 "web/package.json 的 engines.$Tool 必须声明精确版本"
    }
    $version = $package.engines[$Tool]
    if ($version -isnot [string] -or $version -notmatch '^\d+\.\d+\.\d+$') { Stop-Pbi 3 "web/package.json 的 engines.$Tool 必须声明精确版本" }
    return $version
}

<#
.SYNOPSIS 按仓库 global.json 校验 SDK, 失败时提供版本绑定检查建议.
.NOTES 仅查询当前 dotnet 可见的 SDK; 缺少绑定版本返回 3, 原生查询失败保留退出码. 不安装或改写配置.
#>
function Assert-PbiDotnetSdk {
    $configPath = Join-Path $script:Root 'global.json'
    try { $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable }
    catch { Stop-Pbi 3 "无法读取 SDK 配置: $configPath. 请检查 global.json 的 JSON 格式和 sdk.version 绑定." }
    if ($config -isnot [hashtable] -or !$config.ContainsKey('sdk') -or $config.sdk -isnot [hashtable] -or
        !$config.sdk.ContainsKey('version') -or $config.sdk.version -isnot [string] -or $config.sdk.version -notmatch '^\d+\.\d+\.\d+$') {
        Stop-Pbi 3 "SDK 配置无效: $configPath. 请将 global.json 的 sdk.version 绑定到完整的稳定版本号."
    }
    $expected = $config.sdk.version
    $guidance = "请开发者检查版本: 如需保持现有绑定, 安装 SDK $expected 或检查 PATH 是否指向正确的 dotnet; 如计划升级, 请确认是否更新 global.json 的 sdk.version 绑定, 更新后重新还原依赖并运行 all 验证. 本脚本不会自动安装 SDK 或修改 global.json."
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (!$command) { Stop-Pbi 3 "未找到 dotnet. global.json 绑定 SDK: $expected.`n$guidance" }
    $PSNativeCommandUseErrorActionPreference = $false
    $installedOutput = @(dotnet --list-sdks 2>&1)
    $listCode = $LASTEXITCODE
    $installedText = ($installedOutput | Out-String).Trim()
    if ($listCode -ne 0) { Stop-Pbi $listCode "无法查询已安装的 SDK. global.json 绑定 SDK: $expected.`ndotnet: $($command.Source)`n$installedText`n$guidance" }
    $versions = @($installedOutput | ForEach-Object {
        if ([string]$_ -match '^\s*(\S+)\s+\[') { $Matches[1] }
    })
    if (!$installedText) { $installedText = '(当前 dotnet 未发现任何 SDK)' }
    $details = "global.json 绑定 SDK: $expected.`n配置: $configPath`ndotnet: $($command.Source)`n当前 dotnet 可见的 SDK:`n$installedText"
    if ($expected -notin $versions) { Stop-Pbi 3 "缺少 global.json 绑定的 SDK.`n$details`n$guidance" }
    # 已安装并不等于已选中, 仍让 SDK 在仓库目录解析 global.json 并复核实际版本.
    $actualOutput = @(dotnet --version 2>&1)
    $versionCode = $LASTEXITCODE
    $actual = ($actualOutput | Out-String).Trim()
    if ($versionCode -ne 0) { Stop-Pbi $versionCode "SDK 版本解析失败.`n$details`n原生诊断:`n$actual`n$guidance" }
    if ($actual -ne $expected) { Stop-Pbi 3 "SDK 版本与 global.json 绑定不一致, 实际选中: $actual.`n$details`n$guidance" }
}

<#
.SYNOPSIS 验证 SDK、Node 和 pnpm 的精确版本, 并检查 makensis 可执行及版本输出.
.PARAMETER Tools 当前动作所需工具名称, 不检查其他工具.
.NOTES 一次入口仅检查一次同一工具; 前端与 SDK 绑定不符或 NSIS 版本无效返回 3, 不安装或修改系统设置.
#>
function Assert-PbiTools([string[]]$Tools) {
    $PSNativeCommandUseErrorActionPreference = $false
    foreach ($tool in $Tools) {
        if ($null -ne $script:ValidatedTools -and $script:ValidatedTools.ContainsKey($tool)) { continue }
        if ($tool -eq 'dotnet') {
            Assert-PbiDotnetSdk
            if ($null -ne $script:ValidatedTools) { $script:ValidatedTools[$tool] = $true }
            continue
        }
        if (!(Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-Pbi 3 "缺少 $tool, 请参阅 .docs/toolchain.md" }
        if ($tool -eq 'makensis') {
            $actual = & $tool /VERSION
            $code = $LASTEXITCODE
            if ($code -ne 0) { Stop-Pbi $code '无法读取 makensis 版本' }
            if (($actual | Out-String).Trim() -notmatch '^v?\d+\.\d+(?:\.\d+)?(?:[A-Za-z0-9.+-]*)$') { Stop-Pbi 3 'makensis 版本输出无效, 请检查 NSIS 安装' }
            if ($null -ne $script:ValidatedTools) { $script:ValidatedTools[$tool] = $true }
            continue
        }
        $actual = & $tool --version
        $code = $LASTEXITCODE
        if ($code -ne 0) { Stop-Pbi $code "无法读取 $tool 版本" }
        $expected = switch ($tool) {
            'node' { 'v' + (Get-PbiFrontendToolVersion 'node') }
            'pnpm' { Get-PbiFrontendToolVersion 'pnpm' }
        }
        if (($actual | Out-String).Trim() -ne $expected) { Stop-Pbi 3 "$tool 需要精确版本 $expected, 请参阅 .docs/toolchain.md" }
        if ($null -ne $script:ValidatedTools) { $script:ValidatedTools[$tool] = $true }
    }
}

<#
.SYNOPSIS 检查所选 C# 项目的依赖资产, 缺失时给出显式还原命令, 不调用工具或联网.
.PARAMETER Scope 当前动作需要的 Core 或 All 项目范围.
.PARAMETER IncludeTests 是否检查测试项目, 构建应用时不要求测试项目资产.
.NOTES clean 会删除 obj 中的资产; 缺少文件返回 3, 不隐式还原或改变锁文件.
#>
function Assert-PbiRestoreAssets([string]$Scope, [bool]$IncludeTests = $true) {
    foreach ($project in (Get-PbiProjects $Scope)) {
        if (!$IncludeTests -and $project.StartsWith('tests/')) { continue }
        if (!(Test-Path -LiteralPath (Join-Path $script:Root "$project/obj/project.assets.json") -PathType Leaf)) {
            Stop-Pbi 3 "C# 依赖资产缺失 (首次使用或 clean 后需重新还原). 请先运行 .\run.ps1 -Choice 'setupC#' -Scope $Scope, 再重试当前动作."
        }
    }
}

<#
.SYNOPSIS 读取本轮前端源码内容与已安装依赖元数据的稳定指纹, 不创建跨运行缓存.
.OUTPUTS 可完整核对时返回 SHA256 字符串; 外部链接等无法确认时返回 null, 后续使用完整包脚本.
.NOTES 快照工具失败保留原生退出码; JSON 输出损坏返回 4, 不把失败视为已检查.
#>
function Get-PbiFrontendInputFingerprint {
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @(& node (Join-Path $script:Root 'web/scripts/frontend-inputs.ts') 2>&1)
    $code = $LASTEXITCODE
    if ($code -ne 0) { Stop-Pbi $code ('无法核对本轮前端检查输入: ' + ($output -join "`n")) }
    try {
        $snapshot = ($output -join "`n") | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if ($snapshot -isnot [hashtable] -or $snapshot.reusable -isnot [bool] -or $snapshot.fingerprint -isnot [string] -or $snapshot.fingerprint -notmatch '^[0-9a-f]{64}$') { throw '快照输出无效' }
    } catch { Stop-Pbi 4 '前端输入快照输出无效, 未复用检查结果' }
    if ($snapshot.reusable) { return $snapshot.fingerprint }
    return $null
}

<#
.SYNOPSIS 独立动作完整自检, 仅同一 all 内且输入一致时复用已通过的 Vue 静态检查.
.PARAMETER Action 固定的 checkVue、testVue 或 buildVue, 不接受通用命令字符串.
.NOTES 源码按内容、依赖按实际文件元数据核对; 包脚本变化或存在生命周期脚本时执行完整包脚本, 失败不保留本轮凭据.
#>
function Invoke-PbiFrontendCommand([string]$Action) {
    $command = switch ($Action) { 'checkVue' { 'lint' }; 'testVue' { 'test' }; 'buildVue' { 'build' }; default { Stop-Pbi 2 '未知前端检查动作' } }
    if ($null -eq $script:FrontendValidation) {
        Invoke-PbiNative pnpm @('--dir', 'web', $command)
        return
    }
    $before = Get-PbiFrontendInputFingerprint
    $alreadyChecked = $before -and $script:FrontendValidation.Fingerprint -ceq $before
    $expectedScript = switch ($command) { 'test' { 'pnpm lint && vitest run' }; 'build' { 'pnpm lint && vite build' }; default { '' } }
    $isExpectedScript = $false
    if ($expectedScript) {
        $package = Get-Content -LiteralPath (Join-Path $script:Root 'web/package.json') -Raw | ConvertFrom-Json -AsHashtable
        $hasLifecycle = $package.scripts.ContainsKey('pre' + $command) -or $package.scripts.ContainsKey('post' + $command)
        $isExpectedScript = !$hasLifecycle -and $package.scripts[$command] -ceq $expectedScript
    }
    # 调用前先清旧凭据, 原生失败或取消时不能留下可复用成功.
    $script:FrontendValidation.Fingerprint = $null
    if ($alreadyChecked -and $isExpectedScript) {
        Write-PbiMessage '复用本轮已通过且输入一致的 Vue 静态检查.'
        $toolArguments = if ($command -eq 'test') { @('vitest', 'run') } else { @('vite', 'build') }
        Invoke-PbiNative pnpm (@('--dir', 'web', 'exec') + $toolArguments)
        # 下一阶段重新取快照, 测试中发生的编辑不会沿用此凭据.
        $script:FrontendValidation.Fingerprint = $before
        return
    }
    Invoke-PbiNative pnpm @('--dir', 'web', $command)
    $after = Get-PbiFrontendInputFingerprint
    if ($before -and $before -ceq $after -and ($alreadyChecked -or $command -eq 'lint' -or $isExpectedScript)) {
        $script:FrontendValidation.Fingerprint = $after
    }
}

<#
.SYNOPSIS 每侧依次检查、测试、构建, 先 C# 后 Vue, 交付后注册开发外部工具, 首错停止.
.PARAMETER Options 同一 Scope 与 Configuration, DotNetMode 只传给构建, check 不接 Configuration.
.PARAMETER RegisterExternalTool 是否在完整桌面交付后注册开发入口; release 内部调用关闭此步骤.
.NOTES 前端或测试失败不替换旧产物; 本轮 Vue 检查上下文在 finally 恢复. 注册失败保留已构建产物并报告独立失败阶段.
#>
function Invoke-PbiAll([hashtable]$Options, [bool]$RegisterExternalTool = $true) {
    Invoke-PbiAction 'checkC#' @{ Scope = $Options.Scope }
    Invoke-PbiAction 'testC#' @{ Scope = $Options.Scope; Configuration = $Options.Configuration }
    $buildOptions = @{ Scope = $Options.Scope; Configuration = $Options.Configuration }
    if ($Options.ContainsKey('DotNetMode')) { $buildOptions.DotNetMode = $Options.DotNetMode }
    Invoke-PbiAction 'buildC#' $buildOptions -AfterCompile {
        $savedFrontend = $script:FrontendValidation
        $script:FrontendValidation = @{ Fingerprint = $null }
        try {
            Invoke-PbiAction 'checkVue' @{}
            Invoke-PbiAction 'testVue' @{}
            Invoke-PbiAction 'buildVue' @{}
        } finally { $script:FrontendValidation = $savedFrontend }
    }
    if ($RegisterExternalTool -and $Options.Scope -eq 'All') {
        try { Invoke-PbiDevelopmentRegistration $Options }
        catch {
            if (!$_.Exception.Data.Contains('Action')) { $_.Exception.Data['Action'] = 'registerExternalTool' }
            throw
        }
    }
}

<#
.SYNOPSIS 校验本轮开发产物后注册 Power BI 外部工具, 不修改构建清单或安装版登记.
.PARAMETER Options 已通过入口校验的配置、模式与非交互选择.
.OUTPUTS 无; 产物不匹配、权限不足、取消或文件冲突直接失败, 保留已完成的构建结果.
#>
function Invoke-PbiDevelopmentRegistration([hashtable]$Options) {
    $configuration = $Options.Configuration
    $mode = if ($Options.ContainsKey('DotNetMode')) { $Options.DotNetMode } elseif ($configuration -eq 'Release') { 'without-dotnet' } else { 'with-dotnet' }
    $relative = if ($configuration -eq 'Release') { "artifacts/build/release/win-x64/$mode" } else { 'artifacts/build/debug/win-x64' }
    $destination = [IO.Path]::GetFullPath((Join-Path $script:Root $relative))
    Import-Module (Join-Path $PSScriptRoot 'PublishArtifacts.psm1') -Force
    $manifest = Assert-PbiPublishedArtifacts $destination
    $deployment = if ($mode -eq 'with-dotnet') { 'self-contained' } else { 'framework-dependent' }
    if ($manifest.scope -cne 'All' -or $manifest.configuration -cne $configuration -or $manifest.deploymentMode -cne $deployment) {
        Stop-Pbi 3 '开发外部工具注册前发现产物范围、配置或部署模式不匹配, 未修改登记'
    }
    Import-Module (Join-Path $PSScriptRoot 'installer/ExternalToolRegistration.psm1') -Force
    $nonInteractive = ($Options.ContainsKey('NonInteractive') -and $Options.NonInteractive) -or [bool]$env:CI -or [Console]::IsInputRedirected
    Write-PbiMessage '开始注册 Power BI 外部工具 pbi-utils (开发).'
    $registration = Register-PbiDevelopmentExternalTool -PublishRoot $destination -Version $manifest.version -NonInteractive:$nonInteractive
    Write-PbiMessage ('开发外部工具已注册; 重启 Power BI Desktop 后可从外部工具打开并连接当前模型: ' + $registration.Path) 'Success'
}

<#
.SYNOPSIS 对选中模式执行完整 Release 门禁, 成功后从本轮产物生成安装包.
.PARAMETER Options 已规范化的 DotNetMode, 不接受局部范围或 Debug 配置.
.NOTES 编译、测试或打包失败保留原始退出码, 不自动重试或改用其他模式.
#>
function Invoke-PbiRelease([hashtable]$Options) {
    Invoke-PbiAll @{ Scope = 'All'; Configuration = 'Release'; DotNetMode = $Options.DotNetMode } -RegisterExternalTool $false
    Invoke-PbiAction 'package' @{ DotNetMode = $Options.DotNetMode }
}

<#
.SYNOPSIS 合并动作先执行 C#, 后执行 Vue; 构建延后交付以包含新前端.
.PARAMETER Family setup、build、test、format 或 check 动作组.
.PARAMETER Action 注册表中的动作名, 单语言返回 false.
.PARAMETER Options C# 的范围、可选配置及构建模式, 不传给 Vue 或测试动作.
.OUTPUTS 合并动作执行后返回 true, 首错保留原退出码.
#>
function Invoke-PbiGroup([string]$Family, [string]$Action, [hashtable]$Options) {
    if ($Action -ne ($Family + 'All')) { return $false }
    if ($Family -eq 'build') {
        $buildOptions = @{ Scope = $Options.Scope; Configuration = $Options.Configuration }
        if ($Options.ContainsKey('DotNetMode')) { $buildOptions.DotNetMode = $Options.DotNetMode }
        Invoke-PbiAction 'buildC#' $buildOptions -AfterCompile {
            Invoke-PbiAction 'buildVue' @{}
        }
        return $true
    }
    $targets = @('C#', 'Vue')
    foreach ($target in $targets) {
        $child = @{}
        if ($target -eq 'C#') {
            $child.Scope = $Options.Scope
            if ($Options.ContainsKey('Configuration')) { $child.Configuration = $Options.Configuration }
        }
        Invoke-PbiAction ($Family + $target) $child
    }
    return $true
}

<# .SYNOPSIS 完整范围使用解决方案, Core 返回 CLI 与无 UI 业务后台的依赖闭包, 用于 C# 格式工具. #>
function Get-PbiFormatTargets([string]$Scope) {
    if ($Scope -eq 'All') { return 'pbi-utils.slnx' }
    foreach ($project in (Get-PbiProjects 'Core')) { "$project/$($project.Split('/')[-1]).csproj" }
}

<#
.SYNOPSIS 从维护注册表调用限定 handler, 检查必要的依赖资产, 禁止任意字符串执行.
.PARAMETER Name 注册表中的动作名称.
.PARAMETER Options 当前动作参数, 解析时补充范围及配置默认值.
.PARAMETER AfterCompile 内部构建回调, C# 编译后执行, 不接受为 CLI 参数.
.NOTES 工具或依赖资产缺失返回 3; 动作失败保留原退出码, 不隐式还原.
#>
function Invoke-PbiAction([string]$Name, [hashtable]$Options, [scriptblock]$AfterCompile) {
    $Options.Choice = $Name
    $item = Resolve-PbiAction $Options
    Assert-PbiTools $item.Requires
    if ($item.Name -in @('all', 'buildC#', 'testC#', 'checkC#', 'formatC#')) {
        Assert-PbiRestoreAssets $Options.Scope ($item.Name -ne 'buildC#')
    }
    if ($item.Name -eq 'help') { Show-PbiHelp; return }
    if ($item.Name -eq 'all') { Invoke-PbiAll $Options; return }
    if ($item.Name -eq 'release') { Invoke-PbiRelease $Options; return }
    $PbiInvocation = @{ Options = $Options; Action = $item.Name; AfterCompile = $AfterCompile }
    $leaf = !$item.Name.EndsWith('All')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    if ($leaf) {
        $details = @()
        if ($Options.ContainsKey('Scope')) { $details += "Scope=$($Options.Scope)" }
        if ($Options.ContainsKey('Configuration')) { $details += "Configuration=$($Options.Configuration)" }
        if ($Options.ContainsKey('DotNetMode')) { $details += "DotNetMode=$($Options.DotNetMode)" }
        Write-PbiMessage ("开始 $Name" + $(if ($details.Count) { ' (' + ($details -join ', ') + ')' } else { '' }))
    }
    try {
        . (Join-Path $PSScriptRoot $item.Script)
        if ($leaf) {
            $completion = if ($Name -eq 'buildC#' -and $AfterCompile) { '构建与打包' } else { $Name }
            Write-PbiMessage ("完成 $completion ({0:N1}s)" -f $timer.Elapsed.TotalSeconds) 'Success'
        }
    } catch {
        # 保留工具退出码与具体脚本诊断, 嵌套阶段以最先失败的动作为准.
        if (!$_.Exception.Data.Contains('Action')) { $_.Exception.Data['Action'] = $Name }
        throw
    }
}

<# .SYNOPSIS 根入口统一错误边界, 临时使用 UTF-8, 返回退出码并恢复目录、编码和环境变量. #>
function Invoke-PbiEntry([object[]]$Arguments, [string]$Action = '') {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $savedTools = $script:ValidatedTools
    $script:ValidatedTools = @{}
    $saved = Get-Location
    $savedInputEncoding = [Console]::InputEncoding
    $savedOutputEncoding = [Console]::OutputEncoding
    $savedPipeEncoding = $OutputEncoding
    $environment = @{}
    foreach ($key in @('DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'PNPM_DISABLE_SELF_UPDATE_CHECK')) { $environment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
    $options = @{}
    try {
        # 原生命令输出经 PowerShell 管道解码, 必须与 .NET/Node 的 UTF-8 输出一致.
        $utf8 = [Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = $utf8
        [Console]::OutputEncoding = $utf8
        $OutputEncoding = $utf8
        Set-Location -LiteralPath $script:Root
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        $env:PNPM_DISABLE_SELF_UPDATE_CHECK = '1'
        $options = ConvertTo-PbiOptions @($Arguments)
        if ($Action) {
            if ($options.ContainsKey('Choice')) { Stop-Pbi 2 '下层脚本不接受 Choice' }
            $options.Choice = $Action
        }
        $selectors = @('List', 'Help', 'Choice') | Where-Object { $options.ContainsKey($_) }
        if (@($selectors).Count -gt 1) { Stop-Pbi 2 'List/Help/Choice 互斥' }
        if ($options.ContainsKey('List') -or $options.ContainsKey('Help')) {
            if (@($options.Keys | Where-Object { $_ -notin @('List', 'Help', 'NonInteractive') }).Count) { Stop-Pbi 2 '元命令不接受动作参数' }
            Show-PbiHelp; return 0
        }
        if (!$options.ContainsKey('Choice')) {
            if ($options.ContainsKey('NonInteractive') -or $env:CI -or [Console]::IsInputRedirected) { Stop-Pbi 2 '非交互调用必须指定 -Choice, 或使用 -Help' }
            Show-PbiHelp
            $choice = Read-Host '选择动作 (q 退出)'
            if ($choice -eq 'q') { return 0 }
            $options.Choice = $choice
        }
        $item = Resolve-PbiAction $options
        Invoke-PbiAction $item.Name $options
        if ($item.Name -eq 'all') { Write-PbiMessage ("all 全部通过 ({0:N1}s)" -f $timer.Elapsed.TotalSeconds) 'Success' }
        if ($item.Name -eq 'release') { Write-PbiMessage ("release 全部通过 ({0:N1}s)" -f $timer.Elapsed.TotalSeconds) 'Success' }
        return 0
    }
    catch {
        $code = if ($_.Exception -is [System.Management.Automation.PipelineStoppedException] -or $_.Exception -is [OperationCanceledException]) { 130 } elseif ($_.Exception.Data.Contains('ExitCode')) { [int]$_.Exception.Data['ExitCode'] } else { 4 }
        $failedAction = if ($_.Exception.Data.Contains('Action')) { " [$($_.Exception.Data['Action'])]" } else { '' }
        if ($code -eq 130) { Write-PbiMessage "已取消$failedAction (退出码 130)." 'Info' $true }
        else { Write-PbiMessage ("执行失败$failedAction (退出码 $code): " + $_.Exception.Message) 'Error' }
        return $code
    }
    finally {
        $script:ValidatedTools = $savedTools
        [Console]::InputEncoding = $savedInputEncoding
        [Console]::OutputEncoding = $savedOutputEncoding
        $OutputEncoding = $savedPipeEncoding
        Set-Location -LiteralPath $saved.Path
        foreach ($key in $environment.Keys) {
            if ($null -eq $environment[$key]) { Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process') }
        }
    }
}

Export-ModuleMember -Function *-Pbi*
