# FilePath    : pbi-utils\scripts\tests\contracts.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在临时仓库验证脚本注册, 编排, 环境恢复与安全清理契约.

<# .SYNOPSIS 无 Pester 安装依赖的脚本契约测试, 由固定版本 xUnit 执行. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils 测试 ' + [Guid]::NewGuid().ToString('N'))
$pwsh = (Get-Process -Id $PID).Path
$savedOutputEncoding = [Console]::OutputEncoding

<# .SYNOPSIS 断言契约, 不通过时终止测试. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }

try {
    # 测试报告经重定向传给 xUnit, 与其 UTF-8 读取契约保持一致.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'scripts')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'run.ps1') -Destination $sandbox
    foreach ($file in @('CommandRegistry.psm1', 'TestResults.psm1', 'clean.ps1', 'setup.ps1', 'build.ps1', 'test.ps1', 'format.ps1', 'check.ps1')) { Copy-Item -LiteralPath (Join-Path $sourceRoot "scripts/$file") -Destination (Join-Path $sandbox 'scripts') }
    foreach ($file in @('AGENTS.md', 'pbi-utils.slnx', 'Directory.Build.props')) { Set-Content -LiteralPath (Join-Path $sandbox $file) -Value 'sentinel' }
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'artifacts')
    Set-Content -LiteralPath (Join-Path $sandbox 'artifacts/generated.txt') -Value 'generated'
    Set-Content -LiteralPath (Join-Path $sandbox 'user.txt') -Value 'protected'
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'src/PbiUtils.Core/obj') -Force
    Set-Content -LiteralPath (Join-Path $sandbox 'src/PbiUtils.Core/obj/project.assets.json') -Value '{}'
    Import-Module (Join-Path $sandbox 'scripts/CommandRegistry.psm1') -Force
    $registry = Get-PbiRegistry
    $releaseDefaults = @{ Choice = 'all'; Configuration = 'Release' }
    $null = Resolve-PbiAction $releaseDefaults
    Assert ($releaseDefaults.ContainsKey('DotNetMode') -and $releaseDefaults.DotNetMode -ceq 'without-dotnet') 'Release 构建必须默认只选择 without-dotnet 模式'
    $coreProjects = @(Get-PbiProjects 'Core')
    Assert ($coreProjects -contains 'src/PbiUtils.Host' -and $coreProjects -contains 'src/PbiUtils.Windows') 'Core 必须包含无 UI 业务后台及 Windows 依赖'
    Assert ($coreProjects -notcontains 'src/PbiUtils.Desktop') 'Core 不得包含桌面 UI'
    Assert (@(Get-PbiFormatTargets 'Core') -contains 'src/PbiUtils.Host/PbiUtils.Host.csproj') 'Core 格式检查必须覆盖业务后台'
    Assert (@($registry).Count -eq 22) '注册数量'
    Assert (@($registry.Id | Select-Object -Unique).Count -eq 22) '编号唯一'
    Assert (($registry.Id -join ',') -eq ((0..21) -join ',')) '编号按菜单顺序连续递增'
    Assert (@($registry.Name | Select-Object -Unique).Count -eq 22) '名称唯一'
    foreach ($entry in $registry) {
        if ($entry.Name.EndsWith('C#')) { Assert (($entry.Requires -join ',') -eq 'dotnet') 'C# 不依赖 Node/pnpm' }
        if ($entry.Name.EndsWith('Vue')) { Assert (($entry.Requires -join ',') -eq 'node,pnpm') 'Vue 不依赖 dotnet' }
        # 全部动作均须由编号与名称解析到同一条注册项.
        $numeric = Resolve-PbiAction @{ Choice = [string]$entry.Id }
        $named = Resolve-PbiAction @{ Choice = $entry.Name.ToUpperInvariant() }
        Assert ($numeric.Id -eq $named.Id) '全部动作编号与名称一致'
        if ('Scope' -in $entry.Parameters) {
            $defaults = @{ Choice = $entry.Name }
            $null = Resolve-PbiAction $defaults
            Assert ($defaults.Scope -eq 'All') 'C# 项目范围默认 All'
        }
        if ('Configuration' -in $entry.Parameters) {
            $defaults = @{ Choice = $entry.Name }
            $null = Resolve-PbiAction $defaults
            Assert ($defaults.Configuration -ceq 'Debug') '构建及测试配置默认规范的 Debug'
            foreach ($configuration in @('debug', 'DEBUG', 'dEbUg', 'release', 'RELEASE', 'rElEaSe')) {
                $normalized = @{ Choice = $entry.Name; Configuration = $configuration }
                $null = Resolve-PbiAction $normalized
                $expected = if ($configuration -eq 'Debug') { 'Debug' } else { 'Release' }
                Assert ($normalized.Configuration -ceq $expected) '配置输入大小写必须规范化, 防止产物路径和清单出现不同拼写'
            }
        }
    }
    foreach ($action in @('all', 'buildAll', 'buildC#')) {
        foreach ($configuration in @('Debug', 'Release')) {
            $defaults = @{ Choice = $action; Configuration = $configuration }
            $null = Resolve-PbiAction $defaults
            $expected = if ($configuration -eq 'Debug') { 'with-dotnet' } else { 'without-dotnet' }
            Assert ($defaults.DotNetMode -ceq $expected) '构建模式默认值必须按配置确定'
        }
    }
    foreach ($action in @('all', 'buildAll', 'buildC#', 'package', 'release')) {
        foreach ($mode in @('WITH-DOTNET', 'With-DotNet', 'WITHOUT-DOTNET', 'Without-DotNet')) {
            $options = ConvertTo-PbiOptions @('-Choice', $action, '-DotNetMode', $mode)
            $null = Resolve-PbiAction $options
            Assert ($options.DotNetMode -ceq $mode.ToLowerInvariant()) 'DotNetMode 输入大小写必须规范化'
        }
    }
    foreach ($action in @('package', 'release')) {
        $options = @{ Choice = $action }
        $entry = Resolve-PbiAction $options
        Assert ($options.DotNetMode -ceq 'without-dotnet') '安装包动作默认只选择 without-dotnet'
        Assert (($entry.Parameters -join ',') -ceq 'DotNetMode') '安装包动作只接受部署模式参数'
        $expectedTools = if ($action -eq 'package') { 'makensis' } else { 'dotnet,node,pnpm,makensis' }
        Assert (($entry.Requires -join ',') -ceq $expectedTools) 'package 不构建, release 完整构建后打包'
    }
    $originalPath = $env:PATH
    try {
        $env:PATH = ''
        & $pwsh -NoProfile -File (Join-Path $sandbox 'run.ps1') -List
        Assert ($LASTEXITCODE -eq 0) '缺工具帮助'
        & $pwsh -NoProfile -File (Join-Path $sandbox 'run.ps1') -Choice clean -WhatIf
        Assert ($LASTEXITCODE -eq 0) '缺工具 WhatIf'
    } finally { $env:PATH = $originalPath }
    Assert (Test-Path -LiteralPath (Join-Path $sandbox 'artifacts/generated.txt')) 'WhatIf 不能删除'
    Assert (Test-Path -LiteralPath (Join-Path $sandbox 'user.txt')) '保护用户文件'
    $outside = Join-Path $sandbox 'outside'
    $null = New-Item -ItemType Directory -Path $outside
    Set-Content -LiteralPath (Join-Path $outside 'keep.txt') -Value 'keep'
    $link = Join-Path $sandbox 'artifacts/link'
    $null = New-Item -ItemType Junction -Path $link -Target $outside
    & $pwsh -NoProfile -File (Join-Path $sandbox 'run.ps1') -Choice clean -WhatIf
    Assert ($LASTEXITCODE -eq 4) '子树链接拒绝'
    & $pwsh -NoProfile -File (Join-Path $sandbox 'run.ps1') -Choice clean
    Assert ($LASTEXITCODE -eq 4) '真实清理链接拒绝'
    Assert (Test-Path -LiteralPath (Join-Path $outside 'keep.txt')) '链接目标保护'
    Remove-Item -LiteralPath $link -Force
    & $pwsh -NoProfile -File (Join-Path $sandbox 'run.ps1') -Choice clean
    Assert ($LASTEXITCODE -eq 0) '真实白名单清理'
    Assert (!(Test-Path -LiteralPath (Join-Path $sandbox 'artifacts'))) '产物已删'
    Assert (!(Test-Path -LiteralPath (Join-Path $sandbox 'src/PbiUtils.Core/obj/project.assets.json'))) 'clean 删除依赖资产'
    Assert (Test-Path -LiteralPath (Join-Path $sandbox 'user.txt')) '源码与用户文件保留'
    $module = Get-Module CommandRegistry
    & $module {
        $script:MakensisCalls = 0
        $script:MakensisCode = 0
        $script:MakensisVersion = 'v3.13'
        <# .SYNOPSIS 模拟 NSIS 官方版本参数, 不读取 SDK/前端配置或执行打包. #>
        function makensis {
            Assert ($args.Count -eq 1 -and $args[0] -ceq '/VERSION') 'NSIS 版本探测必须使用 /VERSION'
            $script:MakensisCalls++
            $global:LASTEXITCODE = $script:MakensisCode
            $script:MakensisVersion
        }
        $originalTools = $script:ValidatedTools
        try {
            $script:ValidatedTools = @{}
            Assert-PbiTools @('makensis')
            Assert-PbiTools @('makensis')
            Assert ($script:MakensisCalls -eq 1) '同轮 NSIS 检查应复用, 不调用无关工具'
            $script:ValidatedTools = @{}
            $script:MakensisCode = 53
            try { Assert-PbiTools @('makensis'); throw 'NSIS 版本查询失败未停止' }
            catch { Assert ($_.Exception.Data['ExitCode'] -eq 53) 'NSIS 版本查询保留工具退出码' }
            $script:ValidatedTools = @{}
            $script:MakensisCode = 0
            $script:MakensisVersion = 'unexpected output'
            try { Assert-PbiTools @('makensis'); throw 'NSIS 无效版本未停止' }
            catch { Assert ($_.Exception.Data['ExitCode'] -eq 3) 'NSIS 无效输出必须返回前置条件错误' }
        } finally { $script:ValidatedTools = $originalTools }
    }
    & $module {
        # 合成 SDK 环境覆盖版本选择与失败诊断, 不安装工具或修改真实仓库配置.
        $configPath = Join-Path $script:Root 'global.json'
        $validConfig = '{"sdk":{"version":"98.7.601","rollForward":"disable","allowPrerelease":false}}'
        Set-Content -LiteralPath $configPath -Value $validConfig
        $script:SdkList = @('98.7.601 [C:\SDK tools\sdk]', '98.7.602 [C:\SDK tools\sdk]')
        $script:SelectedSdk = '98.7.601'
        $script:SdkListCode = 0
        $script:SdkVersionCode = 0
        $script:SdkCalls = [Collections.Generic.List[string]]::new()
        <# .SYNOPSIS 模拟 SDK 查询及原生退出码, 其他命令一律拒绝以证明未开始构建. #>
        function dotnet([string]$Argument) {
            $script:SdkCalls.Add($Argument)
            switch ($Argument) {
                '--list-sdks' { $global:LASTEXITCODE = $script:SdkListCode; $script:SdkList }
                '--version' { $global:LASTEXITCODE = $script:SdkVersionCode; $script:SelectedSdk }
                default { throw 'SDK 失败后不得调用构建或还原' }
            }
        }
        <# .SYNOPSIS 验证 SDK 失败的退出码、关键提示和只读配置边界. #>
        function Assert-SdkFailure([int]$Code, [string[]]$Messages) {
            $before = Get-Content -LiteralPath $configPath -Raw
            try { Assert-PbiTools @('dotnet'); throw 'SDK 校验应失败' }
            catch {
                Assert ($_.Exception.Data['ExitCode'] -eq $Code) 'SDK 校验退出码'
                foreach ($message in $Messages) { Assert ($_.Exception.Message.Contains($message)) "SDK 提示缺少: $message" }
            }
            Assert ((Get-Content -LiteralPath $configPath -Raw) -ceq $before) 'SDK 校验不得修改 global.json'
        }
        Assert-PbiTools @('dotnet')
        Assert (($script:SdkCalls -join ',') -eq '--list-sdks,--version') '多个 SDK 并存且绑定版本正常选中时通过'
        $script:SelectedSdk = '98.7.602'
        Assert-SdkFailure 3 @('实际选中: 98.7.602', '98.7.601', '是否更新 global.json', 'PATH')
        $script:SdkList = @('98.7.602 [C:\SDK tools\sdk]')
        $script:SdkCalls.Clear()
        Assert-SdkFailure 3 @('缺少 global.json 绑定', '98.7.601', '98.7.602', '是否更新 global.json')
        Assert (($script:SdkCalls -join ',') -eq '--list-sdks') '缺少绑定 SDK 时不执行必然失败的 --version'
        foreach ($action in @('all', 'buildC#', 'setupC#')) {
            Assert ((Invoke-PbiEntry -Arguments @('-Choice', $action, '-NonInteractive')) -eq 3) '入口在 SDK 不匹配时首错停止'
        }
        $script:SdkList = @()
        Assert-SdkFailure 3 @('未发现任何 SDK', '安装 SDK 98.7.601')
        $script:SdkList = @('synthetic SDK query error')
        $script:SdkListCode = 47
        Assert-SdkFailure 47 @('无法查询', 'synthetic SDK query error')
        $script:SdkListCode = 0
        $script:SdkList = @('98.7.601 [C:\SDK tools\sdk]')
        $script:SdkVersionCode = 48
        $script:SelectedSdk = 'synthetic SDK resolver error'
        Assert-SdkFailure 48 @('SDK 版本解析失败', 'synthetic SDK resolver error', '是否更新 global.json')
        foreach ($invalid in @('{}', 'null', '{', '{"sdk":null}', '{"sdk":{"version":null}}', '{"sdk":{"version":"98.7"}}', '{"sdk":{"version":"98.7.601-preview.1"}}')) {
            Set-Content -LiteralPath $configPath -Value $invalid
            $script:SdkCalls.Clear()
            Assert-SdkFailure 3 @('global.json', 'sdk.version')
            Assert ($script:SdkCalls.Count -eq 0) '无效绑定在调用工具前失败'
        }
        Set-Content -LiteralPath $configPath -Value $validConfig
        $script:SelectedSdk = '98.7.601'
        $script:SdkVersionCode = 0
        $script:SdkCalls.Clear()
        $savedTools = $script:ValidatedTools
        try {
            $script:ValidatedTools = @{}
            Assert-PbiTools @('dotnet')
            Assert-PbiTools @('dotnet')
            Assert ($script:SdkCalls.Count -eq 2) '同一入口 SDK 查询仅执行一轮'
        } finally { $script:ValidatedTools = $savedTools }
        # 隔离 Get-Command 以模拟完全未安装 dotnet, 不改变系统 PATH.
        function Get-Command([string]$Name) { if ($Name -ne 'dotnet') { throw '只应查询 dotnet' } }
        Assert-SdkFailure 3 @('未找到 dotnet', '98.7.601', '是否更新 global.json')
    }
    & $module {
        # 在隔离仓库变更 packageManager, 验证 pnpm 校验仅使用此版本声明.
        $null = New-Item -ItemType Directory -Path (Join-Path $script:Root 'web')
        $packagePath = Join-Path $script:Root 'web/package.json'
        function pnpm { $global:LASTEXITCODE = 0; '98.7.6' }
        Set-Content -LiteralPath $packagePath -Value '{"packageManager":"pnpm@98.7.6"}'
        Assert-PbiTools @('pnpm')
        Set-Content -LiteralPath $packagePath -Value '{"packageManager":"pnpm@98.7.7"}'
        try { Assert-PbiTools @('pnpm'); throw '版本不符应失败' }
        catch {
            if ($_.Exception.Data['ExitCode'] -ne 3 -or $_.Exception.Message -notlike '*98.7.7*') { throw }
        }
        foreach ($invalid in @('{"packageManager":"pnpm@^98.7.6"}', '{"packageManager":"npm@98.7.6"}', '{"packageManager":null}', '{}', 'null', '{')) {
            Set-Content -LiteralPath $packagePath -Value $invalid
            try { Assert-PbiTools @('pnpm'); throw '非精确或缺失版本应失败' }
            catch { if ($_.Exception.Data['ExitCode'] -ne 3) { throw } }
        }
        # 不创建 .node-version, 验证 Node 与 pnpm 共用 package.json 并保留 Node 输出的 v 前缀.
        function node { $global:LASTEXITCODE = 0; 'v98.7.6' }
        Set-Content -LiteralPath $packagePath -Value '{"packageManager":"pnpm@98.7.6","engines":{"node":"98.7.6"}}'
        Assert-PbiTools @('node', 'pnpm')
        Set-Content -LiteralPath $packagePath -Value '{"packageManager":"pnpm@98.7.6","engines":{"node":"98.7.7"}}'
        try { Assert-PbiTools @('node'); throw 'Node 版本不符应失败' }
        catch {
            if ($_.Exception.Data['ExitCode'] -ne 3 -or $_.Exception.Message -notlike '*v98.7.7*') { throw }
        }
        foreach ($invalid in @('{"engines":{"node":"^98.7.6"}}', '{"engines":{}}', '{}', 'null', '{')) {
            Set-Content -LiteralPath $packagePath -Value $invalid
            try { Assert-PbiTools @('node'); throw 'Node 无效配置应失败' }
            catch { if ($_.Exception.Data['ExitCode'] -ne 3) { throw } }
        }
    }
    & $module {
        # 同一入口复用版本验证, 独立入口仍重验并恢复外层验证上下文.
        $originalTools = $script:ValidatedTools
        $priorTools = @{ outside = $true }
        $script:ValidatedTools = $priorTools
        $script:VersionCalls = 0
        $script:ToolVersion = '98.7.6'
        $script:EntryFailure = 0
        Set-Content -LiteralPath (Join-Path $script:Root 'web/package.json') -Value '{"packageManager":"pnpm@98.7.6"}'
        <# .SYNOPSIS 返回可控版本并记录调用次数, 不启动真实包管理器. #>
        function pnpm { $script:VersionCalls++; $global:LASTEXITCODE = 0; $script:ToolVersion }
        <# .SYNOPSIS 重复检查同一工具, 按指定场景模拟入口失败. #>
        function Invoke-PbiAction([string]$Name, [hashtable]$Options) {
            Assert-PbiTools @('pnpm')
            Assert-PbiTools @('pnpm')
            if ($script:EntryFailure) { Stop-Pbi $script:EntryFailure '模拟入口失败' }
        }
        try {
            foreach ($failure in @(0, 47, 3)) {
                $script:EntryFailure = $failure
                if ($failure -eq 3) { $script:ToolVersion = '98.7.7' }
                Assert ((Invoke-PbiEntry -Arguments @('-Choice', 'help')) -eq $failure) '工具验证及入口失败保留退出码'
                Assert ([object]::ReferenceEquals($script:ValidatedTools, $priorTools)) '成功或失败均恢复外层工具验证上下文'
            }
            Assert ($script:VersionCalls -eq 3) '每个入口仅验证一次工具, 新入口必须重新验证'
        }
        finally { $script:ValidatedTools = $originalTools }
    }
    & $module {
        $script:Calls = [Collections.Generic.List[string]]::new()
        $script:FailureAction = ''
        $script:FailureCode = 0
        $script:BuildReturned = $false
        # 替换原生动作边界, 编译后的回调模拟交付前的前端检查, 测试与构建.
        <# .SYNOPSIS 记录动作与内部回调, 校验选项隔离并在指定阶段抛出原生退出码. #>
        function Invoke-PbiAction([string]$Name, [hashtable]$Options, [scriptblock]$AfterCompile) {
            $script:Calls.Add($Name)
            if ($Name.EndsWith('Vue') -and $Options.Count) { throw 'Vue 收到 C# 参数' }
            if ($Name.EndsWith('C#') -and $Options.Scope -ne 'Core') { throw 'C# Scope 丢失' }
            if ($Name -eq 'checkC#' -and $Options.ContainsKey('Configuration')) { throw 'check 接收了 Configuration' }
            if ($Name -in @('buildC#', 'testC#') -and $Options.Configuration -ne 'Release') { throw 'Configuration 丢失' }
            if ($Name -eq 'buildC#' -and $Options.DotNetMode -cne 'with-dotnet') { throw 'all 丢失显式部署模式' }
            if ($Name -ne 'buildC#' -and $Options.ContainsKey('DotNetMode')) { throw '非构建动作收到部署模式' }
            if ($Options.ContainsKey('AfterCompile')) { throw '内部回调混入 CLI 选项' }
            if ($Name -eq 'buildC#' -and !$AfterCompile) { throw 'all 缺少编译后回调' }
            if ($Name -ne 'buildC#' -and $AfterCompile) { throw '回调传给非构建动作' }
            if ($Name -eq $script:FailureAction) { Stop-Pbi $script:FailureCode '模拟阶段失败' }
            if ($AfterCompile) { & $AfterCompile; $script:BuildReturned = $true }
        }
        $options = @{ Scope = 'Core'; Configuration = 'Release'; DotNetMode = 'with-dotnet' }
        $expected = @('checkC#', 'testC#', 'buildC#', 'checkVue', 'testVue', 'buildVue')
        Invoke-PbiAll $options
        Assert (($script:Calls -join ',') -eq ($expected -join ',')) 'all 必须先 C# 后前端, 每侧均按检查、测试、构建顺序'
        Assert $script:BuildReturned '前端完成后构建动作才返回'
        Assert ($options.Count -eq 3) 'all 不改变调用者选项'
        for ($index = 0; $index -lt $expected.Count; $index++) {
            $script:Calls.Clear()
            $script:BuildReturned = $false
            $script:FailureAction = $expected[$index]
            $script:FailureCode = 41 + $index
            try { Invoke-PbiAll $options; throw 'all 阶段失败未停止' }
            catch { if ($_.Exception.Data['ExitCode'] -ne $script:FailureCode) { throw } }
            Assert (($script:Calls -join ',') -eq ($expected[0..$index] -join ',')) 'all 两侧检查、测试、构建的每个阶段均须首错停止'
            Assert (!$script:BuildReturned) '失败阶段不得完成构建交付'
        }
    }
    & $module {
        $script:Calls = [Collections.Generic.List[string]]::new()
        $script:FailureAction = ''
        # 模拟动作分发, 验证组合顺序、参数隔离、首错停止, 不调用真实工具.
        <# .SYNOPSIS 记录语言动作, 模拟构建后的前端回调并在指定动作抛出失败. #>
        function Invoke-PbiAction([string]$Name, [hashtable]$Options, [scriptblock]$AfterCompile) {
            $script:Calls.Add($Name)
            if ($Name.EndsWith('Vue') -and $Options.Count) { throw 'Vue 收到 C# 参数' }
            if ($Name.EndsWith('C#') -and $Options.Scope -ne 'Core') { throw 'C# Scope 丢失' }
            if ($Name -in @('buildC#', 'testC#') -and $Options.Configuration -ne 'Release') { throw '配置丢失' }
            if ($Name -eq 'buildC#' -and $Options.DotNetMode -cne 'with-dotnet') { throw 'buildAll 丢失显式部署模式' }
            if ($Name -ne 'buildC#' -and $Options.ContainsKey('DotNetMode')) { throw '非构建组合收到部署模式' }
            if ($Options.ContainsKey('AfterCompile')) { throw '内部回调混入 CLI 选项' }
            if ($Name -eq 'buildC#' -and !$AfterCompile) { throw 'buildAll 缺少编译后回调' }
            if ($Name -ne 'buildC#' -and $AfterCompile) { throw '回调传给非构建动作' }
            if ($Name -eq $script:FailureAction) { Stop-Pbi 47 '模拟失败' }
            if ($AfterCompile) { & $AfterCompile }
        }
        foreach ($family in @('setup', 'build', 'test', 'format', 'check')) {
            $script:Calls.Clear()
            $options = @{ Scope = 'Core' }
            if ($family -in @('build', 'test')) { $options.Configuration = 'Release' }
            if ($family -eq 'build') { $options.DotNetMode = 'with-dotnet' }
            if (!(Invoke-PbiGroup $family ($family + 'All') $options)) { throw '组合未分发' }
            $expected = $family + 'C#,' + $family + 'Vue'
            if (($script:Calls -join ',') -ne $expected) { throw '组合顺序错误' }
        }
        foreach ($family in @('setup', 'build', 'test', 'format', 'check')) {
            foreach ($target in @('C#', 'Vue')) {
                $script:Calls.Clear()
                $script:FailureAction = $family + $target
                $options = @{ Scope = 'Core'; Configuration = 'Release' }
                if ($family -eq 'build') { $options.DotNetMode = 'with-dotnet' }
                try { Invoke-PbiGroup $family ($family + 'All') $options; throw '未停止' }
                catch { if ($_.Exception.Data['ExitCode'] -ne 47) { throw } }
                $expected = if ($target -eq 'C#') { $family + 'C#' } else { $family + 'C#,' + $family + 'Vue' }
                Assert (($script:Calls -join ',') -eq $expected) '组合每个语言阶段均须首错停止'
            }
        }
        $script:Calls.Clear()
        Assert (!(Invoke-PbiGroup 'build' 'buildC#' @{ Scope = 'Core' })) '独立动作不进入组合分发'
        Assert ($script:Calls.Count -eq 0) '独立动作不触发组合回调'
    }
    & $module {
        # 只修正 .NET restore 的精确本地化提示, 失败与其他工具文本不可被弱化.
        $original = '  所有项目均是最新的，无法还原。'
        Assert ((ConvertTo-PbiNativeText 'dotnet' @('restore', 'pbi-utils.slnx') $original) -eq '  所有项目依赖均为最新, 无需还原.') '无需还原语义应准确'
        foreach ($line in @('无法还原依赖: 真实错误', 'error NU1101: 找不到包, 无法还原。', '所有项目均是最新的，无法还原。失败', '  保留其他输出与空白  ')) {
            Assert ((ConvertTo-PbiNativeText 'dotnet' @('restore') $line) -ceq $line) '未知或失败诊断必须原样保留'
        }
        foreach ($arguments in @(@('build'), @('publish'), @('test'))) {
            Assert ((ConvertTo-PbiNativeText 'dotnet' $arguments $original) -ceq $original) '非 restore 动作不可替换输出'
        }
        Assert ((ConvertTo-PbiNativeText 'pnpm' @('restore') $original) -ceq $original) '其他工具不可替换输出'
        Assert ((ConvertTo-PbiNativeText 'dotnet' @() $original) -ceq $original) '空参数不可替换输出'
    }
    & $module {
        $script:NativeCalls = [Collections.Generic.List[string]]::new()
        # 独立仓库模拟 clean 后的缺失资产, 验证提前停止及显式还原提示, 不调用真实工具.
        <# .SYNOPSIS 跳过工具版本检查, 隔离依赖资产前置条件. #>
        function Assert-PbiTools([string[]]$Tools) { }
        <# .SYNOPSIS 记录原生调用, 缺失依赖资产时应没有调用. #>
        function Invoke-PbiNative([string]$File, [string[]]$Arguments) { $script:NativeCalls.Add($File) }
        foreach ($action in @('all', 'buildC#', 'testC#', 'checkC#', 'formatC#')) {
            try { Invoke-PbiAction $action @{ Scope = 'Core' }; throw '缺失依赖资产应失败' }
            catch {
                if ($_.Exception.Data['ExitCode'] -ne 3 -or $_.Exception.Message -notlike "*setupC#*Scope Core*") { throw }
            }
        }
        if ($script:NativeCalls.Count) { throw '缺失依赖时不应开始检查或构建' }
        foreach ($project in (Get-PbiProjects 'Core' | Where-Object { $_ -notlike 'tests/*' })) {
            $assetDirectory = Join-Path $script:Root "$project/obj"
            $null = New-Item -ItemType Directory -Path $assetDirectory -Force
            Set-Content -LiteralPath (Join-Path $assetDirectory 'project.assets.json') -Value '{}'
        }
        # 应用构建不要求测试资产, Core 范围只排除桌面 UI, 包含业务后台的 Windows 依赖.
        Assert-PbiRestoreAssets 'Core' $false
        try { Assert-PbiRestoreAssets 'Core'; throw '测试资产缺失应失败' }
        catch { if ($_.Exception.Data['ExitCode'] -ne 3) { throw } }
        foreach ($project in (Get-PbiProjects 'Core' | Where-Object { $_ -like 'tests/*' })) {
            $assetDirectory = Join-Path $script:Root "$project/obj"
            $null = New-Item -ItemType Directory -Path $assetDirectory -Force
            Set-Content -LiteralPath (Join-Path $assetDirectory 'project.assets.json') -Value '{}'
        }
        Assert-PbiRestoreAssets 'Core'
        try { Assert-PbiRestoreAssets 'All'; throw 'Windows 资产缺失应失败' }
        catch { if ($_.Exception.Data['ExitCode'] -ne 3 -or $_.Exception.Message -notlike '*Scope All*') { throw } }
        foreach ($project in (Get-PbiProjects 'All' | Where-Object { !(Test-Path -LiteralPath (Join-Path $script:Root "$_/obj/project.assets.json")) })) {
            $assetDirectory = Join-Path $script:Root "$project/obj"
            $null = New-Item -ItemType Directory -Path $assetDirectory -Force
            Set-Content -LiteralPath (Join-Path $assetDirectory 'project.assets.json') -Value '{}'
        }
        Assert-PbiRestoreAssets 'All'
    }
    & $module {
        $script:NativeCalls = [Collections.Generic.List[string]]::new()
        # 运行真实分层脚本并替换工具边界, 确保 C#/Vue 不交叉调用依赖.
        function Assert-PbiTools([string[]]$Tools) { }
        <# .SYNOPSIS 记录工具边界并提供空 lint 报告及本轮通过 TRX, 不运行真实构建或分析器. #>
        function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
            $script:NativeCalls.Add($File)
            if ($Arguments[0] -in @('format', 'test')) { Assert ($Arguments -contains '--no-restore') 'C# 日常检查和测试不得隐式还原' }
            if ($Arguments[0] -eq 'format') {
                $severity = [Array]::IndexOf($Arguments, '--severity')
                Assert ($severity -ge 0 -and $Arguments[$severity + 1] -eq 'info') 'C# 必须检查建议级别'
                if ('--verify-no-changes' -in $Arguments) {
                    $report = [Array]::IndexOf($Arguments, '--report')
                    Assert ($report -ge 0) '只读 lint 必须有诊断报告'
                    Set-Content -LiteralPath $Arguments[$report + 1] -Value '[]'
                }
            }
            if ($Arguments[0] -eq 'test') {
                $logger = $Arguments[[Array]::IndexOf($Arguments, '--logger') + 1]
                Assert ($logger -match '^trx;LogFileName=(.+\.trx)$') '每次测试必须使用本轮唯一 TRX 文件名'
                $name = $Matches[1]
                $directory = [IO.Path]::GetFullPath($Arguments[[Array]::IndexOf($Arguments, '--results-directory') + 1], (Get-Location).Path)
                $null = New-Item -ItemType Directory -Path $directory -Force
                $now = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
                # 合成一项本轮通过, 让真实 TRX 门禁继续验证语言隔离及参数传播.
                $xml = @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times start="$now" finish="$now" />
  <Results><UnitTestResult outcome="Passed" /></Results>
  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" /></ResultSummary>
</TestRun>
"@
                [IO.File]::WriteAllText((Join-Path $directory $name), $xml)
            }
        }
        # 内部调用绕过正式入口的 Set-Location, 仍将本轮 TRX 限制在隔离仓库中.
        Push-Location -LiteralPath $script:Root
        try {
            foreach ($family in @('setup', 'test', 'format', 'check')) {
                foreach ($target in @('C#', 'Vue')) {
                    $script:NativeCalls.Clear()
                    $options = if ($target -eq 'C#') { @{ Scope = 'Core' } } else { @{} }
                    Invoke-PbiAction ($family + $target) $options
                    $expected = if ($target -eq 'C#') { 'dotnet' } else { 'pnpm' }
                    if (!$script:NativeCalls.Count -or @($script:NativeCalls | Where-Object { $_ -ne $expected }).Count) { throw '语言动作依赖串线' }
                }
            }
            $script:NativeCalls.Clear()
            Invoke-PbiAction 'buildVue' @{}
            if (($script:NativeCalls -join ',') -ne 'pnpm') { throw 'buildVue 调用了 C#' }
        } finally { Pop-Location }
        Push-Location $script:Root
        try {
            try { Invoke-PbiAction 'buildC#' @{ Scope = 'All' }; throw '缺页面应失败' }
            catch { if ($_.Exception.Data['ExitCode'] -ne 3) { throw } }
        } finally { Pop-Location }
    }
    & $module {
        # 即使工具退出成功, 没有自动修复器的诊断仍须阻止 lint 通过并清理临时报告.
        function Assert-PbiTools([string[]]$Tools) { }
        $script:LintReportPath = $null
        <# .SYNOPSIS 模拟分析器发现 CA1873, 保持工具退出成功以覆盖报告兜底. #>
        function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
            $report = [Array]::IndexOf($Arguments, '--report')
            $script:LintReportPath = $Arguments[$report + 1]
            Set-Content -LiteralPath $script:LintReportPath -Value '[{"FileChanges":[{"DiagnosticId":"CA1873"}]}]'
        }
        try { Invoke-PbiAction 'checkC#' @{ Scope = 'Core' }; throw '未修复诊断应失败' }
        catch { if ($_.Exception.Data['ExitCode'] -ne 1 -or $_.Exception.Message -notlike '*CA1873*') { throw } }
        Assert (!(Test-Path -LiteralPath $script:LintReportPath)) '临时 lint 报告必须清理'
    }
    & $module {
        # 用临时构建 handler 检查真实动作边界, 不触发编译或文件发布.
        <# .SYNOPSIS 跳过工具版本检查, 只验证回调在内部调用上下文中传递. #>
        function Assert-PbiTools([string[]]$Tools) { }
        $script:Calls = [Collections.Generic.List[string]]::new()
        $buildPath = Join-Path $script:Root 'scripts/build.ps1'
        $originalBuild = Get-Content -LiteralPath $buildPath -Raw
        $handler = @'
Assert (!$PbiInvocation.Options.ContainsKey('AfterCompile')) '内部回调不得混入动作选项'
Assert ($PbiInvocation.Action -eq 'buildC#') '内部 handler 仍使用注册动作'
Assert ([object]::ReferenceEquals($PbiInvocation.AfterCompile, $script:ExpectedAfterCompile)) '必须传递原始回调实例'
$script:Calls.Add('compile')
if ($PbiInvocation.AfterCompile) { & $PbiInvocation.AfterCompile }
$script:Calls.Add('deliver')
'@
        try {
            Set-Content -LiteralPath $buildPath -Value $handler
            $script:ExpectedAfterCompile = { $script:Calls.Add('frontend') }
            Invoke-PbiAction 'buildC#' @{ Scope = 'Core'; Configuration = 'Release' } -AfterCompile $script:ExpectedAfterCompile
            Assert (($script:Calls -join ',') -eq 'compile,frontend,deliver') '回调须在编译与交付之间执行'
            $script:Calls.Clear()
            $script:ExpectedAfterCompile = { $script:Calls.Add('frontend'); Stop-Pbi 52 '模拟前端失败' }
            try {
                Invoke-PbiAction 'buildC#' @{ Scope = 'Core'; Configuration = 'Release' } -AfterCompile $script:ExpectedAfterCompile
                throw '回调失败未停止'
            }
            catch { if ($_.Exception.Data['ExitCode'] -ne 52) { throw } }
            Assert (($script:Calls -join ',') -eq 'compile,frontend') '回调失败不得继续交付'
        }
        finally { Set-Content -LiteralPath $buildPath -Value $originalBuild }
    }
    & $module {
        $script:PackageCalls = [Collections.Generic.List[string]]::new()
        <# .SYNOPSIS 只接受 package 实际所需的 NSIS, SDK 和前端工具不应被探测. #>
        function Assert-PbiTools([string[]]$Tools) { Assert (($Tools -join ',') -ceq 'makensis') 'package 只能检查 NSIS' }
        <# .SYNOPSIS package 不编译源码, 不得要求 obj 资产存在. #>
        function Assert-PbiRestoreAssets { throw 'package 不得检查编译依赖资产' }
        $packagePath = Join-Path $script:Root 'scripts/package.ps1'
        Set-Content -LiteralPath $packagePath -Value '$script:PackageCalls.Add($PbiInvocation.Options.DotNetMode)'
        Invoke-PbiAction 'package' @{}
        Invoke-PbiAction 'package' @{ DotNetMode = 'WITH-DOTNET' }
        Assert (($script:PackageCalls -join ',') -ceq 'without-dotnet,with-dotnet') '独立 package 应直接分发选中模式, 不运行构建'
    }
    & $module {
        $script:ReleaseCalls = [Collections.Generic.List[string]]::new()
        $script:ReleaseFailure = ''
        $script:ExpectedMode = ''
        <# .SYNOPSIS 验证 release 固定执行 All/Release 且保留显式模式, 按场景模拟门禁失败. #>
        function Invoke-PbiAll([hashtable]$Options) {
            Assert ($Options.Count -eq 3 -and $Options.Scope -ceq 'All' -and $Options.Configuration -ceq 'Release' -and $Options.DotNetMode -ceq $script:ExpectedMode) 'release 必须固定完整 Release 门禁并传递选中模式'
            $script:ReleaseCalls.Add('all')
            if ($script:ReleaseFailure -eq 'all') { Stop-Pbi 47 '模拟 release 门禁失败' }
        }
        <# .SYNOPSIS 只记录安装包动作, 不启动 NSIS, 确保 package 仅收到部署模式. #>
        function Invoke-PbiAction([string]$Name, [hashtable]$Options) {
            Assert ($Name -eq 'package' -and $Options.Count -eq 1 -and $Options.DotNetMode -ceq $script:ExpectedMode) 'release 门禁后只打包同一模式'
            $script:ReleaseCalls.Add('package')
            if ($script:ReleaseFailure -eq 'package') { Stop-Pbi 49 '模拟 NSIS 失败' }
        }
        foreach ($mode in @('with-dotnet', 'without-dotnet')) {
            $script:ExpectedMode = $mode
            foreach ($failure in @('', 'all', 'package')) {
                $script:ReleaseCalls.Clear()
                $script:ReleaseFailure = $failure
                $rejected = $false
                try { Invoke-PbiRelease @{ DotNetMode = $mode } }
                catch {
                    $rejected = $true
                    $expectedCode = if ($failure -eq 'all') { 47 } else { 49 }
                    Assert ($failure -ne '' -and $_.Exception.Data['ExitCode'] -eq $expectedCode) 'release 必须保留首个失败退出码'
                }
                Assert ($rejected -eq ($failure -ne '')) 'release 成功与失败必须符合真实阶段'
                $expectedCalls = if ($failure -eq 'all') { 'all' } else { 'all,package' }
                Assert (($script:ReleaseCalls -join ',') -eq $expectedCalls) 'release 门禁失败不得开始安装包, 正常仅打包一次'
            }
        }
    }
    $saved = (Get-Location).Path
    $previous = $env:DOTNET_CLI_TELEMETRY_OPTOUT
    $code = Invoke-PbiEntry -Arguments @('-Choice', 'unknown')
    Assert ($code -eq 2) '入口参数退出码'
    Assert ((Get-Location).Path -eq $saved) '恢复目录'
    Assert ($env:DOTNET_CLI_TELEMETRY_OPTOUT -eq $previous) '恢复环境'
    & $module {
        # 在动作边界注入可控取消及原生失败, 不发送真实控制台信号或运行构建.
        $script:EntryFailure = 0
        <# .SYNOPSIS 按当前场景抛出取消或原生失败, 保留入口的错误及恢复边界. #>
        function Invoke-PbiAction([string]$Name, [hashtable]$Options) {
            if ($script:EntryFailure) { Stop-Pbi $script:EntryFailure '测试阶段失败' }
            throw [OperationCanceledException]::new('测试取消')
        }
        $savedLocation = (Get-Location).Path
        $savedEnvironment = @{}
        foreach ($key in @('DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'PNPM_DISABLE_SELF_UPDATE_CHECK')) {
            $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        }
        foreach ($failure in @(0, 47)) {
            $script:EntryFailure = $failure
            $expectedCode = if ($failure) { $failure } else { 130 }
            $actualCode = Invoke-PbiEntry -Arguments @('-Choice', 'help')
            Assert ($actualCode -eq $expectedCode) '取消及原生失败保持退出码'
            Assert ((Get-Location).Path -eq $savedLocation) '失败后目录须恢复'
            foreach ($key in $savedEnvironment.Keys) {
                Assert ([Environment]::GetEnvironmentVariable($key, 'Process') -ceq $savedEnvironment[$key]) '失败后环境须恢复'
            }
        }
    }
    try { Invoke-PbiNative $pwsh @('-NoProfile', '-Command', 'exit 47'); throw '未传播原生退出码' }
    catch { Assert ($_.Exception.Data['ExitCode'] -eq 47) '原生退出码原样传播' }
    Write-Output 'PASS 脚本安全与编排契约'
}
finally {
    [Console]::OutputEncoding = $savedOutputEncoding
    # 只移除本测试新建的独立目录, 清理前验证绝对路径和所有残余链接.
    $resolved = [IO.Path]::GetFullPath($sandbox)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'pbi-utils 测试 *' -and (Test-Path -LiteralPath $resolved)) {
        $remaining = Join-Path $resolved 'artifacts/link'
        if (Test-Path -LiteralPath $remaining) { Remove-Item -LiteralPath $remaining -Force }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
