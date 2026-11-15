# FilePath    : pbi-utils\scripts\tests\contracts.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在临时仓库验证脚本注册, 编排, 环境恢复与安全清理契约.

<# .SYNOPSIS 无 Pester 安装依赖的脚本契约测试, 由固定版本 xUnit 执行. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $PSScriptRoot 'TestProjectGraphFixture.ps1')
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
    foreach ($file in @('CommandRegistry.psm1', 'GateWatchdog.psm1', 'TestResults.psm1', 'InstallerMetadata.psm1', 'clean.ps1', 'setup.ps1', 'build.ps1', 'test.ps1', 'format.ps1', 'check.ps1')) { Copy-Item -LiteralPath (Join-Path $sourceRoot "scripts/$file") -Destination (Join-Path $sandbox 'scripts') }
    foreach ($file in @('AGENTS.md', 'pbi-utils.slnx', 'Directory.Build.props')) { Set-Content -LiteralPath (Join-Path $sandbox $file) -Value 'sentinel' }
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'artifacts')
    Set-Content -LiteralPath (Join-Path $sandbox 'artifacts/generated.txt') -Value 'generated'
    Set-Content -LiteralPath (Join-Path $sandbox 'user.txt') -Value 'protected'
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'src/PbiUtils.Core/obj') -Force
    Set-Content -LiteralPath (Join-Path $sandbox 'src/PbiUtils.Core/obj/project.assets.json') -Value '{}'
    Import-Module (Join-Path $sandbox 'scripts/CommandRegistry.psm1') -Force
    $registry = Get-PbiRegistry
    $expectedActions = @(
        'help', 'setupAll', 'setupC#', 'setupVue', 'devDesktop', 'devWeb',
        'formatAll', 'formatC#', 'formatVue', 'checkAll', 'checkC#', 'checkVue',
        'testAll', 'testC#', 'testVue', 'buildAll', 'buildC#', 'buildVue',
        'verifyAndBuild', 'package', 'release', 'clean'
    )
    Assert (($registry.Name -join ',') -ceq ($expectedActions -join ',')) '注册表须按开发到打包的顺序展示动作'
    $releaseDefaults = @{ Choice = 'verifyAndBuild'; Configuration = 'Release' }
    $null = Resolve-PbiAction $releaseDefaults
    Assert ($releaseDefaults.ContainsKey('DotNetMode') -and $releaseDefaults.DotNetMode -ceq 'without-dotnet') 'Release 构建必须默认只选择 without-dotnet 模式'
    $coreProjects = @(Get-PbiProjects 'Core')
    Assert ($coreProjects -contains 'src/PbiUtils.Host' -and $coreProjects -contains 'src/PbiUtils.Windows') 'Core 必须包含无 UI 业务后台及 Windows 依赖'
    Assert ($coreProjects -notcontains 'src/PbiUtils.Desktop') 'Core 不得包含桌面 UI'
    Assert (@(Get-PbiFormatTargets 'Core') -contains 'src/PbiUtils.Host/PbiUtils.Host.csproj') 'Core 格式检查必须覆盖业务后台'
    $expectedIds = 0..($expectedActions.Count - 1)
    Assert (($registry.Id -join ',') -eq ($expectedIds -join ',')) '注册编号须随动作顺序从 0 连续递增'
    foreach ($entry in $registry) {
        if ($entry.Name.EndsWith('C#')) { Assert (($entry.Requires -join ',') -eq 'dotnet') 'C# 不依赖 Node/pnpm' }
        if ($entry.Name.EndsWith('Vue')) { Assert (($entry.Requires -join ',') -eq 'node,pnpm') 'Vue 不依赖 dotnet' }
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
    foreach ($action in @('verifyAndBuild', 'buildAll', 'buildC#')) {
        foreach ($configuration in @('Debug', 'Release')) {
            $defaults = @{ Choice = $action; Configuration = $configuration }
            $null = Resolve-PbiAction $defaults
            $expected = if ($configuration -eq 'Debug') { 'with-dotnet' } else { 'without-dotnet' }
            Assert ($defaults.DotNetMode -ceq $expected) '构建模式默认值必须按配置确定'
        }
    }
    foreach ($action in @('verifyAndBuild', 'buildAll', 'buildC#', 'package', 'release')) {
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
        $entryScript = Join-Path $sandbox 'run.ps1'
        $helpLines = @(& $pwsh -NoProfile -File $entryScript -List)
        Assert ($LASTEXITCODE -eq 0) '缺工具帮助'
        $menuEntries = @($helpLines | ForEach-Object {
            if ($_ -match '^\s*(\d+)\s+(\S+)\s') { @{ Id = $Matches[1]; Name = $Matches[2] } }
        })
        Assert (($menuEntries.Name -join ',') -ceq ($expectedActions -join ',')) '帮助中的动作须匹配注册顺序'
        Assert (($menuEntries.Id -join ',') -eq ($expectedIds -join ',')) '帮助中的编号须从 0 连续递增'
        foreach ($entry in $menuEntries) {
            $numeric = Resolve-PbiAction @{ Choice = $entry.Id }
            $named = Resolve-PbiAction @{ Choice = $entry.Name.ToUpperInvariant() }
            Assert ($numeric.Name -ceq $entry.Name -and $numeric.Id -eq $named.Id) '显示的编号和名称须解析到同一个动作'
        }
        Assert ((Invoke-PbiEntry -Arguments @('-Choice', 'all')) -eq 2) '旧动作 all 返回参数错误'
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
        foreach ($action in @('verifyAndBuild', 'buildC#', 'setupC#')) {
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
        . (Join-Path $PSScriptRoot 'InlineGateLanes.ps1')
        $script:Calls = [Collections.Generic.List[string]]::new()
        $script:FailureCodes = @{}
        $script:CancelledAction = ''
        $script:BuildReturned = $false
        <# .SYNOPSIS 固定前端输入指纹, 编排测试不调用 Node. #>
        function Get-PbiFrontendInputFingerprint { return ('b' * 64) }
        # 替换原生动作边界, 通道按启动顺序同步执行; 编译后与交付前回调模拟前端构建和等待全部通道.
        <# .SYNOPSIS 记录动作与内部回调, 校验选项隔离并在指定阶段抛出原生退出码或取消. #>
        function Invoke-PbiAction([string]$Name, [hashtable]$Options, [scriptblock]$AfterCompile, [scriptblock]$BeforeDelivery) {
            $script:Calls.Add($Name)
            if ($Name.EndsWith('Vue') -and $Options.Count) { throw 'Vue 收到 C# 参数' }
            if ($Name.EndsWith('C#') -and $Options.Scope -ne 'Core') { throw 'C# Scope 丢失' }
            if ($Name -eq 'checkC#' -and $Options.ContainsKey('Configuration')) { throw 'check 接收了 Configuration' }
            if ($Name -in @('buildC#', 'testC#') -and $Options.Configuration -ne 'Release') { throw 'Configuration 丢失' }
            if ($Name -eq 'buildC#' -and $Options.DotNetMode -cne 'with-dotnet') { throw 'verifyAndBuild 丢失显式部署模式' }
            if ($Name -ne 'buildC#' -and $Options.ContainsKey('DotNetMode')) { throw '非构建动作收到部署模式' }
            if ($Options.ContainsKey('AfterCompile') -or $Options.ContainsKey('BeforeDelivery')) { throw '内部回调混入 CLI 选项' }
            if ($Name -eq 'buildC#' -and (!$AfterCompile -or !$BeforeDelivery)) { throw 'verifyAndBuild 缺少编译后或交付前回调' }
            if ($Name -ne 'buildC#' -and ($AfterCompile -or $BeforeDelivery)) { throw '回调传给非构建动作' }
            if ($Name -eq $script:CancelledAction) { throw [OperationCanceledException]::new('模拟阶段取消') }
            if ($script:FailureCodes.ContainsKey($Name)) {
                # 与真实动作边界一致, 失败记录首个失败动作和原退出码.
                $failure = [InvalidOperationException]::new('模拟阶段失败')
                $failure.Data['ExitCode'] = $script:FailureCodes[$Name]
                $failure.Data['Action'] = $Name
                throw $failure
            }
            if ($AfterCompile) { & $AfterCompile; & $BeforeDelivery 'staging'; $script:BuildReturned = $true }
        }
        $options = @{ Scope = 'Core'; Configuration = 'Release'; DotNetMode = 'with-dotnet' }
        $expected = @('checkC#', 'testC#', 'checkVue', 'testVue', 'buildC#', 'buildVue')
        Invoke-PbiVerifyAndBuild $options
        Assert (($script:Calls -join ',') -eq ($expected -join ',')) 'verifyAndBuild 先启动四个检查测试通道, 再编译 C# 并在 Vue 通道后构建前端'
        Assert $script:BuildReturned '全部通道和前端完成后构建动作才交付'
        Assert ($options.Count -eq 3) 'verifyAndBuild 不改变调用者选项'
        for ($index = 0; $index -lt $expected.Count; $index++) {
            $script:Calls.Clear()
            $script:BuildReturned = $false
            $script:FailureCodes = @{ $expected[$index] = 41 + $index }
            try { Invoke-PbiVerifyAndBuild $options; throw 'verifyAndBuild 阶段失败未停止' }
            catch { if ($_.Exception.Data['ExitCode'] -ne 41 + $index -or $_.Exception.Data['Action'] -cne $expected[$index]) { throw } }
            # 同步替身的通道启动即结束: 失败一经记录, 后续通道、C# 编译与前端构建均不再开始, 也不交付.
            Assert (($script:Calls -join ',') -eq ($expected[0..$index] -join ',')) 'verifyAndBuild 任一阶段失败后不得开始任何后续阶段'
            Assert (!$script:BuildReturned) '失败阶段不得完成构建交付'
        }
        # 首个失败一经记录即停止: 同样配置为失败的后续阶段不会启动, 报告最先记录的失败.
        foreach ($case in @(
            @{ Failures = @{ 'testVue' = 61; 'checkC#' = 62 }; Code = 62; Action = 'checkC#'; Calls = 'checkC#' },
            @{ Failures = @{ 'checkVue' = 63; 'buildC#' = 64 }; Code = 63; Action = 'checkVue'; Calls = 'checkC#,testC#,checkVue' },
            @{ Failures = @{ 'testVue' = 65; 'testC#' = 66; 'checkVue' = 67 }; Code = 66; Action = 'testC#'; Calls = 'checkC#,testC#' }
        )) {
            $script:Calls.Clear()
            $script:BuildReturned = $false
            $script:FailureCodes = $case.Failures
            try { Invoke-PbiVerifyAndBuild $options; throw '多阶段失败未停止' }
            catch { Assert ($_.Exception.Data['ExitCode'] -eq $case.Code -and $_.Exception.Data['Action'] -ceq $case.Action) '必须报告最先记录的失败及其退出码' }
            Assert (($script:Calls -join ',') -ceq $case.Calls) '首个失败之后不得启动其他阶段'
            Assert (!$script:BuildReturned) '多阶段失败不得交付'
        }
        $script:FailureCodes = @{ 'testC#' = 68 }
        $script:CancelledAction = 'checkC#'
        $script:Calls.Clear()
        try { Invoke-PbiVerifyAndBuild $options; throw '取消未停止' }
        catch { Assert ($_.Exception -is [OperationCanceledException]) '通道取消时本轮按取消结束, 不以后续阶段的失败替代' }
        Assert (($script:Calls -join ',') -eq 'checkC#') '取消后不得启动新的通道、C# 编译或前端构建'
        $script:CancelledAction = ''
        $script:FailureCodes = @{}
        # 并发阶段在快速失败终止前各自失败时仍按规范顺序报告; 被终止的通道与主流程在途命令不是失败候选, 取消优先.
        <# .SYNOPSIS 构造已结束的通道记录. #>
        function New-TestGateLane([string]$Name, [int]$Code, [bool]$Terminated = $false) {
            [pscustomobject]@{ Name = $Name; Process = $null; Result = ''; Completed = $true; Terminated = $Terminated; ExitCode = $Code; Action = $Name; Message = "模拟 $Name"; Elapsed = [TimeSpan]::Zero }
        }
        <# .SYNOPSIS 构造主流程错误记录, 可附加检查点或已终止标记. #>
        function New-TestGateFailure([string]$Action, [int]$Code, [string]$Marker = '') {
            $exception = [InvalidOperationException]::new("模拟 $Action")
            $exception.Data['ExitCode'] = $Code
            $exception.Data['Action'] = $Action
            if ($Marker) { $exception.Data[$Marker] = $true }
            return [Management.Automation.ErrorRecord]::new($exception, 'GateTest', [Management.Automation.ErrorCategory]::NotSpecified, $null)
        }
        foreach ($case in @(
            @{ Lanes = @(@('checkVue', 63, $false), @('testC#', -1, $true)); Main = @('buildC#', 64, ''); Code = 64; Action = 'buildC#' },
            @{ Lanes = @(@('checkC#', -1, $true), @('testC#', -1, $true), @('checkVue', 63, $false), @('testVue', -1, $true)); Main = @('buildC#', 4, 'PbiGateTerminated'); Code = 63; Action = 'checkVue' },
            @{ Lanes = @(@('checkC#', -1, $true), @('testVue', 65, $false)); Main = @('testVue', 65, 'PbiGateCheckpoint'); Code = 65; Action = 'testVue' },
            @{ Lanes = @(@('checkC#', 68, $false), @('testC#', 130, $false)); Main = $null; Code = 130; Action = 'testC#' }
        )) {
            $gate = [pscustomobject]@{ Lanes = [Collections.Generic.List[object]]::new() }
            foreach ($lane in $case.Lanes) { $gate.Lanes.Add((New-TestGateLane $lane[0] $lane[1] $lane[2])) }
            $main = if ($null -ne $case.Main) { New-TestGateFailure $case.Main[0] $case.Main[1] $case.Main[2] } else { $null }
            $resolved = Resolve-PbiGateFailure $gate $main
            $exception = if ($resolved -is [Management.Automation.ErrorRecord]) { $resolved.Exception } else { $resolved }
            Assert ((Get-PbiExitCode $exception) -eq $case.Code -and [string]$exception.Data['Action'] -ceq $case.Action) '汇总必须排除被终止阶段并按规范顺序选择首个真实失败, 取消优先'
        }
        # 检查点与清理方式同样取消优先: 已结束通道中有取消时按取消抛出, 门禁随后排空而不是终止通道.
        $mixedGate = [pscustomobject]@{ Lanes = [Collections.Generic.List[object]]::new(); State = [hashtable]::Synchronized(@{ Trigger = $null }); Watchdog = $null }
        $mixedGate.Lanes.Add((New-TestGateLane 'checkC#' 71))
        $mixedGate.Lanes.Add((New-TestGateLane 'testC#' 130))
        try { Assert-PbiGateRunning $mixedGate; throw '检查点未停止' }
        catch { Assert ($_.Exception -is [OperationCanceledException] -and $_.Exception.Data.Contains('PbiGateCheckpoint')) '检查点在已有取消时必须按取消抛出' }
        Assert ((Get-PbiGateStopReason $mixedGate) -ceq 'cancelled') '清理方式在已有取消时必须选择排空'
        # 主流程在门禁中且已有失败时, 原生命令在启动前被拒绝.
        $failedGate = New-PbiVerificationGate $options
        $sentinel = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-native-' + [Guid]::NewGuid().ToString('N'))
        try {
            $failedGate.Lanes.Add((New-TestGateLane 'checkVue' 69))
            $script:ActiveGate = $failedGate
            try { Invoke-PbiNative 'pwsh' @('-NoProfile', '-Command', "New-Item -ItemType File -Path '$sentinel' | Out-Null"); throw '门禁失败后仍启动了原生命令' }
            catch { Assert ($_.Exception.Data.Contains('PbiGateCheckpoint') -and $_.Exception.Data['ExitCode'] -eq 69) '门禁已失败时原生命令必须以检查点异常拒绝启动' }
            Assert (!(Test-Path -LiteralPath $sentinel)) '门禁已失败时不得启动原生命令'
        }
        finally {
            $script:ActiveGate = $null
            Stop-PbiVerificationGate $failedGate $true
            if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
        }
        $pwshPath = (Get-Process -Id $PID).Path
        <# .SYNOPSIS 构造经孙进程长时等待的原生命令参数; 孙进程启动后把自身 PID 写入登记文件. #>
        function New-TestSleeperArguments([string]$Record) {
            $inner = "[IO.File]::WriteAllText('$Record', [string]`$PID); Start-Sleep -Seconds 60"
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
            return @('-NoProfile', '-NonInteractive', '-Command', "& '$pwshPath' -NoProfile -NonInteractive -EncodedCommand $encoded")
        }
        <# .SYNOPSIS 登记的孙进程在 3 秒内退出时返回 $true. #>
        function Test-TestSleeperGone([string]$Record) {
            $sleeper = [int][IO.File]::ReadAllText($Record)
            $deadline = [DateTime]::UtcNow.AddSeconds(3)
            do {
                $process = Get-Process -Id $sleeper -ErrorAction SilentlyContinue
                if ($null -eq $process -or $process.HasExited) { return $true }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            return $false
        }
        # 门禁中的可执行文件持有句柄运行: 两路输出按来源逐行转发, 非零退出码原样保留.
        $nativeGate = New-PbiVerificationGate $options
        $savedOut = [Console]::Out
        $savedError = [Console]::Error
        $capturedOut = [IO.StringWriter]::new()
        $capturedError = [IO.StringWriter]::new()
        $nativeFailure = $null
        try {
            $script:ActiveGate = $nativeGate
            [Console]::SetOut($capturedOut)
            [Console]::SetError($capturedError)
            try { Invoke-PbiNative $pwshPath @('-NoProfile', '-NonInteractive', '-Command', "[Console]::Out.WriteLine('OUT-1'); [Console]::Error.WriteLine('ERR-1'); exit 7"); throw '非零退出码未失败' }
            catch { $nativeFailure = $_ }
        }
        finally {
            [Console]::SetOut($savedOut)
            [Console]::SetError($savedError)
            $script:ActiveGate = $null
            Stop-PbiVerificationGate $nativeGate $false
        }
        Assert ($nativeFailure.Exception.Data['ExitCode'] -eq 7 -and !$nativeFailure.Exception.Data.Contains('PbiGateTerminated')) '门禁中的原生命令必须保留真实退出码'
        Assert ($capturedOut.ToString().Contains('OUT-1') -and !$capturedOut.ToString().Contains('ERR-1') -and $capturedError.ToString().Contains('ERR-1')) '门禁中的原生命令必须按来源转发 stdout 与 stderr'
        # 其他阶段在原生命令运行期间失败: 主流程只终止本命令自有的整棵进程树并按被终止报告, 不等待其完成.
        Import-Module (Join-Path $script:Root 'scripts/GateWatchdog.psm1') -Force
        <#
        .SYNOPSIS 在门禁中运行原生命令; 子进程写出登记后再等待指定毫秒, 由后台线程对门禁状态执行触发动作.
        .OUTPUTS 含 Failure (错误记录或 null) 与 Seconds (调用耗时) 的结果.
        #>
        function Invoke-TestGateNative([pscustomobject]$Gate, [string]$File, [string[]]$Arguments, [string]$Record, [string]$Trigger, [int]$DelayMilliseconds = 0) {
            $job = Start-ThreadJob -ArgumentList $Gate.State, $Record, $Trigger, $DelayMilliseconds -ScriptBlock {
                param($State, $Record, $Action, $Delay)
                $deadline = [DateTime]::UtcNow.AddSeconds(30)
                while (![IO.File]::Exists($Record) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
                Start-Sleep -Milliseconds $Delay
                if ($Action) { & ([scriptblock]::Create($Action)) $State }
            }
            $failure = $null
            $watch = [Diagnostics.Stopwatch]::StartNew()
            try {
                $script:ActiveGate = $Gate
                try { Invoke-PbiNative $File $Arguments } catch { $failure = $_ }
            }
            finally {
                $script:ActiveGate = $null
                $null = Wait-Job $job -Timeout 30
                Remove-Job $job -Force
            }
            return [pscustomobject]@{ Failure = $failure; Seconds = $watch.Elapsed.TotalSeconds }
        }
        $failTrigger = 'param($State) $State.Trigger = ''testVue'''
        <# .SYNOPSIS 编码一段 PowerShell 脚本供 -EncodedCommand 使用. #>
        function ConvertTo-TestEncoded([string]$Script) { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script)) }
        $scenarioFiles = [Collections.Generic.List[string]]::new()
        try {
            $abortRecord = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-sleeper-' + [Guid]::NewGuid().ToString('N'))
            $scenarioFiles.Add($abortRecord)
            $abortGate = New-PbiVerificationGate $options
            try { $abort = Invoke-TestGateNative $abortGate $pwshPath (New-TestSleeperArguments $abortRecord) $abortRecord $failTrigger }
            finally { Stop-PbiVerificationGate $abortGate $true }
            Assert ($null -ne $abort.Failure -and $abort.Failure.Exception.Data.Contains('PbiGateTerminated') -and $abort.Seconds -lt 30) '门禁失败后在途原生命令必须立即终止并标记为已终止'
            Assert (Test-TestSleeperGone $abortRecord) '终止原生命令必须结束其整棵进程树'

            # 指向可执行文件的别名按最终目标持有根进程句柄运行, 其他阶段失败时同样立即结束.
            $aliasRecord = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-alias-' + [Guid]::NewGuid().ToString('N'))
            $scenarioFiles.Add($aliasRecord)
            $aliasName = 'pbi-gate-alias-' + [Guid]::NewGuid().ToString('N')
            Set-Alias -Name $aliasName -Value $pwshPath
            $aliasGate = New-PbiVerificationGate $options
            try { $aliasRun = Invoke-TestGateNative $aliasGate $aliasName (New-TestSleeperArguments $aliasRecord) $aliasRecord $failTrigger }
            finally { Stop-PbiVerificationGate $aliasGate $true }
            Assert ($null -ne $aliasRun.Failure -and $aliasRun.Failure.Exception.Data.Contains('PbiGateTerminated') -and $aliasRun.Seconds -lt 30) '别名指向的原生命令必须在门禁失败后立即结束'
            Assert (Test-TestSleeperGone $aliasRecord) '别名指向的原生命令进程树必须结束'

            # 关闭输出后仍运行的命令: 读到输出结束后继续检查门禁, 其他阶段失败时仍能结束.
            $closedRecord = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-closed-' + [Guid]::NewGuid().ToString('N'))
            $scenarioFiles.Add($closedRecord)
            $closing = "Add-Type -Namespace PbiGateTest -Name Std -MemberDefinition '[DllImport(""kernel32.dll"")] public static extern System.IntPtr GetStdHandle(int n); [DllImport(""kernel32.dll"")] public static extern bool CloseHandle(System.IntPtr h);'; `$null = [PbiGateTest.Std]::CloseHandle([PbiGateTest.Std]::GetStdHandle(-11)); `$null = [PbiGateTest.Std]::CloseHandle([PbiGateTest.Std]::GetStdHandle(-12)); [IO.File]::WriteAllText('$closedRecord', [string]`$PID); Start-Sleep -Seconds 60"
            $closedGate = New-PbiVerificationGate $options
            try { $closed = Invoke-TestGateNative $closedGate $pwshPath @('-NoProfile', '-NonInteractive', '-EncodedCommand', (ConvertTo-TestEncoded $closing)) $closedRecord $failTrigger }
            finally { Stop-PbiVerificationGate $closedGate $true }
            Assert ($null -ne $closed.Failure -and $closed.Failure.Exception.Data.Contains('PbiGateTerminated') -and $closed.Seconds -lt 30) '关闭输出后仍运行的命令必须在门禁失败后被结束'
            Assert (Test-TestSleeperGone $closedRecord) '关闭输出的命令进程必须被结束'

            # 批处理入口同样持有根进程句柄 (cmd.exe), 其他阶段失败时结束整棵进程树.
            $shimRecord = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-shim-' + [Guid]::NewGuid().ToString('N'))
            $shim = $shimRecord + '.cmd'
            $scenarioFiles.Add($shimRecord)
            $scenarioFiles.Add($shim)
            $shimSleeper = ConvertTo-TestEncoded "[IO.File]::WriteAllText('$shimRecord', [string]`$PID); Start-Sleep -Seconds 60"
            [IO.File]::WriteAllText($shim, "@echo off`r`n`"$pwshPath`" -NoProfile -NonInteractive -EncodedCommand $shimSleeper`r`nexit /b %errorlevel%`r`n", [Text.Encoding]::ASCII)
            $shimGate = New-PbiVerificationGate $options
            try { $shimRun = Invoke-TestGateNative $shimGate $shim @('--dir', 'web', 'build') $shimRecord $failTrigger }
            finally { Stop-PbiVerificationGate $shimGate $true }
            Assert ($null -ne $shimRun.Failure -and $shimRun.Failure.Exception.Data.Contains('PbiGateTerminated') -and $shimRun.Seconds -lt 30) '批处理入口必须在门禁失败后立即结束'
            Assert (Test-TestSleeperGone $shimRecord) '批处理入口启动的子进程必须随进程树结束'

            # 通道以 130 结束时按取消结束本命令并抛出取消, 门禁随后排空而不是终止通道.
            $cancelRecord = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-cancel-' + [Guid]::NewGuid().ToString('N'))
            $cancelGate = New-PbiVerificationGate $options
            $cancelResult = Join-Path $cancelGate.Directory 'cancel.json'
            $scenarioFiles.Add($cancelRecord)
            $laneScript = @"
while (![IO.File]::Exists('$cancelRecord')) { Start-Sleep -Milliseconds 50 }
[IO.File]::WriteAllText('$cancelResult', '{"code":130,"action":"testVue","message":"cancelled"}')
exit 130
"@
            $laneStart = [Diagnostics.ProcessStartInfo]::new($pwshPath)
            foreach ($argument in @('-NoProfile', '-NonInteractive', '-EncodedCommand', (ConvertTo-TestEncoded $laneScript))) { $laneStart.ArgumentList.Add($argument) }
            $laneStart.UseShellExecute = $false
            $laneStart.CreateNoWindow = $true
            $laneProcess = [Diagnostics.Process]::Start($laneStart)
            $cancelGate.Lanes.Add([pscustomobject]@{ Name = 'testVue'; Process = $laneProcess; Result = $cancelResult; Completed = $false; Terminated = $false; ExitCode = $null; Action = 'testVue'; Message = ''; Elapsed = [TimeSpan]::Zero })
            try { $cancel = Invoke-TestGateNative $cancelGate $pwshPath (New-TestSleeperArguments $cancelRecord) $cancelRecord '' }
            finally {
                if (!$laneProcess.WaitForExit(30000)) { $laneProcess.Kill($true) }
                Stop-PbiVerificationGate $cancelGate $false
            }
            Assert ($null -ne $cancel.Failure -and $cancel.Failure.Exception -is [OperationCanceledException] -and (Get-PbiExitCode $cancel.Failure.Exception) -eq 130) '通道取消时在途原生命令必须按取消结束, 门禁随后排空'
            Assert (Test-TestSleeperGone $cancelRecord) '通道取消时原生命令进程树必须结束'

            # 根进程已自行以 -1 退出、后代仍持有输出时才出现的门禁失败: 只清理后代, 保留真实退出码而不判定为被终止.
            $naturalRecord = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-natural-' + [Guid]::NewGuid().ToString('N'))
            $scenarioFiles.Add($naturalRecord)
            $naturalSleeper = ConvertTo-TestEncoded "[IO.File]::WriteAllText('$naturalRecord', [string]`$PID); Start-Sleep -Seconds 60"
            $naturalRoot = "Start-Process -FilePath '$pwshPath' -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', '$naturalSleeper' -NoNewWindow; exit -1"
            $naturalGate = New-PbiVerificationGate $options
            try { $natural = Invoke-TestGateNative $naturalGate $pwshPath @('-NoProfile', '-NonInteractive', '-EncodedCommand', (ConvertTo-TestEncoded $naturalRoot)) $naturalRecord $failTrigger 2000 }
            finally { Stop-PbiVerificationGate $naturalGate $true }
            Assert ($null -ne $natural.Failure -and !$natural.Failure.Exception.Data.Contains('PbiGateTerminated') -and (Get-PbiExitCode $natural.Failure.Exception) -eq -1 -and $natural.Seconds -lt 30) '终止前已自行结束的命令必须保留真实退出码'
            Assert (Test-TestSleeperGone $naturalRecord) '已退出根进程下仍持有输出的后代必须被结束'
        }
        finally { foreach ($path in $scenarioFiles) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } } }
        # 管道被停止 (Ctrl+C) 时与 PowerShell 原生命令一致, 门禁中的原生命令进程树随之结束.
        $stopRecord = Join-Path ([IO.Path]::GetTempPath()) ('pbi-gate-sleeper-' + [Guid]::NewGuid().ToString('N'))
        $stopShared = [hashtable]::Synchronized(@{})
        $stopRunspace = [runspacefactory]::CreateRunspace()
        $stopRunspace.Open()
        $stopShell = [powershell]::Create()
        $stopShell.Runspace = $stopRunspace
        try {
            $null = $stopShell.AddScript({
                    param($ModulePath, $Pwsh, $Arguments, $Shared)
                    Import-Module $ModulePath -Force
                    & (Get-Module CommandRegistry) {
                        param($Pwsh, $Arguments, $Shared)
                        $script:ActiveGate = New-PbiVerificationGate @{ Scope = 'Core'; Configuration = 'Release' }
                        $Shared.Directory = $script:ActiveGate.Directory
                        Invoke-PbiNative $Pwsh $Arguments
                    } $Pwsh $Arguments $Shared
                }).AddArgument((Get-Module CommandRegistry).Path).AddArgument($pwshPath).AddArgument((New-TestSleeperArguments $stopRecord)).AddArgument($stopShared)
            $stopHandle = $stopShell.BeginInvoke()
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (![IO.File]::Exists($stopRecord) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
            Assert ([IO.File]::Exists($stopRecord)) '停止测试的原生命令必须已启动'
            $stopShell.Stop()
            Assert ($stopHandle.IsCompleted) '停止管道必须结束原生命令调用'
            Assert (Test-TestSleeperGone $stopRecord) '管道停止时门禁中的原生命令进程树必须结束'
        }
        finally {
            $stopShell.Dispose()
            $stopRunspace.Dispose()
            if ($stopShared.ContainsKey('Directory') -and [IO.Directory]::Exists($stopShared.Directory)) { [IO.Directory]::Delete($stopShared.Directory, $true) }
            if (Test-Path -LiteralPath $stopRecord) { Remove-Item -LiteralPath $stopRecord }
        }
        # 逻辑处理器不足时通道逐个执行: C# 检查与测试先于编译, Vue 检查与测试在编译后依次运行, 失败后不再启动后续阶段.
        <# .SYNOPSIS 模拟逻辑处理器不足的本机. #>
        function Test-PbiParallelGate { return $false }
        $sequential = @('checkC#', 'testC#', 'buildC#', 'checkVue', 'testVue', 'buildVue')
        $script:Calls.Clear()
        $script:BuildReturned = $false
        Invoke-PbiVerifyAndBuild $options
        Assert (($script:Calls -join ',') -eq ($sequential -join ',')) '逐个执行时按 checkC#、testC#、C# 编译、checkVue、testVue 顺序运行, 再构建前端'
        Assert $script:BuildReturned '逐个执行全部通过后构建动作才交付'
        for ($index = 0; $index -lt $sequential.Count; $index++) {
            $script:Calls.Clear()
            $script:BuildReturned = $false
            $script:FailureCodes = @{ $sequential[$index] = 71 + $index }
            try { Invoke-PbiVerifyAndBuild $options; throw '逐个执行的阶段失败未停止' }
            catch { if ($_.Exception.Data['ExitCode'] -ne 71 + $index -or $_.Exception.Data['Action'] -cne $sequential[$index]) { throw } }
            Assert (($script:Calls -join ',') -eq ($sequential[0..$index] -join ',')) '逐个执行时失败阶段之后不得启动任何阶段'
            Assert (!$script:BuildReturned) '逐个执行的失败阶段不得完成构建交付'
        }
        $script:FailureCodes = @{}
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
        foreach ($action in @('verifyAndBuild', 'buildC#', 'testC#', 'checkC#', 'formatC#')) {
            try { Invoke-PbiAction $action @{ Scope = 'Core' }; throw '缺失依赖资产应失败' }
            catch {
                if ($_.Exception.Data['ExitCode'] -ne 3 -or $_.Exception.Message -notlike "*setupC#*Scope Core*") { throw }
            }
        }
        <# .SYNOPSIS 记录 release 门禁是否被启动; 前置失败时不得启动通道、预取或编译. #>
        function Invoke-PbiRelease([hashtable]$Options) { $script:NativeCalls.Add('release-gate') }
        try { Invoke-PbiAction 'release' @{}; throw 'release 缺失依赖资产应失败' }
        catch {
            if ($_.Exception.Data['ExitCode'] -ne 3 -or $_.Exception.Message -notlike "*setupC#*Scope All*") { throw }
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
            if ($Arguments[0] -eq 'format') { Assert ($Arguments -contains '--no-restore') 'C# 日常检查不得隐式还原' }
            if ($Arguments[0] -eq 'format') {
                $severity = [Array]::IndexOf($Arguments, '--severity')
                Assert ($severity -ge 0 -and $Arguments[$severity + 1] -eq 'info') 'C# 必须检查建议级别'
                if ('--verify-no-changes' -in $Arguments) {
                    $report = [Array]::IndexOf($Arguments, '--report')
                    Assert ($report -ge 0) '只读 lint 必须有诊断报告'
                    Set-Content -LiteralPath $Arguments[$report + 1] -Value '[]'
                }
            }
            if ($Arguments[0] -eq 'msbuild') {
                $graph = Get-TestProjectGraphFixture $File $Arguments
                Assert (($graph.Projects -join ',') -ceq 'PbiUtils.UnitTests,PbiUtils.Cli.Tests') 'Core 图只传入已注册的两套测试'
                $now = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
                # 合成一项本轮通过, 让真实 TRX 门禁继续验证语言隔离及参数传播.
                $xml = @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times start="$now" finish="$now" />
  <Results><UnitTestResult outcome="Passed" /></Results>
  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" /></ResultSummary>
</TestRun>
"@
                Write-TestProjectGraphFixtureReports $graph $xml
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
Assert (!$PbiInvocation.Options.ContainsKey('AfterCompile') -and !$PbiInvocation.Options.ContainsKey('BeforeDelivery')) '内部回调不得混入动作选项'
Assert ($PbiInvocation.Action -eq 'buildC#') '内部 handler 仍使用注册动作'
Assert ([object]::ReferenceEquals($PbiInvocation.AfterCompile, $script:ExpectedAfterCompile)) '必须传递原始回调实例'
Assert ([object]::ReferenceEquals($PbiInvocation.BeforeDelivery, $script:ExpectedBeforeDelivery)) '必须传递原始交付前回调实例'
$script:Calls.Add('compile')
if ($PbiInvocation.AfterCompile) { & $PbiInvocation.AfterCompile }
if ($PbiInvocation.BeforeDelivery) { & $PbiInvocation.BeforeDelivery 'staging' }
$script:Calls.Add('deliver')
'@
        try {
            Set-Content -LiteralPath $buildPath -Value $handler
            $script:ExpectedAfterCompile = { $script:Calls.Add('frontend') }
            $script:ExpectedBeforeDelivery = $null
            Invoke-PbiAction 'buildC#' @{ Scope = 'Core'; Configuration = 'Release' } -AfterCompile $script:ExpectedAfterCompile
            Assert (($script:Calls -join ',') -eq 'compile,frontend,deliver') '回调须在编译与交付之间执行'
            $script:Calls.Clear()
            $script:ExpectedBeforeDelivery = { param([string]$Staging) $script:Calls.Add("wait:$Staging") }
            Invoke-PbiAction 'buildC#' @{ Scope = 'Core'; Configuration = 'Release' } -AfterCompile $script:ExpectedAfterCompile -BeforeDelivery $script:ExpectedBeforeDelivery
            Assert (($script:Calls -join ',') -eq 'compile,frontend,wait:staging,deliver') '交付前回调须在前端之后、交付之前执行并收到 staging'
            $script:Calls.Clear()
            $script:ExpectedBeforeDelivery = { param([string]$Staging) $script:Calls.Add('wait'); Stop-Pbi 53 '模拟并行通道失败' }
            try {
                Invoke-PbiAction 'buildC#' @{ Scope = 'Core'; Configuration = 'Release' } -AfterCompile $script:ExpectedAfterCompile -BeforeDelivery $script:ExpectedBeforeDelivery
                throw '交付前回调失败未停止'
            }
            catch { if ($_.Exception.Data['ExitCode'] -ne 53) { throw } }
            Assert (($script:Calls -join ',') -eq 'compile,frontend,wait') '交付前回调失败不得继续交付'
            $script:Calls.Clear()
            $script:ExpectedBeforeDelivery = $null
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
        <# .SYNOPSIS 替换官方元数据预取, 记录启动并返回可识别的上下文, 不访问网络. #>
        function Start-PbiReleaseMetadataPrefetch([string]$RepositoryRoot) { $script:ReleaseCalls.Add('prefetch'); return 'prefetch-context' }
        <# .SYNOPSIS 记录预取清理, 必须收到同一上下文. #>
        function Stop-PbiReleaseMetadataPrefetch($Prefetch) {
            Assert ($Prefetch -ceq 'prefetch-context') 'release 必须清理本轮启动的元数据预取'
            $script:ReleaseCalls.Add('prefetch-stop')
        }
        <# .SYNOPSIS 验证 release 固定执行 All/Release、关闭开发登记并在交付前回调中预先生成安装包, 按场景模拟门禁失败. #>
        function Invoke-PbiVerifyAndBuild([hashtable]$Options, [bool]$RegisterExternalTool = $true, [scriptblock]$BeforeDelivery = $null) {
            Assert ($Options.Count -eq 3 -and $Options.Scope -ceq 'All' -and $Options.Configuration -ceq 'Release' -and $Options.DotNetMode -ceq $script:ExpectedMode) 'release 必须固定完整 Release 门禁并传递选中模式'
            Assert (!$RegisterExternalTool -and $null -ne $BeforeDelivery) 'release 不登记开发入口, 必须在交付前预先生成安装包'
            $script:ReleaseCalls.Add('verifyAndBuild')
            if ($script:ReleaseFailure -eq 'beforeStaging') { Stop-Pbi 47 '模拟 release 门禁失败' }
            & $BeforeDelivery 'release-staging'
            if ($script:ReleaseFailure -eq 'lanes') { Stop-Pbi 48 '模拟并行测试失败' }
            $script:ReleaseCalls.Add('deliver-build')
        }
        <# .SYNOPSIS 记录安装包预先生成、交付或丢弃, 不启动 NSIS; 按场景模拟生成或交付失败. #>
        function Invoke-PbiInstallerStep([hashtable]$Options, [hashtable]$Context) {
            Assert ($Options.Count -eq 1 -and $Options.DotNetMode -ceq $script:ExpectedMode) 'release 只打包同一模式'
            if ($Context.ContainsKey('Discard') -and $Context.Discard) {
                $script:ReleaseCalls.Add('package-discard')
                $Context.Pending.Closed = $true
                return
            }
            if ($null -eq $Context.Pending) {
                Assert ($Context.DeferDelivery -and $Context.Source -ceq 'release-staging' -and $Context.MetadataPrefetch -ceq 'prefetch-context') '安装包必须从本轮 staging 预先生成并使用预取元数据'
                $script:ReleaseCalls.Add('package-prepare')
                if ($script:ReleaseFailure -eq 'prepare') { Stop-Pbi 49 '模拟 NSIS 失败' }
                $Context.Pending = @{ Closed = $false }
                return
            }
            Assert (!$Context.DeferDelivery) '发布目录交付后才交付安装包'
            $script:ReleaseCalls.Add('package-deliver')
            $Context.Pending.Closed = $true
            if ($script:ReleaseFailure -eq 'deliver') { Stop-Pbi 50 '模拟安装包交付失败' }
        }
        foreach ($mode in @('with-dotnet', 'without-dotnet')) {
            $script:ExpectedMode = $mode
            foreach ($case in @(
                @{ Failure = ''; Code = 0; Calls = 'prefetch,verifyAndBuild,package-prepare,deliver-build,package-deliver,prefetch-stop' },
                @{ Failure = 'beforeStaging'; Code = 47; Calls = 'prefetch,verifyAndBuild,prefetch-stop' },
                @{ Failure = 'lanes'; Code = 48; Calls = 'prefetch,verifyAndBuild,package-prepare,package-discard,prefetch-stop' },
                @{ Failure = 'prepare'; Code = 49; Calls = 'prefetch,verifyAndBuild,package-prepare,prefetch-stop' },
                @{ Failure = 'deliver'; Code = 50; Calls = 'prefetch,verifyAndBuild,package-prepare,deliver-build,package-deliver,prefetch-stop' }
            )) {
                $script:ReleaseCalls.Clear()
                $script:ReleaseFailure = $case.Failure
                $code = 0
                try { Invoke-PbiRelease @{ DotNetMode = $mode } }
                catch { $code = $_.Exception.Data['ExitCode'] }
                Assert ($code -eq $case.Code) 'release 必须保留首个失败退出码'
                Assert (($script:ReleaseCalls -join ',') -ceq $case.Calls) 'release 门禁通过并交付发布目录后才交付安装包, 失败时丢弃未交付安装包'
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
