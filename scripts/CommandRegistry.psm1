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
# 并行通道子进程为每行输出添加阶段前缀, 普通入口保持原样.
$script:LinePrefix = ''
# 门禁阶段的规范顺序; 并行阶段同时失败时按此顺序报告首个失败, 与串行门禁结论一致.
$script:GateStages = @('checkC#', 'testC#', 'buildC#', 'checkVue', 'testVue', 'buildVue', 'package', 'registerExternalTool')
# 并行通道与调度优先级: 关键路径测试与轻量 Vue 检查保持正常优先级; CPU 消耗大且有余量的 C# 格式与分析检查让出 CPU.
$script:GateLanes = [ordered]@{ 'checkC#' = 'BelowNormal'; 'testC#' = 'Normal'; 'checkVue' = 'Normal'; 'testVue' = 'Normal' }
# 并行门禁所需的最少逻辑处理器; 更少时争用会成倍放大用例耗时并逼近挂起保护, 通道改为逐个执行.
$script:ParallelGateMinProcessors = 16
# 当前主流程正在执行的门禁; 原生命令据此在失败后不再启动, 并登记在途命令供快速失败终止.
$script:ActiveGate = $null
$script:CliTestProject = 'tests/PbiUtils.Cli.Tests'
$script:Projects = @('src/PbiUtils.Core', 'src/PbiUtils.Application', 'src/PbiUtils.Infrastructure', 'src/PbiUtils.Windows', 'src/PbiUtils.Host', 'src/PbiUtils.Cli', 'tests/PbiUtils.UnitTests', $script:CliTestProject)
$script:WindowsProjects = @('src/PbiUtils.Desktop', 'tests/PbiUtils.Desktop.Tests')
# 注册顺序决定菜单展示顺序.
$script:Registry = @(
    @{ Name = 'help'; Parameters = @(); Script = ''; Description = '查看帮助'; Defaults = @{}; Requires = @() }

    @{ Name = 'setupAll'; Parameters = @('Scope'); Script = 'setup.ps1'; Description = '还原 C# 和 Vue 依赖'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'setupC#'; Parameters = @('Scope'); Script = 'setup.ps1'; Description = '还原 C# 依赖'; Defaults = @{}; Requires = @('dotnet') }
    @{ Name = 'setupVue'; Parameters = @(); Script = 'setup.ps1'; Description = '还原 Vue 依赖'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'setupLicense'; Parameters = @(); Script = 'setup.ps1'; Description = '从已准备的依赖更新第三方许可清单'; Defaults = @{}; Requires = @('dotnet', 'node') }
    @{ Name = 'devDesktop'; Parameters = @(); Script = 'dev.ps1'; Description = '启动桌面开发'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'devWeb'; Parameters = @(); Script = 'dev.ps1'; Description = '浏览器模拟预览'; Defaults = @{}; Requires = @('node', 'pnpm') }

    @{ Name = 'formatAll'; Parameters = @('Scope'); Script = 'format.ps1'; Description = '格式化 C# 和 Vue'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'formatC#'; Parameters = @('Scope'); Script = 'format.ps1'; Description = '格式化 C#'; Defaults = @{}; Requires = @('dotnet') }
    @{ Name = 'formatVue'; Parameters = @(); Script = 'format.ps1'; Description = '格式化 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'checkAll'; Parameters = @('Scope'); Script = 'check.ps1'; Description = '只读检查 C# 和 Vue'; Defaults = @{}; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'checkC#'; Parameters = @('Scope'); Script = 'check.ps1'; Description = '只读检查 C# 格式与分析器'; Defaults = @{}; Requires = @('dotnet') }
    @{ Name = 'checkVue'; Parameters = @(); Script = 'check.ps1'; Description = '只读检查 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'testAll'; Parameters = @('Scope', 'Configuration'); Script = 'test.ps1'; Description = '测试 C#、Vue 和开发脚本'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'testC#'; Parameters = @('Scope', 'Configuration'); Script = 'test.ps1'; Description = '测试 C# 和开发脚本'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet') }
    @{ Name = 'testVue'; Parameters = @(); Script = 'test.ps1'; Description = '测试 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'testScripts'; Parameters = @('Configuration'); Script = 'test.ps1'; Description = '只运行开发、构建和打包脚本的回归测试'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet') }
    @{ Name = 'buildAll'; Parameters = @('Scope', 'Configuration', 'DotNetMode'); Script = 'build.ps1'; Description = '构建 C# 和 Vue'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet', 'node', 'pnpm') }
    @{ Name = 'buildC#'; Parameters = @('Scope', 'Configuration', 'DotNetMode'); Script = 'build.ps1'; Description = '构建 C#'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet') }
    @{ Name = 'buildVue'; Parameters = @(); Script = 'build.ps1'; Description = '构建 Vue'; Defaults = @{}; Requires = @('node', 'pnpm') }
    @{ Name = 'verifyAndBuild'; Parameters = @('Scope', 'Configuration', 'DotNetMode'); Script = ''; Description = '检查、测试并构建 C# 和 Vue'; Defaults = @{ Configuration = 'Debug' }; Requires = @('dotnet', 'node', 'pnpm') }

    @{ Name = 'package'; Parameters = @('DotNetMode'); Script = 'package.ps1'; Description = '用已有 Release/All 产物打包'; Defaults = @{ DotNetMode = 'without-dotnet' }; Requires = @('makensis') }
    @{ Name = 'release'; Parameters = @('DotNetMode'); Script = ''; Description = '检查、测试、构建 Release 并打包'; Defaults = @{ DotNetMode = 'without-dotnet' }; Requires = @('dotnet', 'node', 'pnpm', 'makensis') }
    @{ Name = 'clean'; Parameters = @('WhatIf'); Script = 'clean.ps1'; Description = '清理白名单内的生成目录'; Defaults = @{ WhatIf = $false }; Requires = @() }
)
# 菜单编号按注册顺序从 0 自动生成.
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

<# .SYNOPSIS 显示常用命令和注册表中的完整动作目录, 无工具链依赖, 无返回值. #>
function Show-PbiHelp {
    Write-Host 'pbi-utils 开发入口（PowerShell 7）'
    Write-Host '  .\run.ps1                            交互菜单，输入编号或动作名，q 退出'
    Write-Host '  .\run.ps1 -Choice <动作名或编号>     直接执行，可加 -NonInteractive'
    Write-Host '  .\run.ps1 -Help                      查看帮助（也可用 -List）'
    Write-Host ''
    Write-Host '常用命令'
    Write-Host '  .\run.ps1 -Choice setupAll                             # 首次还原依赖'
    Write-Host '  .\run.ps1 -Choice setupLicense                         # 从已准备的依赖更新许可'
    Write-Host '  .\run.ps1 -Choice devDesktop                           # 启动桌面开发'
    Write-Host '  .\run.ps1 -Choice devWeb                               # 浏览器模拟预览'
    Write-Host '  .\run.ps1 -Choice checkAll                             # 只读检查'
    Write-Host '  .\run.ps1 -Choice testAll                              # 测试 C#、Vue 和开发脚本'
    Write-Host '  .\run.ps1 -Choice testScripts                          # 只运行脚本回归'
    Write-Host '  .\run.ps1 -Choice verifyAndBuild                       # 完整验证并构建'
    Write-Host '  .\run.ps1 -Choice buildAll -Configuration Debug        # 构建调试版本 F12打开开发者工具'
    Write-Host '  日常修改可先开发预览并执行相关检查和测试；需要交付安装包时再执行 release。'
    Write-Host ''
    Write-Host '生成轻量安装包'
    Write-Host '  .\run.ps1 -Choice package -DotNetMode without-dotnet    # 使用已有产物'
    Write-Host '  .\run.ps1 -Choice release -DotNetMode without-dotnet    # 完整验证、构建后打包'
    Write-Host '  package 使用已有的同模式 Release/All 构建产物。需要纳入本次源码修改时用 release。'
    Write-Host '  轻量安装包位于 artifacts/release/win-x64/without-dotnet。'
    Write-Host ''
    Write-Host '动作与可选参数（按开发流程排列）'
    foreach ($item in $script:Registry) {
        $details = if ($item.Parameters.Count) { "  (-$($item.Parameters -join ', -'))" } else { '' }
        Write-Host ("{0,2}  {1,-14}  {2}{3}" -f $item.Id, $item.Name, $item.Description, $details)
    }
    Write-Host ''
    Write-Host '  -Scope          All（默认）或 Core'
    Write-Host '  -Configuration  Debug（默认）或 Release'
    Write-Host '  -DotNetMode     Debug 默认 with-dotnet，Release 和打包动作默认 without-dotnet'
    Write-Host '  -WhatIf         预览 clean 将清理的目录'
    Write-Host ''
    Write-Host '  verifyAndBuild 在 All 范围交付后注册 Power BI 开发外部工具，可能需要管理员权限。'
    Write-Host '  定向测试与产物路径见 .agents/skills/pbi-utils-development/references/validation-and-delivery.md。'
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
.NOTES 重定向时不添加 ANSI 控制字符, 不改变工具的输出通道; 并行通道内每行带阶段前缀.
#>
function Write-PbiMessage([string]$Message, [string]$Kind = 'Info', [bool]$ErrorStream = ($Kind -eq 'Error')) {
    $writer = if ($ErrorStream) { [Console]::Error } else { [Console]::Out }
    $redirected = if ($ErrorStream) { [Console]::IsErrorRedirected } else { [Console]::IsOutputRedirected }
    $color = switch ($Kind) { 'Success' { 32 }; 'Warning' { 33 }; 'Error' { 31 }; default { 36 } }
    if ($script:LinePrefix) { $Message = $script:LinePrefix + [Text.RegularExpressions.Regex]::Replace($Message, '(\r?\n)', '$1' + $script:LinePrefix.Replace('$', '$$')) }
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
.SYNOPSIS 读取当前前端包的精确 pnpm 脚本回显, 不将未知 stderr 文本猜测为普通信息.
.PARAMETER File 只有 pnpm 或其同名入口可分类, 其他工具不读取前端配置.
.PARAMETER Arguments 必须明确使用当前仓库的 --dir web, 避免套用其他包的脚本定义.
.OUTPUTS 精确单行回显集合; 配置无法确认时返回 null, 原生命令仍执行并自行报告输入错误和退出码.
.NOTES 此处仅决定显示通道, 不取代 pnpm 的配置验证, 不将读取失败当作命令成功.
#>
function Get-PbiPnpmCommandEchoes([string]$File, [string[]]$Arguments) {
    if ([IO.Path]::GetFileNameWithoutExtension($File) -ine 'pnpm' -or $null -eq $Arguments -or $Arguments.Count -lt 2 -or
        $Arguments[0] -cne '--dir' -or $Arguments[1] -cne 'web') { return $null }
    try {
        $package = Get-Content -LiteralPath (Join-Path $script:Root 'web/package.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($package -isnot [hashtable] -or !$package.ContainsKey('scripts') -or $package.scripts -isnot [hashtable]) { return $null }
        $echoes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($command in $package.scripts.Values) {
            if ($command -is [string] -and $command.Length -gt 0 -and !$command.Contains("`n") -and !$command.Contains("`r")) {
                $null = $echoes.Add('$ ' + $command)
            }
        }
        return ,$echoes
    } catch {
        if ($_.Exception -is [OperationCanceledException] -or $_.Exception -is [Management.Automation.PipelineStoppedException]) { throw }
        # 无法确认时不改写通道, 让后续 pnpm 原样报告损坏配置, 保留其真实退出码.
        Write-Verbose '无法确认 pnpm 脚本回显, 保留原始输出通道.'
        return $null
    }
}

<#
.SYNOPSIS 按参数数组执行原生命令, 保留输出通道和原始非零退出码.
.PARAMETER File 固定工具名称或测试工具路径.
.PARAMETER Arguments 独立参数数组, 不拼接或执行任意命令字符串.
.NOTES 仅精确改写 .NET 无需还原提示及已核验的 pnpm 包脚本回显; 诊断不凭 stderr 或错误字样裁决成功.
       主流程处于门禁中时先检查已有失败, 已失败则不启动; 可执行文件、批处理与 PowerShell 脚本改为持有根进程句柄运行,
       其他阶段失败或取消时只结束本命令自有的进程树. 被结束时抛出带 PbiGateTerminated 标记的异常或取消异常,
       汇总时作为被终止阶段而不是新的失败. 别名先解析到最终目标; 函数、cmdlet 等进程内命令保持原调用方式.
#>
function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
    $command = Get-Command $File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$command) { Stop-Pbi 3 "缺少工具 $File, 请参阅 .docs/toolchain.md 官方安装来源" }
    # 别名可能指向可执行文件或脚本, 按最终目标决定启动方式; 循环别名保持原调用方式, 由 PowerShell 报告.
    for ($depth = 0; $depth -lt 8 -and $command -is [Management.Automation.AliasInfo] -and $null -ne $command.ResolvedCommand; $depth++) { $command = $command.ResolvedCommand }
    $commandEchoes = Get-PbiPnpmCommandEchoes $File $Arguments
    $gate = $script:ActiveGate
    if ($null -ne $gate) {
        Assert-PbiGateRunning $gate
        $start = New-PbiGateNativeStart $command $Arguments
        if ($null -ne $start) {
            $nativeCode = Invoke-PbiGateNative $gate $start $File $Arguments $commandEchoes
            if ($nativeCode -ne 0) { Stop-Pbi $nativeCode "命令 $File 返回 $nativeCode" }
            return
        }
    }
    # 原生命令的退出码在输出全部读完后裁决, 不受调用 shell 的原生错误偏好影响.
    $PSNativeCommandUseErrorActionPreference = $false
    & $File @Arguments 2>&1 | ForEach-Object { Write-PbiNativeLine $File $Arguments ([string]$_) ($_ -is [Management.Automation.ErrorRecord]) $commandEchoes }
    $nativeCode = $LASTEXITCODE
    if ($nativeCode -ne 0) { Stop-Pbi $nativeCode "命令 $File 返回 $nativeCode" }
}

<#
.SYNOPSIS 为门禁中的原生命令创建持有句柄的启动信息, 使本轮拥有其根进程.
.PARAMETER Command Invoke-PbiNative 解析出的命令.
.PARAMETER Arguments 独立参数数组, 按标准 Windows 规则逐项传递.
.OUTPUTS 可执行文件直接启动; 批处理由 CreateProcess 经 cmd.exe 执行; PowerShell 脚本经当前版本 pwsh -File 执行;
         其他命令类型 (函数、cmdlet 及无法解析的别名等进程内命令) 返回 null, 仍按原方式调用.
.NOTES 使用当前位置、继承环境与标准输入, 两路输出按控制台编码解码, 与 PowerShell 调用原生命令一致. 参数为固定字面量, 不拼接命令字符串.
#>
function New-PbiGateNativeStart([Management.Automation.CommandInfo]$Command, [string[]]$Arguments) {
    if ($Command.CommandType -eq [Management.Automation.CommandTypes]::Application) {
        $start = [Diagnostics.ProcessStartInfo]::new($Command.Source)
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    }
    elseif ($Command.CommandType -eq [Management.Automation.CommandTypes]::ExternalScript) {
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $Command.Source)) { $start.ArgumentList.Add($argument) }
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    }
    else { return $null }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Console]::OutputEncoding
    $start.StandardErrorEncoding = [Console]::OutputEncoding
    $start.WorkingDirectory = (Get-Location -PSProvider FileSystem).ProviderPath
    return $start
}

<#
.SYNOPSIS 按原生命令输出的来源与内容选择显示通道和颜色, 写出一行.
.PARAMETER File 工具名称, 用于精确改写已知提示.
.PARAMETER Arguments 原生命令参数数组.
.PARAMETER Text 原始输出行.
.PARAMETER ErrorStream 该行是否来自 stderr.
.PARAMETER CommandEchoes 已核验的 pnpm 包脚本回显集合, 无法确认时为 null.
.NOTES 不改变工具退出码; 并行通道内每行带阶段前缀.
#>
function Write-PbiNativeLine([string]$File, [string[]]$Arguments, [string]$Text, [bool]$ErrorStream, $CommandEchoes) {
    $line = ConvertTo-PbiNativeText $File $Arguments $Text
    if ($null -ne $CommandEchoes -and $CommandEchoes.Contains($line)) {
        # pnpm 将包脚本命令回显写 stderr; 只将精确已知的回显作为普通信息, 避免外层 PowerShell 误染红.
        [Console]::Out.WriteLine($script:LinePrefix + $line)
    } elseif ($line -match '^\s*(已成功生成[。.]|Build succeeded\.|0 个(警告|错误)\b|0 (Warning|Error)\(s\)|所有项目依赖均为最新|All projects are up-to-date for restore\.)') {
        Write-PbiMessage $line 'Success' $ErrorStream
    } elseif ($line -match '(?i)(\berror\s+[A-Z]+\d+\s*:|:\s*error\b|已失败生成|生成失败|Build FAILED|^\s*[1-9]\d* 个错误)') {
        Write-PbiMessage $line 'Error' $ErrorStream
    } elseif ($ErrorStream) {
        # 有些工具把正常进度或警告写 stderr, 保留原始诊断但不冒充失败.
        if (![Console]::IsErrorRedirected -and !$env:NO_COLOR) { [Console]::Error.WriteLine("`e[33m$($script:LinePrefix)$line`e[0m") }
        else { [Console]::Error.WriteLine($script:LinePrefix + $line) }
    } elseif ($line -match '(?i)(\bwarning\b|警告|^\(!\))' -and $line -notmatch '^\s*0\s') {
        Write-PbiMessage $line 'Warning' $ErrorStream
    } else { [Console]::Out.WriteLine($script:LinePrefix + $line) }
}

<#
.SYNOPSIS 门禁中持有句柄运行原生命令, 逐行转发两路输出, 其他阶段失败或取消时只结束本命令自有的进程树.
.PARAMETER Gate 当前门禁上下文.
.PARAMETER Start New-PbiGateNativeStart 创建的启动信息.
.PARAMETER File 调用方给出的工具名称, 用于输出分类与诊断.
.PARAMETER Arguments 原生命令参数数组, 用于输出分类.
.PARAMETER CommandEchoes 已核验的 pnpm 包脚本回显集合或 null.
.OUTPUTS 原生命令退出码; 因其他阶段失败被结束时抛出带 PbiGateTerminated 标记的异常, 因通道取消被结束时抛出取消异常.
.NOTES 读到两路输出结束后继续限时等待进程退出, 整个运行期间每 100 毫秒检查门禁. 根进程已退出但后代仍持有输出管道时,
       持有的句柄仍能定位并结束这些后代. 仅当结束时根进程仍在运行且退出码为 TerminateProcess 写入的 -1 时判定为被结束;
       此前已自行结束的命令按真实退出码汇总. 管道被停止 (Ctrl+C) 时与 PowerShell 原生命令一致结束本命令进程树.
#>
function Invoke-PbiGateNative([pscustomobject]$Gate, [Diagnostics.ProcessStartInfo]$Start, [string]$File, [string[]]$Arguments, $CommandEchoes) {
    $process = [Diagnostics.Process]::Start($Start)
    $finished = $false
    $stopReason = $null
    $rootStopped = $false
    try {
        $outputRead = $process.StandardOutput.ReadLineAsync()
        $errorRead = $process.StandardError.ReadLineAsync()
        # 两路输出结束后仍限时等待进程退出, 关闭输出后继续运行的命令也能在其他阶段失败后被结束.
        while ($true) {
            if ($null -ne $outputRead -or $null -ne $errorRead) {
                $pending = [Collections.Generic.List[Threading.Tasks.Task]]::new()
                if ($null -ne $outputRead) { $pending.Add($outputRead) }
                if ($null -ne $errorRead) { $pending.Add($errorRead) }
                $null = [Threading.Tasks.Task]::WaitAny($pending.ToArray(), 100)
                while ($null -ne $outputRead -and $outputRead.IsCompleted) {
                    $line = $outputRead.GetAwaiter().GetResult()
                    if ($null -eq $line) { $outputRead = $null; break }
                    Write-PbiNativeLine $File $Arguments $line $false $CommandEchoes
                    $outputRead = $process.StandardOutput.ReadLineAsync()
                }
                while ($null -ne $errorRead -and $errorRead.IsCompleted) {
                    $line = $errorRead.GetAwaiter().GetResult()
                    if ($null -eq $line) { $errorRead = $null; break }
                    Write-PbiNativeLine $File $Arguments $line $true $CommandEchoes
                    $errorRead = $process.StandardError.ReadLineAsync()
                }
            }
            elseif ($process.WaitForExit(100)) { break }
            if ($null -eq $stopReason) {
                $stopReason = Get-PbiGateStopReason $Gate
                if ($null -ne $stopReason) {
                    $rootStopped = !$process.HasExited
                    try { $process.Kill($true) }
                    catch { [Console]::Error.WriteLine("并行门禁未能完全结束命令 $File`: " + $_.Exception.Message) }
                }
            }
        }
        $nativeCode = $process.ExitCode
        $finished = $true
    }
    finally {
        # 管道被停止或读取异常时与 PowerShell 原生命令一致, 不遗留本命令的进程树.
        if (!$finished) { try { $process.Kill($true) } catch { [Console]::Error.WriteLine("命令 $File 的进程树清理失败: " + $_.Exception.Message) } }
        $process.Dispose()
    }
    if ($rootStopped -and $nativeCode -eq -1) {
        if ($stopReason -eq 'cancelled') {
            $cancelled = [OperationCanceledException]::new("并行阶段已取消, 已结束在途命令 $File")
            $cancelled.Data['ExitCode'] = 130
            throw $cancelled
        }
        $terminated = [InvalidOperationException]::new("其他门禁阶段失败, 已终止在途命令 $File")
        $terminated.Data['PbiGateTerminated'] = $true
        throw $terminated
    }
    return $nativeCode
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
    $guidance = "请开发者检查版本: 如需保持现有绑定, 安装 SDK $expected 或检查 PATH 是否指向正确的 dotnet; 如计划升级, 请确认是否更新 global.json 的 sdk.version 绑定, 更新后重新还原依赖并运行 verifyAndBuild. 本脚本不会自动安装 SDK 或修改 global.json."
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
.PARAMETER TestProject 指定时只检查该测试项目和范围内的产品项目, 用于定向测试.
.NOTES clean 会删除 obj 中的资产; 缺少文件返回 3, 不隐式还原或改变锁文件.
#>
function Assert-PbiRestoreAssets([string]$Scope, [bool]$IncludeTests = $true, [string]$TestProject = '') {
    foreach ($project in (Get-PbiProjects $Scope)) {
        if ($project.StartsWith('tests/') -and (!$IncludeTests -or ($TestProject -and $project -ne $TestProject))) { continue }
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
.SYNOPSIS 判断前端包脚本是否仍为可拆分复用的固定定义, 且没有对应的生命周期脚本.
.PARAMETER Command test 或 build; 其他命令不读取包配置并返回 false.
.OUTPUTS 定义与预期完全一致时返回 true; 包配置读取或解析失败直接抛出, 不视为可复用.
#>
function Test-PbiFrontendScriptReusable([string]$Command) {
    $expectedScript = switch ($Command) { 'test' { 'pnpm lint && vitest run' }; 'build' { 'pnpm lint && vite build' }; default { '' } }
    if (!$expectedScript) { return $false }
    $package = Get-Content -LiteralPath (Join-Path $script:Root 'web/package.json') -Raw | ConvertFrom-Json -AsHashtable
    $hasLifecycle = $package.scripts.ContainsKey('pre' + $Command) -or $package.scripts.ContainsKey('post' + $Command)
    return (!$hasLifecycle -and $package.scripts[$Command] -ceq $expectedScript)
}

<#
.SYNOPSIS 独立动作完整自检, 仅同一轮验证构建内且输入一致时复用已通过的 Vue 静态检查.
.PARAMETER Action 固定的 checkVue、testVue 或 buildVue, 不接受通用命令字符串.
.NOTES 源码按内容、依赖按实际文件元数据核对; 包脚本变化或存在生命周期脚本时执行完整包脚本, 失败不保留本轮凭据.
       并行门禁的测试通道只运行 Vitest, 完整 lint 由同时运行的检查通道执行, 构建前由主流程核对输入指纹.
#>
function Invoke-PbiFrontendCommand([string]$Action) {
    $command = switch ($Action) { 'checkVue' { 'lint' }; 'testVue' { 'test' }; 'buildVue' { 'build' }; default { Stop-Pbi 2 '未知前端检查动作' } }
    if ($null -eq $script:FrontendValidation) {
        Invoke-PbiNative pnpm @('--dir', 'web', $command)
        return
    }
    if ($command -eq 'test' -and $script:FrontendValidation.ContainsKey('ConcurrentLint') -and $script:FrontendValidation.ConcurrentLint) {
        if (Test-PbiFrontendScriptReusable 'test') {
            Write-PbiMessage 'Vue 静态检查由并行检查通道执行, 本通道只运行 Vitest.'
            Invoke-PbiNative pnpm @('--dir', 'web', 'exec', 'vitest', 'run')
        } else { Invoke-PbiNative pnpm @('--dir', 'web', 'test') }
        return
    }
    $before = Get-PbiFrontendInputFingerprint
    $alreadyChecked = $before -and $script:FrontendValidation.Fingerprint -ceq $before
    $isExpectedScript = Test-PbiFrontendScriptReusable $command
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
.SYNOPSIS 返回门禁阶段在规范顺序中的位置, 未登记的动作按 C# 构建阶段排序.
.PARAMETER Action 阶段或动作名称.
.OUTPUTS 用于在多个失败中选择首个阶段的非负整数.
#>
function Get-PbiGateStageOrder([string]$Action) {
    $index = [Array]::IndexOf($script:GateStages, $Action)
    if ($index -lt 0) { return [Array]::IndexOf($script:GateStages, 'buildC#') }
    return $index
}

<#
.SYNOPSIS 把动作异常映射为入口退出码: 取消 130, 已登记的退出码原样, 其他异常 4.
.PARAMETER Exception 动作、原生边界或并行通道抛出的异常.
.OUTPUTS 整数退出码.
#>
function Get-PbiExitCode([Exception]$Exception) {
    if ($Exception -is [Management.Automation.PipelineStoppedException] -or $Exception -is [OperationCanceledException]) { return 130 }
    if ($Exception.Data.Contains('ExitCode')) { return [int]$Exception.Data['ExitCode'] }
    return 4
}

<#
.SYNOPSIS 严格解析内部并行通道参数; 结果文件必须是本轮门禁私有临时目录中的新文件.
.PARAMETER Arguments lane.ps1 收到的原始参数数组.
.OUTPUTS 含 Lane、Result 及 C# 通道所需 Scope/Configuration 的选项; 参数无效返回 2.
#>
function ConvertTo-PbiLaneOptions([object[]]$Arguments) {
    $options = @{}
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $token = [string]$Arguments[$i]
        if ($token -cnotmatch '^-(Lane|Result|Scope|Configuration)$') { Stop-Pbi 2 '内部并行通道参数无效' }
        $key = $Matches[1]
        if ($options.ContainsKey($key)) { Stop-Pbi 2 '参数不可重复' }
        $i++
        if ($i -ge $Arguments.Count -or [string]$Arguments[$i] -match '^-') { Stop-Pbi 2 '参数缺少值' }
        $options[$key] = [string]$Arguments[$i]
    }
    if (!$options.ContainsKey('Lane') -or @($script:GateLanes.Keys) -cnotcontains $options.Lane) { Stop-Pbi 2 '未知或缺少并行通道' }
    $csharp = $options.Lane.EndsWith('C#', [StringComparison]::Ordinal)
    if ($csharp -ne $options.ContainsKey('Scope') -or ($csharp -and $options.Scope -cnotin @('Core', 'All'))) { Stop-Pbi 2 '并行通道范围参数无效' }
    if (($options.Lane -ceq 'testC#') -ne $options.ContainsKey('Configuration') -or ($options.ContainsKey('Configuration') -and $options.Configuration -cnotin @('Debug', 'Release'))) {
        Stop-Pbi 2 '并行通道配置参数无效'
    }
    $result = if ($options.ContainsKey('Result')) { $options.Result } else { '' }
    if (!$result -or ![IO.Path]::IsPathFullyQualified($result)) { Stop-Pbi 2 '并行通道结果路径无效' }
    $full = [IO.Path]::GetFullPath($result)
    $directory = [IO.Path]::GetDirectoryName($full)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetFileName($full) -cnotmatch '^[0-9a-f]{32}\.json$' -or [IO.Path]::GetFileName($directory) -cnotmatch '^pbi-utils-gate-[0-9a-f]{32}$' -or
        ![string]::Equals([IO.Path]::GetDirectoryName($directory), $temporary, [StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Directory]::Exists($directory) -or [IO.File]::Exists($full)) { Stop-Pbi 2 '并行通道结果路径无效' }
    $options.Result = $full
    return $options
}

<#
.SYNOPSIS 把并行通道的退出码、失败阶段和原因写入私有结果文件, 供主流程报告首个失败.
.PARAMETER Path 已通过校验的结果文件路径.
.PARAMETER Code 通道最终退出码.
.PARAMETER Action 失败阶段或通道名称.
.PARAMETER Message 失败原因; 成功时为空.
.NOTES 只使用 .NET 写入, 取消后的错误边界中也可执行; 写入失败只写诊断, 主流程按缺失结果判定失败.
#>
function Write-PbiLaneResult([string]$Path, [int]$Code, [string]$Action, [string]$Message) {
    try {
        $json = '{"code":' + $Code + ',"action":"' + [Text.Json.JsonEncodedText]::Encode($Action).ToString() + '","message":"' + [Text.Json.JsonEncodedText]::Encode($Message).ToString() + '"}'
        [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
    }
    catch { [Console]::Error.WriteLine($script:LinePrefix + '并行通道结果写入失败: ' + $_.Exception.Message) }
}

<#
.SYNOPSIS 在当前进程执行一个并行通道的固定阶段, 由 lane.ps1 子进程调用.
.PARAMETER Lane 允许列表中的通道名称.
.PARAMETER Options 子进程入口解析出的 Scope 与 Configuration.
.NOTES 测试通道声明 lint 由并行检查通道执行, 只运行 Vitest; 失败保留动作退出码.
#>
function Invoke-PbiGateLaneBody([string]$Lane, [hashtable]$Options) {
    switch -CaseSensitive ($Lane) {
        'checkC#' { Invoke-PbiAction 'checkC#' @{ Scope = $Options.Scope } }
        'testC#' { Invoke-PbiAction 'testC#' @{ Scope = $Options.Scope; Configuration = $Options.Configuration } }
        'checkVue' { Invoke-PbiAction 'checkVue' @{} }
        'testVue' {
            $savedFrontend = $script:FrontendValidation
            $script:FrontendValidation = @{ Fingerprint = $null; ConcurrentLint = $true }
            try { Invoke-PbiAction 'testVue' @{} }
            finally { $script:FrontendValidation = $savedFrontend }
        }
        default { Stop-Pbi 2 '未知并行通道' }
    }
}

<#
.SYNOPSIS 判断本机是否并行运行门禁通道; 按当前进程可用的逻辑处理器计算, 已包含亲和性与作业限制.
.OUTPUTS 不少于并行阈值返回 $true; 否则返回 $false, 通道按规范顺序逐个执行.
#>
function Test-PbiParallelGate {
    return [Environment]::ProcessorCount -ge $script:ParallelGateMinProcessors
}

<#
.SYNOPSIS 创建本轮并行门禁上下文和私有结果目录, 不启动任何通道.
.PARAMETER Options verifyAndBuild 已规范化的 Scope 与 Configuration.
.OUTPUTS 门禁上下文, Parallel 记录本轮是否并行; 临时目录创建失败直接抛出.
.NOTES State 由主流程与看护 runspace 共享: Lanes 为已启动通道进程与结果路径, Trigger 为首个失败来源, Terminated 为被快速失败终止的通道,
       Draining 表示按取消排空, Stopping 通知看护退出; Lock 串行终止与排空的决定. Watchdog 在首个真实通道启动时创建.
#>
function New-PbiVerificationGate([hashtable]$Options) {
    $parallel = Test-PbiParallelGate
    $directory = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-gate-' + [Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($directory)
    $state = [hashtable]::Synchronized(@{
            Lanes      = [Collections.Concurrent.ConcurrentQueue[object]]::new()
            Terminated = [Collections.Concurrent.ConcurrentDictionary[string, bool]]::new([StringComparer]::Ordinal)
            Lock       = [object]::new()
            Trigger    = $null
            Draining   = $false
            Stopping   = $false
        })
    return [pscustomobject]@{ Options = $Options; Directory = $directory; Lanes = [Collections.Generic.List[object]]::new(); FrontendFingerprint = $null; Parallel = $parallel; State = $state; Watchdog = $null }
}

<#
.SYNOPSIS 启动继承当前控制台的并行通道子进程; 子进程每行输出带阶段前缀, 结果写入门禁私有目录.
.PARAMETER Gate 当前并行门禁上下文.
.PARAMETER Name 允许列表中的通道名称.
.OUTPUTS 已加入门禁的通道记录; 进程无法启动时抛出带阶段名的错误.
.NOTES 进程启动后登记到看护状态; 首个真实通道启动时加载看护模块并启动看护 runspace, 进程内同步替身不启动看护.
#>
function Start-PbiGateLane([pscustomobject]$Gate, [string]$Name) {
    # 看护模块先于首个通道进程加载, 任何真实通道启动后终止函数均已可用.
    if ($Gate.State.Lanes.IsEmpty) { Import-Module (Join-Path $PSScriptRoot 'GateWatchdog.psm1') -Force }
    $result = Join-Path $Gate.Directory ([Guid]::NewGuid().ToString('N') + '.json')
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $script:Root
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'lane.ps1'), '-Lane', $Name, '-Result', $result)) { $start.ArgumentList.Add($argument) }
    if ($Name.EndsWith('C#', [StringComparison]::Ordinal)) { $start.ArgumentList.Add('-Scope'); $start.ArgumentList.Add($Gate.Options.Scope) }
    if ($Name -ceq 'testC#') { $start.ArgumentList.Add('-Configuration'); $start.ArgumentList.Add($Gate.Options.Configuration) }
    try { $process = [Diagnostics.Process]::Start($start) }
    catch {
        $_.Exception.Data['Action'] = $Name
        throw
    }
    $lane = [pscustomobject]@{ Name = $Name; Process = $process; Result = $result; Completed = $false; Terminated = $false; ExitCode = $null; Action = $Name; Message = ''; Elapsed = [TimeSpan]::Zero }
    $Gate.Lanes.Add($lane)
    $Gate.State.Lanes.Enqueue([pscustomobject]@{ Name = $Name; Process = $process; Result = $result })
    if ($null -eq $Gate.Watchdog) { $Gate.Watchdog = Start-PbiGateWatchdog $Gate.State }
    return $lane
}

<#
.SYNOPSIS 收集已退出通道的退出码、耗时与结果记录, 不阻塞运行中的通道.
.PARAMETER Gate 当前并行门禁上下文.
.NOTES 退出码 0 但缺少有效结果时按执行失败 4 处理, 不把未知状态当作通过.
       被快速失败终止且没有写回结果的通道标记为 Terminated, 不作为失败候选; 终止前已写回结果的通道仍按真实结果汇总.
#>
function Update-PbiGateLanes([pscustomobject]$Gate) {
    foreach ($lane in $Gate.Lanes) {
        if ($lane.Completed -or !$lane.Process.HasExited) { continue }
        $lane.Process.WaitForExit()
        $lane.ExitCode = $lane.Process.ExitCode
        $lane.Elapsed = $lane.Process.ExitTime - $lane.Process.StartTime
        $lane.Completed = $true
        $record = Read-PbiLaneResult $lane.Result
        $terminated = $Gate.State.Terminated.ContainsKey($lane.Name)
        if ($null -ne $record -and ($record.Code -eq $lane.ExitCode -or $terminated)) {
            # 终止只改变进程退出码; 已完整写回的结果仍是该阶段的真实结论.
            $lane.ExitCode = $record.Code
            if ($record.Action) { $lane.Action = $record.Action }
            $lane.Message = $record.Message
        } elseif ($terminated) {
            $lane.Terminated = $true
            $lane.Message = "并行阶段 $($lane.Name) 已在首个失败后终止"
        } elseif ($lane.ExitCode -eq 0) {
            $lane.ExitCode = 4
            $lane.Message = "并行阶段 $($lane.Name) 已退出但没有写入有效结果"
        } else {
            $lane.Message = "并行阶段 $($lane.Name) 异常退出 (退出码 $($lane.ExitCode))"
        }
    }
}

<#
.SYNOPSIS 为失败的通道创建带退出码与阶段的异常; 通道取消映射为取消异常.
.PARAMETER Lane 已完成且退出码非零的通道记录.
.PARAMETER Checkpoint 是否由主流程等待点抛出; 等待点异常不作为额外失败重复汇总.
.OUTPUTS 可直接抛出的异常.
#>
function New-PbiGateLaneException([pscustomobject]$Lane, [bool]$Checkpoint) {
    $message = if ($Lane.Message) { $Lane.Message } else { "并行阶段 $($Lane.Name) 失败 (退出码 $($Lane.ExitCode))" }
    $exception = if ($Lane.ExitCode -eq 130) { [OperationCanceledException]::new($message) } else { [InvalidOperationException]::new($message) }
    $exception.Data['ExitCode'] = $Lane.ExitCode
    $exception.Data['Action'] = $Lane.Action
    if ($Checkpoint) { $exception.Data['PbiGateCheckpoint'] = $true }
    return $exception
}

<#
.SYNOPSIS 不抛出地判断门禁是否应结束主流程在途原生命令, 供其轮询.
.PARAMETER Gate 当前并行门禁上下文.
.OUTPUTS 有通道以 130 结束时返回 cancelled (取消优先); 已记录失败来源或有未被终止的失败通道时返回 failed; 否则返回 null.
.NOTES 主流程自身轮询通道结果, 看护未运行时原生命令仍能在其他阶段失败后及时结束; 取消返回 cancelled, 使门禁按取消排空而不是终止通道.
#>
function Get-PbiGateStopReason([pscustomobject]$Gate) {
    Update-PbiGateLanes $Gate
    $reason = if ($null -ne $Gate.State.Trigger) { 'failed' } else { $null }
    foreach ($lane in $Gate.Lanes) {
        if (!$lane.Completed -or $lane.Terminated -or $lane.ExitCode -eq 0) { continue }
        if ($lane.ExitCode -eq 130) { return 'cancelled' }
        $reason = 'failed'
    }
    return $reason
}

<#
.SYNOPSIS 检查门禁是否已有阶段失败; 有则立即抛出, 调用方不再开始新的阶段或原生命令.
.PARAMETER Gate 当前并行门禁上下文.
.NOTES 已结束的通道中任一以 130 取消即按取消抛出 (取消优先, 使门禁按取消排空); 否则按规范阶段顺序选择首个失败, 被快速失败终止的通道不计入.
       看护已记录失败来源但通道尚无结论时按该来源停止. 这些异常带 PbiGateCheckpoint 标记, 汇总时只代表已记录的结果.
       看护异步调用已结束时无法保证快速失败, 作为门禁自身失败抛出.
#>
function Assert-PbiGateRunning([pscustomobject]$Gate) {
    Update-PbiGateLanes $Gate
    $failed = $null
    foreach ($lane in $Gate.Lanes) {
        if (!$lane.Completed -or $lane.Terminated -or $lane.ExitCode -eq 0) { continue }
        if ($lane.ExitCode -eq 130) { throw (New-PbiGateLaneException $lane $true) }
        if ($null -eq $failed -or (Get-PbiGateStageOrder $lane.Action) -lt (Get-PbiGateStageOrder $failed.Action)) { $failed = $lane }
    }
    if ($null -ne $failed) { throw (New-PbiGateLaneException $failed $true) }
    $trigger = $Gate.State.Trigger
    if ($null -ne $trigger) {
        $stopped = [InvalidOperationException]::new("并行门禁已因 [$trigger] 失败停止")
        $stopped.Data['ExitCode'] = 4
        $stopped.Data['Action'] = $trigger
        $stopped.Data['PbiGateCheckpoint'] = $true
        throw $stopped
    }
    # PowerShell 批处理在加载模块与运行看护之间会短暂进入 Completed, 异步句柄才表示整个调用是否结束.
    if ($null -ne $Gate.Watchdog -and $Gate.Watchdog.Handle.IsCompleted) {
        $invocation = $Gate.Watchdog.PowerShell.InvocationStateInfo
        $detail = if ($null -ne $invocation.Reason) { $invocation.Reason.Message } elseif ($Gate.Watchdog.PowerShell.Streams.Error.Count) { $Gate.Watchdog.PowerShell.Streams.Error[0].ToString() } else { $invocation.State.ToString() }
        Stop-Pbi 4 "并行门禁看护未运行, 无法保证首个失败立即终止其余阶段: $detail"
    }
}

<#
.SYNOPSIS 主流程处于门禁中时检查已有失败, 供构建脚本在阶段之间调用; 不在门禁中时不做任何事.
.NOTES 失败后不开始新的构建阶段, 抛出的检查点异常由门禁汇总为已记录的首个失败.
#>
function Assert-PbiActiveGate {
    if ($null -ne $script:ActiveGate) { Assert-PbiGateRunning $script:ActiveGate }
}

<#
.SYNOPSIS 等待指定通道完成; 任一已结束通道失败时立即抛出, 主流程不再开始后续阶段.
.PARAMETER Gate 当前并行门禁上下文.
.PARAMETER Names 需要等待的通道; 为空时等待全部已启动通道.
.NOTES 每次最多阻塞 200 毫秒, 取消可在下一条语句生效; 同时失败时按规范阶段顺序选择.
#>
function Wait-PbiGateLanes([pscustomobject]$Gate, [string[]]$Names = @()) {
    while ($true) {
        Assert-PbiGateRunning $Gate
        $pending = $null
        foreach ($lane in $Gate.Lanes) {
            if (!$lane.Completed -and ($Names.Count -eq 0 -or $Names -ccontains $lane.Name)) { $pending = $lane; break }
        }
        if ($null -eq $pending) { return }
        $null = $pending.Process.WaitForExit(200)
    }
}

<#
.SYNOPSIS 结束门禁: 失败时立即终止在途通道, 取消时排空在途通道; 输出各通道耗时并清理看护与私有结果目录.
.PARAMETER Gate 当前并行门禁上下文.
.PARAMETER Terminate 主流程以非取消失败结束时为 $true, 立即终止仍在运行的通道进程树而不等待其完成.
.NOTES 真实 Ctrl+C 同时到达全部通道, 取消时等待它们自行结束; 成功路径中全部通道已完成.
       等待只使用 .NET 调用, 失败或取消后的 finally 中仍会执行; 目录清理失败写诊断并保留目录.
#>
function Stop-PbiVerificationGate([pscustomobject]$Gate, [bool]$Terminate = $false) {
    try {
        if ($Terminate) {
            if (!$Gate.State.Lanes.IsEmpty) { $null = Invoke-PbiGateAbort $Gate.State 'main' }
        }
        else {
            # 排空与看护终止在同一把锁中决定先后: 排空开始后看护不再终止通道.
            [Threading.Monitor]::Enter($Gate.State.Lock)
            try { $Gate.State.Draining = $true }
            finally { [Threading.Monitor]::Exit($Gate.State.Lock) }
        }
        $announced = $false
        foreach ($lane in $Gate.Lanes) {
            if ($null -eq $lane.Process) { continue }
            while (!$lane.Process.WaitForExit(250)) {
                if (!$announced) {
                    $waiting = if ($Terminate) { '等待已终止的并行阶段退出.' } else { '等待在途并行阶段响应取消并退出.' }
                    [Console]::Error.WriteLine($waiting)
                    $announced = $true
                }
            }
        }
        Update-PbiGateLanes $Gate
        $label = if ($Gate.Parallel) { '并行计时' } else { '通道计时' }
        foreach ($lane in $Gate.Lanes) {
            $state = if ($lane.Terminated) { '已终止' } elseif ($lane.ExitCode -eq 0) { '通过' } else { "退出码 $($lane.ExitCode)" }
            [Console]::Out.WriteLine($label + ' [' + $lane.Name + '] ' + $lane.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture) + 's ' + $state)
        }
    }
    finally {
        if ($null -ne $Gate.Watchdog) { Stop-PbiGateWatchdog $Gate.Watchdog $Gate.State }
        foreach ($lane in $Gate.Lanes) { if ($null -ne $lane.Process) { $lane.Process.Dispose() } }
        try { if ([IO.Directory]::Exists($Gate.Directory)) { [IO.Directory]::Delete($Gate.Directory, $true) } }
        catch { [Console]::Error.WriteLine('并行门禁结果目录清理失败, 保留于: ' + $Gate.Directory + '. 原因: ' + $_.Exception.Message) }
    }
}

<#
.SYNOPSIS 汇总主流程与全部通道失败, 按规范阶段顺序返回首个失败, 其余失败写入诊断.
.PARAMETER Gate 已结束的门禁上下文.
.PARAMETER MainFailure 主流程捕获的错误记录; 等待点错误只代表已记录的通道失败.
.OUTPUTS 需要抛出的错误记录或异常; 全部通过返回 null. 取消优先于其他失败.
.NOTES 被快速失败终止的通道与主流程在途命令不是失败候选, 统一列为已终止阶段.
#>
function Resolve-PbiGateFailure([pscustomobject]$Gate, $MainFailure) {
    $mainIsCheckpoint = $null -ne $MainFailure -and $MainFailure.Exception.Data.Contains('PbiGateCheckpoint')
    $mainIsTerminated = $null -ne $MainFailure -and $MainFailure.Exception.Data.Contains('PbiGateTerminated')
    if ($null -ne $MainFailure -and !$mainIsCheckpoint -and !$mainIsTerminated -and (Get-PbiExitCode $MainFailure.Exception) -eq 130) { return $MainFailure }
    $candidates = [Collections.Generic.List[object]]::new()
    $terminated = [Collections.Generic.List[string]]::new()
    foreach ($lane in $Gate.Lanes) {
        if ($lane.Terminated) { $terminated.Add($lane.Name); continue }
        if (!$lane.Completed -or $lane.ExitCode -eq 0) { continue }
        # 任一通道被取消视为本轮取消, 不再以其他通道的连带失败替代.
        if ($lane.ExitCode -eq 130) { return (New-PbiGateLaneException $lane $false) }
        $candidates.Add([pscustomobject]@{ Order = Get-PbiGateStageOrder $lane.Action; Action = $lane.Action; Code = $lane.ExitCode; Lane = $lane; Failure = $null })
    }
    $mainAction = if ($null -ne $MainFailure -and $MainFailure.Exception.Data.Contains('Action')) { [string]$MainFailure.Exception.Data['Action'] } else { 'buildC#' }
    if ($mainIsTerminated) { $terminated.Add($mainAction) }
    elseif ($null -ne $MainFailure -and !$mainIsCheckpoint) {
        $candidates.Add([pscustomobject]@{ Order = Get-PbiGateStageOrder $mainAction; Action = $mainAction; Code = Get-PbiExitCode $MainFailure.Exception; Lane = $null; Failure = $MainFailure })
    }
    if ($terminated.Count) { Write-PbiMessage ('首个失败后已立即终止在途阶段: ' + ($terminated -join '、')) 'Warning' $true }
    if (!$candidates.Count) { return $MainFailure }
    $first = $candidates[0]
    foreach ($candidate in $candidates) { if ($candidate.Order -lt $first.Order) { $first = $candidate } }
    foreach ($candidate in $candidates) {
        if (![object]::ReferenceEquals($candidate, $first)) { Write-PbiMessage "其他失败阶段 [$($candidate.Action)] (退出码 $($candidate.Code))" 'Warning' $true }
    }
    if ($null -ne $first.Failure) { return $first.Failure }
    return (New-PbiGateLaneException $first.Lane $false)
}

<#
.SYNOPSIS 等待 Vue 检查与测试通道通过, 以同轮输入指纹决定复用 lint 或执行完整构建脚本.
.PARAMETER Gate 当前并行门禁上下文, 含通道启动前取得的前端输入指纹.
.NOTES 等待期间任一通道失败立即停止, 不构建前端; 外层 Vue 检查上下文在 finally 恢复.
#>
function Complete-PbiFrontendGate([pscustomobject]$Gate) {
    Wait-PbiGateLanes $Gate @('checkVue', 'testVue')
    $savedFrontend = $script:FrontendValidation
    $script:FrontendValidation = @{ Fingerprint = $Gate.FrontendFingerprint }
    try { Invoke-PbiAction 'buildVue' @{} }
    finally { $script:FrontendValidation = $savedFrontend }
}

<#
.SYNOPSIS 确认门禁没有已记录的失败后启动通道, 失败后不再开始新阶段.
.PARAMETER Gate 当前并行门禁上下文.
.PARAMETER Name 允许列表中的通道名称.
.OUTPUTS 已加入门禁的通道记录; 已有失败时抛出检查点异常.
#>
function Start-PbiGateStage([pscustomobject]$Gate, [string]$Name) {
    Assert-PbiGateRunning $Gate
    return (Start-PbiGateLane $Gate $Name)
}

<#
.SYNOPSIS 运行 C#/Vue 检查与测试通道并编译 C#; 全部通过后交付, 再按需注册开发外部工具.
.PARAMETER Options 同一 Scope 与 Configuration, DotNetMode 只传给构建, check 不接 Configuration.
.PARAMETER RegisterExternalTool 是否在完整桌面交付后注册开发入口; release 内部调用关闭此步骤.
.PARAMETER BeforeDelivery 可选内部回调, 在 staging 完成清单后、等待全部通道前执行, 参数为 staging 路径.
.NOTES checkC#、testC#、checkVue、testVue 为独立子进程; Vue 构建等待 Vue 检查与测试通过并核对同轮输入指纹.
       逻辑处理器充足时四个通道与 C# 编译同时执行; 不足时按 checkC#、testC#、C# 编译、checkVue、testVue 顺序逐个执行.
       任一阶段失败后立即终止其余在途通道与主流程在途原生命令, 不再开始新阶段, 按规范阶段顺序报告首个真实失败;
       取消时排空在途通道. 失败不替换旧产物, 注册失败保留已交付产物.
#>
function Invoke-PbiVerifyAndBuild([hashtable]$Options, [bool]$RegisterExternalTool = $true, [scriptblock]$BeforeDelivery = $null) {
    $verificationGate = New-PbiVerificationGate $Options
    $deliveryCallback = $BeforeDelivery
    $mainFailure = $null
    $savedGate = $script:ActiveGate
    $script:ActiveGate = $verificationGate
    try {
        if ($verificationGate.Parallel) { Write-PbiMessage '开始并行门禁: checkC#、testC#、checkVue、testVue 与 C# 编译同时执行; 任一阶段失败立即终止其余阶段.' }
        else { Write-PbiMessage ("本机逻辑处理器 {0} 个, 少于并行门禁所需的 {1} 个: 按 checkC#、testC#、C# 编译、checkVue、testVue 顺序逐个执行." -f [Environment]::ProcessorCount, $script:ParallelGateMinProcessors) }
        $null = Start-PbiGateStage $verificationGate 'checkC#'
        if (!$verificationGate.Parallel) { Wait-PbiGateLanes $verificationGate @('checkC#') }
        $null = Start-PbiGateStage $verificationGate 'testC#'
        if (!$verificationGate.Parallel) { Wait-PbiGateLanes $verificationGate @('testC#') }
        # 指纹早于 Vue 通道取得, 构建前一致才可证明 lint 覆盖本轮构建输入.
        try { $verificationGate.FrontendFingerprint = Get-PbiFrontendInputFingerprint }
        catch {
            if (!$_.Exception.Data.Contains('Action')) { $_.Exception.Data['Action'] = 'checkVue' }
            throw
        }
        if ($verificationGate.Parallel) {
            $null = Start-PbiGateStage $verificationGate 'checkVue'
            $null = Start-PbiGateStage $verificationGate 'testVue'
        }
        $buildOptions = @{ Scope = $Options.Scope; Configuration = $Options.Configuration }
        if ($Options.ContainsKey('DotNetMode')) { $buildOptions.DotNetMode = $Options.DotNetMode }
        Assert-PbiGateRunning $verificationGate
        Invoke-PbiAction 'buildC#' $buildOptions -AfterCompile {
            if (!$verificationGate.Parallel) {
                $null = Start-PbiGateStage $verificationGate 'checkVue'
                Wait-PbiGateLanes $verificationGate @('checkVue')
                $null = Start-PbiGateStage $verificationGate 'testVue'
            }
            Complete-PbiFrontendGate $verificationGate
        } -BeforeDelivery {
            param([string]$Staging)
            Assert-PbiGateRunning $verificationGate
            if ($deliveryCallback) { & $deliveryCallback $Staging }
            Wait-PbiGateLanes $verificationGate
        }
    }
    catch { $mainFailure = $_ }
    finally {
        $script:ActiveGate = $savedGate
        # 非取消失败立即终止在途通道; 主流程或任一通道已表明取消时 (真实 Ctrl+C 跳过 catch, 子进程收到同一取消) 按取消排空.
        $terminateLanes = $false
        if ($null -ne $mainFailure -and (Get-PbiExitCode $mainFailure.Exception) -ne 130) {
            $terminateLanes = $true
            try { if ((Get-PbiGateStopReason $verificationGate) -ceq 'cancelled') { $terminateLanes = $false } }
            catch { [Console]::Error.WriteLine('并行门禁通道状态读取失败, 按失败终止在途通道: ' + $_.Exception.Message) }
        }
        Stop-PbiVerificationGate $verificationGate $terminateLanes
    }
    $failure = Resolve-PbiGateFailure $verificationGate $mainFailure
    if ($null -ne $failure) { throw $failure }
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
.SYNOPSIS 在 package 动作边界内执行 release 的安装包预先生成、延后交付或丢弃, 复用同一打包脚本与事务.
.PARAMETER Options 只含 DotNetMode 的打包选项.
.PARAMETER Context release 内部打包上下文, 含 Source、DeferDelivery、Pending、Discard 与 MetadataPrefetch.
.NOTES release 入口已检查 makensis; 失败记录 package 阶段并保留原退出码, 只有交付完成才打印完成.
#>
function Invoke-PbiInstallerStep([hashtable]$Options, [hashtable]$Context) {
    $stepOptions = @{ Choice = 'package'; DotNetMode = $Options.DotNetMode }
    $item = Resolve-PbiAction $stepOptions
    $discard = $Context.ContainsKey('Discard') -and $Context.Discard
    $prepare = !$discard -and ($null -eq $Context.Pending)
    if ($prepare) { Write-PbiMessage "开始 package (DotNetMode=$($stepOptions.DotNetMode); 门禁等待期间从本轮 staging 生成)" }
    $PbiInvocation = @{ Options = $stepOptions; Action = $item.Name; PackageContext = $Context }
    try { . (Join-Path $PSScriptRoot $item.Script) }
    catch {
        if (!$_.Exception.Data.Contains('Action')) { $_.Exception.Data['Action'] = 'package' }
        throw
    }
    if (!$discard -and !$Context.DeferDelivery) { Write-PbiMessage ("完成 package ({0:N1}s)" -f $Context.Pending.Stopwatch.Elapsed.TotalSeconds) 'Success' }
}

<#
.SYNOPSIS 对选中模式执行完整 Release 门禁; 交付前等待测试期间从本轮 staging 预先生成安装包, 全部通过并交付发布目录后再交付安装包.
.PARAMETER Options 已规范化的 DotNetMode, 不接受局部范围或 Debug 配置.
.NOTES 官方 .NET 元数据在门禁开始时后台预取. 编译、测试或打包失败保留原始退出码,
       未交付的安装包被丢弃并释放打包锁, 已有安装包保持不变; 不自动重试或改用其他模式.
#>
function Invoke-PbiRelease([hashtable]$Options) {
    Import-Module (Join-Path $PSScriptRoot 'InstallerMetadata.psm1') -Force
    $releasePackageOptions = @{ DotNetMode = $Options.DotNetMode }
    $releasePackageContext = @{ DeferDelivery = $true; Pending = $null; MetadataPrefetch = (Start-PbiReleaseMetadataPrefetch $script:Root) }
    try {
        Invoke-PbiVerifyAndBuild @{ Scope = 'All'; Configuration = 'Release'; DotNetMode = $Options.DotNetMode } -RegisterExternalTool $false -BeforeDelivery {
            param([string]$Staging)
            $releasePackageContext.Source = $Staging
            Invoke-PbiInstallerStep $releasePackageOptions $releasePackageContext
        }
        # 发布目录已在全部通道通过后交付, 再交付同一 staging 生成的安装包.
        $releasePackageContext.DeferDelivery = $false
        Invoke-PbiInstallerStep $releasePackageOptions $releasePackageContext
    }
    finally {
        if ($null -ne $releasePackageContext.Pending -and !$releasePackageContext.Pending.Closed) {
            $releasePackageContext.Discard = $true
            try { Invoke-PbiInstallerStep $releasePackageOptions $releasePackageContext }
            catch { [Console]::Error.WriteLine('未交付安装包的临时文件清理失败: ' + $_.Exception.Message) }
        }
        Stop-PbiReleaseMetadataPrefetch $releasePackageContext.MetadataPrefetch
    }
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
.PARAMETER BeforeDelivery 内部构建回调, staging 完成清单后、交付前执行, 参数为 staging 路径, 不接受为 CLI 参数.
.NOTES 工具或依赖资产缺失返回 3; 动作失败保留原退出码, 不隐式还原.
       verifyAndBuild 与 release 在启动任何并行通道、预取或编译前核验工具及含测试项目的还原资产, 前置不足时不启动子进程.
#>
function Invoke-PbiAction([string]$Name, [hashtable]$Options, [scriptblock]$AfterCompile, [scriptblock]$BeforeDelivery) {
    $Options.Choice = $Name
    $item = Resolve-PbiAction $Options
    Assert-PbiTools $item.Requires
    if ($item.Name -in @('verifyAndBuild', 'release', 'buildC#', 'testC#', 'checkC#', 'formatC#')) {
        # release 固定 Release/All, 门禁通道各自也会复核, 但须在主流程启动通道前统一失败.
        $restoreScope = if ($item.Name -eq 'release') { 'All' } else { $Options.Scope }
        Assert-PbiRestoreAssets $restoreScope ($item.Name -ne 'buildC#')
    }
    if ($item.Name -eq 'testScripts') { Assert-PbiRestoreAssets 'Core' -TestProject $script:CliTestProject }
    if ($item.Name -eq 'help') { Show-PbiHelp; return }
    if ($item.Name -eq 'verifyAndBuild') { Invoke-PbiVerifyAndBuild $Options; return }
    if ($item.Name -eq 'release') { Invoke-PbiRelease $Options; return }
    $PbiInvocation = @{ Options = $Options; Action = $item.Name; AfterCompile = $AfterCompile; BeforeDelivery = $BeforeDelivery }
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

<#
.SYNOPSIS 根入口统一错误边界, 临时使用 UTF-8, 返回退出码并恢复目录、编码和环境变量.
.PARAMETER Arguments 原始参数数组.
.PARAMETER Action 下层脚本固定动作; 根入口为空.
.PARAMETER Lane 内部并行通道模式, 仅由 lane.ps1 使用; 只执行允许列表中的单个阶段并写回结果记录.
.OUTPUTS 产品约定退出码; 管道被停止时没有返回值.
.NOTES 真实 Ctrl+C 停止管道时 PowerShell 跳过 catch, 只执行 finally; 此时恢复现场, 通道模式补写取消结果并输出取消提示.
       模块不调用 exit: 入口脚本预设 130, 并在自身 finally 中以该值或本函数返回的退出码结束.
#>
function Invoke-PbiEntry([object[]]$Arguments, [string]$Action = '', [switch]$Lane) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $savedTools = $script:ValidatedTools
    $savedPrefix = $script:LinePrefix
    $script:ValidatedTools = @{}
    $saved = Get-Location
    $savedInputEncoding = [Console]::InputEncoding
    $savedOutputEncoding = [Console]::OutputEncoding
    $savedPipeEncoding = $OutputEncoding
    $environment = @{}
    foreach ($key in @('DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'PNPM_DISABLE_SELF_UPDATE_CHECK')) { $environment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
    $options = @{}
    $laneResult = ''
    $laneName = ''
    # 只有正常返回或进入 catch 才置位; finally 中仍为 false 表示管道被真实 Ctrl+C 停止.
    $settled = $false
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
        if ($Lane) {
            $laneOptions = ConvertTo-PbiLaneOptions @($Arguments)
            $laneName = $laneOptions.Lane
            $laneResult = $laneOptions.Result
            $script:LinePrefix = "[$laneName] "
            $priority = $script:GateLanes[$laneName]
            # Core 范围逐项目执行格式检查, 该通道自身最长, 不再让出 CPU; 逐个执行时各通道独占本机, 也保持正常优先级.
            if (($laneName -ceq 'checkC#' -and $laneOptions.Scope -ceq 'Core') -or !(Test-PbiParallelGate)) { $priority = 'Normal' }
            if ($priority -ne 'Normal') {
                # 有余量的通道降低自身优先级, 之后启动的原生子进程继承该优先级, 让出关键路径 CPU.
                [Diagnostics.Process]::GetCurrentProcess().PriorityClass = [Diagnostics.ProcessPriorityClass]$priority
            }
            Invoke-PbiGateLaneBody $laneName $laneOptions
            Write-PbiLaneResult $laneResult 0 $laneName ''
            $settled = $true
            return 0
        }
        $options = ConvertTo-PbiOptions @($Arguments)
        if ($Action) {
            if ($options.ContainsKey('Choice')) { Stop-Pbi 2 '下层脚本不接受 Choice' }
            $options.Choice = $Action
        }
        $selectors = @('List', 'Help', 'Choice') | Where-Object { $options.ContainsKey($_) }
        if (@($selectors).Count -gt 1) { Stop-Pbi 2 'List/Help/Choice 互斥' }
        if ($options.ContainsKey('List') -or $options.ContainsKey('Help')) {
            if (@($options.Keys | Where-Object { $_ -notin @('List', 'Help', 'NonInteractive') }).Count) { Stop-Pbi 2 '元命令不接受动作参数' }
            Show-PbiHelp; $settled = $true; return 0
        }
        if (!$options.ContainsKey('Choice')) {
            if ($options.ContainsKey('NonInteractive') -or $env:CI -or [Console]::IsInputRedirected) { Stop-Pbi 2 '非交互调用必须指定 -Choice, 或使用 -Help' }
            Show-PbiHelp
            $choice = Read-Host '选择动作 (q 退出)'
            if ($choice -eq 'q') { $settled = $true; return 0 }
            $options.Choice = $choice
        }
        $item = Resolve-PbiAction $options
        Invoke-PbiAction $item.Name $options
        if ($item.Name -eq 'verifyAndBuild') { Write-PbiMessage ("verifyAndBuild 全部通过 ({0:N1}s)" -f $timer.Elapsed.TotalSeconds) 'Success' }
        if ($item.Name -eq 'release') { Write-PbiMessage ("release 全部通过 ({0:N1}s)" -f $timer.Elapsed.TotalSeconds) 'Success' }
        $settled = $true
        return 0
    }
    catch {
        $settled = $true
        $code = Get-PbiExitCode $_.Exception
        $failedAction = if ($_.Exception.Data.Contains('Action')) { " [$($_.Exception.Data['Action'])]" } else { '' }
        if ($laneResult) {
            $resultAction = if ($_.Exception.Data.Contains('Action')) { [string]$_.Exception.Data['Action'] } else { $laneName }
            Write-PbiLaneResult $laneResult $code $resultAction $_.Exception.Message
        }
        if ($code -eq 130) { Write-PbiMessage "已取消$failedAction (退出码 130)." 'Info' $true }
        else { Write-PbiMessage ("执行失败$failedAction (退出码 $code): " + $_.Exception.Message) 'Error' }
        return $code
    }
    finally {
        try {
            if (!$settled) {
                if ($laneResult) { Write-PbiLaneResult $laneResult 130 $laneName '已取消' }
                Write-PbiMessage '已取消 (退出码 130).' 'Info' $true
            }
        }
        finally {
            $script:ValidatedTools = $savedTools
            $script:LinePrefix = $savedPrefix
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
}

Export-ModuleMember -Function *-Pbi*
