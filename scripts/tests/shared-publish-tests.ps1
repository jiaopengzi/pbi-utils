# FilePath    : pbi-utils\scripts\tests\shared-publish-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离夹具验证同目录聚合、完整框架闭包、冲突拒绝和独立编译输出保护.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$savedOutputEncoding = [Console]::OutputEncoding
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $root 'scripts/SharedPublish.psm1') -Force
Import-Module (Join-Path $root 'scripts/PublishArtifacts.psm1') -Force
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-shared-' + [Guid]::NewGuid().ToString('N'))
$coreId = 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64'
$windowsId = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'
$frameworkVersion = '9.0.1'
$targetName = '.NETCoreApp,Version=v9.0/win-x64'
$protectedKey = 'System.Security.Cryptography.ProtectedData/9.0.0'
$facades = @('Microsoft.VisualBasic.dll', 'System.Drawing.dll', 'WindowsBase.dll')

<# .SYNOPSIS 断言业务不变量, 失败抛出当前场景说明. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 将夹具对象写为 UTF-8 JSON, 参数为目标文件和完整对象, 写入失败直接抛错. #>
function Write-TestJson([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8 }
<# .SYNOPSIS 创建托管资产版本声明, 参数为程序集和文件版本, 返回独立字典. #>
function New-TestMetadata([string]$Version = '9.0.1.0') { return @{ assemblyVersion = $Version; fileVersion = $Version } }
<#
.SYNOPSIS 创建 CLI、业务后台与桌面三个隔离 publish 输出, 不使用真实程序集或启动应用.
.PARAMETER Name 场景目录名.
.OUTPUTS 源目录、空 staging 和三份依赖描述; 创建失败直接抛错.
#>
function New-TestInputs([string]$Name) {
    $case = Join-Path $sandbox $Name
    $compiled = Join-Path $case 'compiled'
    $staging = Join-Path $case ('.pbi-utils-publish-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $staging -Force
    $descriptions = @{}
    foreach ($hostName in @('cli', 'host', 'desktop')) {
        $directory = Join-Path $compiled $hostName
        $null = New-Item -ItemType Directory -Path $directory -Force
        $entry = switch ($hostName) { 'cli' { 'pbi-utils' }; 'host' { 'PbiUtils.Host' }; 'desktop' { 'PbiUtils.Desktop' } }
        foreach ($suffix in @('exe', 'dll')) { Set-Content -LiteralPath (Join-Path $directory "$entry.$suffix") -Value "$entry $suffix" }
        Set-Content -LiteralPath (Join-Path $directory 'coreclr.dll') -Value 'same core runtime'
        foreach ($file in @('hostfxr.dll', 'hostpolicy.dll')) { Set-Content -LiteralPath (Join-Path $directory $file) -Value "same $file" }
        Set-Content -LiteralPath (Join-Path $directory 'System.Private.CoreLib.dll') -Value 'same managed core runtime'
        Set-Content -LiteralPath (Join-Path $directory 'Shared.dll') -Value 'same shared product dependency'
        foreach ($file in $facades + 'System.Security.Cryptography.ProtectedData.dll') { Set-Content -LiteralPath (Join-Path $directory $file) -Value "$(if ($hostName -eq 'cli') { 'cli' } else { 'windows' }) $file" }
        $dependencies = @{ $coreId = $frameworkVersion; 'Shared' = '1.0.0' }
        $coreRuntime = @{ 'System.Private.CoreLib.dll' = (New-TestMetadata) }
        $target = @{
            "$entry/1.0.0" = @{ dependencies = $dependencies; runtime = @{ "$entry.dll" = @{} } }
            "$coreId/$frameworkVersion" = @{ runtime = $coreRuntime; native = @{ 'coreclr.dll' = @{ fileVersion = '9.0.1.0' } } }
            'Shared/1.0.0' = @{ runtime = @{ 'Shared.dll' = (New-TestMetadata) } }
        }
        $libraries = @{
            "$entry/1.0.0" = @{ type = 'project'; serviceable = $false; sha512 = '' }
            "$coreId/$frameworkVersion" = @{ type = 'runtimepack'; serviceable = $false; sha512 = '' }
            'Shared/1.0.0' = @{ type = 'project'; serviceable = $false; sha512 = '' }
        }
        $frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = $frameworkVersion })
        if ($hostName -eq 'cli') {
            foreach ($file in $facades) { $coreRuntime[$file] = New-TestMetadata '4.0.0.0' }
            $target[$protectedKey] = @{ runtime = @{ 'lib/net9.0/System.Security.Cryptography.ProtectedData.dll' = (New-TestMetadata '9.0.0.0') } }
            $libraries[$protectedKey] = @{ type = 'package'; serviceable = $true; sha512 = ''; path = 'system.security.cryptography.protecteddata/9.0.0' }
            $target['Shared/1.0.0'].dependencies = @{ 'System.Security.Cryptography.ProtectedData' = '9.0.0' }
        } else {
            $dependencies[$windowsId] = $frameworkVersion
            $windowsRuntime = @{ 'System.Security.Cryptography.ProtectedData.dll' = (New-TestMetadata); 'Windows.Only.dll' = (New-TestMetadata) }
            foreach ($file in $facades) {
                if ($hostName -eq 'host' -and $file -cne 'WindowsBase.dll') {
                    # WPF 运行时不接管 VB/Drawing facade, 模拟实际 Host 与完整 WinForms 桌面的差异.
                    $coreRuntime[$file] = New-TestMetadata '4.0.0.0'
                    Set-Content -LiteralPath (Join-Path $directory $file) -Value "cli $file"
                } else { $windowsRuntime[$file] = New-TestMetadata }
            }
            if ($hostName -eq 'desktop') {
                $windowsRuntime['Forms.Only.dll'] = New-TestMetadata
                Set-Content -LiteralPath (Join-Path $directory 'Forms.Only.dll') -Value 'complete WindowsDesktop asset'
            }
            $target["$windowsId/$frameworkVersion"] = @{ runtime = $windowsRuntime; native = @{ 'Windows.Only.Native.dll' = @{ fileVersion = '9.0.1.0' } } }
            $libraries["$windowsId/$frameworkVersion"] = @{ type = 'runtimepack'; serviceable = $false; sha512 = '' }
            $frameworks += @{ name = 'Microsoft.WindowsDesktop.App'; version = $frameworkVersion }
            foreach ($file in @('Windows.Only.dll', 'Windows.Only.Native.dll')) { Set-Content -LiteralPath (Join-Path $directory $file) -Value $file }
        }
        $deps = @{ runtimeTarget = @{ name = $targetName; signature = '' }; compilationOptions = @{}; targets = @{ $targetName = $target }; libraries = $libraries }
        $config = @{ runtimeOptions = @{ tfm = 'net9.0'; includedFrameworks = $frameworks; configProperties = @{ "$hostName-only" = $true } } }
        $descriptions[$hostName] = @{ Deps = $deps; Config = $config; Directory = $directory; Entry = $entry }
    }
    return @{ Compiled = $compiled; Staging = $staging; Cli = $descriptions.cli; Host = $descriptions.host; Desktop = $descriptions.desktop }
}
<# .SYNOPSIS 保存场景的描述对象, 参数为本测试场景; 写入错误直接抛错. #>
function Save-TestInputs($Case) {
    foreach ($hostName in @('Cli', 'Host', 'Desktop')) {
        $description = $Case[$hostName]
        Write-TestJson (Join-Path $description.Directory "$($description.Entry).deps.json") $description.Deps
        Write-TestJson (Join-Path $description.Directory "$($description.Entry).runtimeconfig.json") $description.Config
    }
}
<# .SYNOPSIS 返回目录精确路径和内容哈希快照, 参数为夹具源根目录; 枚举失败抛错. #>
function Get-TestSnapshot([string]$Directory) {
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object { [IO.Path]::GetRelativePath($Directory, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|')
}
<#
.SYNOPSIS 验证错误在聚合写入前发生, 且完整输入保持原内容.
.PARAMETER Case 当前隔离场景.
.PARAMETER Pattern 预期错误提示模式.
.PARAMETER DeploymentMode 当前场景的部署模式, 默认为自包含夹具.
.OUTPUTS 无; 未拒绝、提示错误、写 staging 或修改输入时抛错.
#>
function Assert-TestRejected($Case, [string]$Pattern, [string]$DeploymentMode = 'self-contained') {
    Save-TestInputs $Case
    $before = Get-TestSnapshot $Case.Compiled
    $failure = $null
    try { Merge-PbiPublishedArtifacts $Case.Compiled $Case.Staging 'All' $DeploymentMode } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.Exception.Message -match $Pattern) "未按预期拒绝共享发布: $Pattern; $failure"
    Assert (@(Get-ChildItem -LiteralPath $Case.Staging -Force).Count -eq 0) '预检失败不得写入 staging'
    Assert ((Get-TestSnapshot $Case.Compiled) -ceq $before) '拒绝聚合时不得修改独立编译输出'
}

<# .SYNOPSIS 将隔离夹具转换为真实 FDD 形状, 参数为场景名, 返回不含 runtimepack 的三个独立入口; 文件错误抛错. #>
function New-FddTestInputs([string]$Name) {
    $case = New-TestInputs $Name
    foreach ($kind in @('Cli', 'Host', 'Desktop')) {
        $description = $case[$kind]
        $target = $description.Deps.targets[$targetName]
        foreach ($key in @($target.Keys | Where-Object { $_.StartsWith('runtimepack.') })) {
            foreach ($section in @('runtime', 'native')) {
                foreach ($asset in $target[$key][$section].Keys) { Remove-Item -LiteralPath (Join-Path $description.Directory ([IO.Path]::GetFileName($asset))) }
            }
            $null = $target.Remove($key); $null = $description.Deps.libraries.Remove($key)
        }
        foreach ($file in @('hostfxr.dll', 'hostpolicy.dll')) { Remove-Item -LiteralPath (Join-Path $description.Directory $file) }
        $null = $target["$($description.Entry)/1.0.0"].dependencies.Remove($coreId)
        $null = $target["$($description.Entry)/1.0.0"].dependencies.Remove($windowsId)
        $frameworks = $description.Config.runtimeOptions.includedFrameworks
        $null = $description.Config.runtimeOptions.Remove('includedFrameworks')
        if ($kind -eq 'Cli') { $description.Config.runtimeOptions.framework = $frameworks[0] }
        else { $description.Config.runtimeOptions.frameworks = $frameworks }
    }
    return $case
}

<# .SYNOPSIS 为测试产物生成 schema3 精确清单, 参数为根目录、部署模式及范围; 返回已写入的字典, 失败抛错. #>
function Write-TestManifest([string]$Directory, [string]$DeploymentMode, [string]$Scope = 'Core') {
    $manifest = @{ schemaVersion = 3; layout = 'shared'; version = '1.0.0'; runtime = 'win-x64'; scope = $Scope; configuration = 'Release'; deploymentMode = $DeploymentMode }
    $manifest.files = @(Get-PbiPublishFiles $Directory | Where-Object Name -ne 'manifest.json' | ForEach-Object {
        @{ file = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    Write-TestJson (Join-Path $Directory 'manifest.json') $manifest
    return $manifest
}

<# .SYNOPSIS 断言已同步哈希的伪造发布描述仍被拒绝, 参数为目录、模式与预期错误; 未拒绝时抛错. #>
function Assert-TestManifestRejected([string]$Directory, [string]$DeploymentMode, [string]$Pattern) {
    $null = Write-TestManifest $Directory $DeploymentMode
    $failure = $null
    try { $null = Assert-PbiPublishedArtifacts $Directory } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.Exception.Message -match $Pattern) "发布模式描述未按预期拒绝: $Pattern; $failure"
}

try {
    # C# 测试宿主固定按 UTF-8 读取子进程, 不能继承 Windows 的旧代码页.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $case = New-TestInputs 'complete'
    Save-TestInputs $case
    $before = Get-TestSnapshot $case.Compiled
    Merge-PbiPublishedArtifacts $case.Compiled $case.Staging 'All' 'self-contained'
    $deps = Get-Content -LiteralPath (Join-Path $case.Staging 'pbi-utils.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $config = Get-Content -LiteralPath (Join-Path $case.Staging 'pbi-utils.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $target = $deps.targets[$targetName]
    Assert (!$target.Contains($protectedKey) -and !$deps.libraries.Contains($protectedKey) -and !$target['Shared/1.0.0'].dependencies.Contains('System.Security.Cryptography.ProtectedData')) '框架接管后必须移除旧包及依赖边'
    Assert ($target["$windowsId/$frameworkVersion"].runtime.Contains('Windows.Only.dll') -and $target["$windowsId/$frameworkVersion"].native.Contains('Windows.Only.Native.dll')) '必须保留完整 WindowsDesktop 托管及原生闭包'
    Assert (!$target["$coreId/$frameworkVersion"].runtime.Contains('WindowsBase.dll')) '核心框架不得保留已被接管的 facade'
    Assert ($target['pbi-utils/1.0.0'].dependencies[$windowsId] -ceq $frameworkVersion -and $deps.libraries["$windowsId/$frameworkVersion"].type -ceq 'runtimepack') '根依赖和 library 必须声明共享框架'
    Assert (!$target.Contains('PbiUtils.Desktop/1.0.0') -and !$deps.libraries.Contains('PbiUtils.Desktop/1.0.0')) 'CLI 描述不能引入 Desktop 产品程序集'
    Assert (@($config.runtimeOptions.includedFrameworks).Count -eq 2 -and $config.runtimeOptions.configProperties['cli-only'] -and !$config.runtimeOptions.configProperties.Contains('desktop-only')) 'CLI 应更新框架而保留自身配置'
    foreach ($name in $facades + 'System.Security.Cryptography.ProtectedData.dll') { Assert ((Get-Content -LiteralPath (Join-Path $case.Staging $name) -Raw).StartsWith('windows ')) "受控冲突必须选择 Windows 框架权威资产: $name" }
    Assert ([IO.File]::Exists((Join-Path $case.Staging 'PbiUtils.Host.exe'))) '完整包必须包含专用业务后台'
    $hostDeps = Get-Content -LiteralPath (Join-Path $case.Staging 'PbiUtils.Host.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $hostTarget = $hostDeps.targets[$targetName]
    Assert ($hostTarget["$windowsId/$frameworkVersion"].runtime.Contains('Forms.Only.dll') -and !$hostTarget["$coreId/$frameworkVersion"].runtime.Contains('Microsoft.VisualBasic.dll')) 'All 必须把后台 WPF 闭包升级为完整桌面权威, 不能只替换文件而保留旧来源'
    Assert ((Get-Content -LiteralPath (Join-Path $case.Staging 'PbiUtils.Desktop.deps.json') -Raw) -ceq (Get-Content -LiteralPath (Join-Path $case.Desktop.Directory 'PbiUtils.Desktop.deps.json') -Raw)) 'Desktop 产品描述必须原样保留'
    Assert ((Get-TestSnapshot $case.Compiled) -ceq $before) '完整聚合必须保留独立编译输出'
    Assert (![IO.Directory]::Exists((Join-Path $case.Staging 'cli')) -and ![IO.Directory]::Exists((Join-Path $case.Staging 'desktop'))) '最终产物必须同目录'

    $case = New-TestInputs 'core'
    Save-TestInputs $case
    Merge-PbiPublishedArtifacts $case.Compiled $case.Staging 'Core' 'self-contained'
    Assert ([IO.File]::Exists((Join-Path $case.Staging 'PbiUtils.Host.exe'))) 'Core 必须包含专用业务后台'
    Assert (![IO.File]::Exists((Join-Path $case.Staging 'PbiUtils.Desktop.exe')) -and ![IO.Directory]::Exists((Join-Path $case.Staging 'web'))) 'Core 不得包含 GUI 或前端'
    $coreConfig = Get-Content -LiteralPath (Join-Path $case.Staging 'pbi-utils.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert (@($coreConfig.runtimeOptions.includedFrameworks).Count -eq 2) 'Core CLI 必须使用业务后台完整框架闭包'
    $coreDeps = Get-Content -LiteralPath (Join-Path $case.Staging 'pbi-utils.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($coreDeps.targets[$targetName]["$coreId/$frameworkVersion"].runtime.Contains('Microsoft.VisualBasic.dll') -and !$coreDeps.targets[$targetName]["$windowsId/$frameworkVersion"].runtime.Contains('Forms.Only.dll')) 'Core 必须保留真实 WPF 资产归属, 不引入未使用的 WinForms 闭包'
    Assert ((Get-Content -LiteralPath (Join-Path $case.Staging 'PbiUtils.Host.deps.json') -Raw) -ceq (Get-Content -LiteralPath (Join-Path $case.Host.Directory 'PbiUtils.Host.deps.json') -Raw)) '后台依赖描述必须原样保留'

    $case = New-TestInputs 'host-conflict'
    Set-Content -LiteralPath (Join-Path $case.Host.Directory 'Shared.dll') -Value 'conflicting host dependency'
    Assert-TestRejected $case '冲突|来源'

    $case = New-TestInputs 'same-framework-owner-content'
    Set-Content -LiteralPath (Join-Path $case.Desktop.Directory 'WindowsBase.dll') -Value 'different bytes with identical framework owner and metadata'
    Assert-TestRejected $case '冲突|来源'

    $case = New-TestInputs 'host-framework-mismatch'
    $case.Host.Config.runtimeOptions.includedFrameworks[1].version = '9.0.2'
    Assert-TestRejected $case '不一致'

    $case = New-TestInputs 'host-owner-version'
    $hostTarget = $case.Host.Deps.targets[$targetName]
    $hostTarget['Shared/2.0.0'] = $hostTarget['Shared/1.0.0']
    $null = $hostTarget.Remove('Shared/1.0.0')
    $case.Host.Deps.libraries['Shared/2.0.0'] = $case.Host.Deps.libraries['Shared/1.0.0']
    $null = $case.Host.Deps.libraries.Remove('Shared/1.0.0')
    $hostTarget['PbiUtils.Host/1.0.0'].dependencies.Shared = '2.0.0'
    Assert-TestRejected $case '来源|不一致'

    $case = New-TestInputs 'unknown-conflict'
    Set-Content -LiteralPath (Join-Path $case.Desktop.Directory 'Shared.dll') -Value 'unknown conflicting product dependency'
    Assert-TestRejected $case '冲突|来源'

    $case = New-TestInputs 'version-regression'
    $case.Desktop.Deps.targets[$targetName]["$windowsId/$frameworkVersion"].runtime['WindowsBase.dll'].fileVersion = '1.0.0.0'
    Assert-TestRejected $case '回退'

    $case = New-TestInputs 'package-shape'
    $case.Cli.Deps.targets[$targetName][$protectedKey].dependencies = @{ 'Shared' = '1.0.0' }
    Assert-TestRejected $case '包形状'

    $case = New-TestInputs 'package-source'
    $case.Cli.Deps.libraries[$protectedKey].type = 'project'
    Assert-TestRejected $case '包形状'

    $case = New-TestInputs 'runtime-mismatch'
    $case.Desktop.Config.runtimeOptions.includedFrameworks[0].version = '9.0.2'
    Assert-TestRejected $case '不一致'

    $case = New-TestInputs 'runtime-duplicate'
    $case.Cli.Deps.targets[$targetName]['Shared/1.0.0'].runtime['WindowsBase.dll'] = New-TestMetadata
    Assert-TestRejected $case '拥有者重复'

    $case = New-TestInputs 'missing-runtime'
    Remove-Item -LiteralPath (Join-Path $case.Desktop.Directory 'Windows.Only.Native.dll')
    Assert-TestRejected $case '文件缺失'

    $case = New-TestInputs 'core-content-conflict'
    Set-Content -LiteralPath (Join-Path $case.Desktop.Directory 'coreclr.dll') -Value 'different native core runtime'
    Assert-TestRejected $case '冲突|来源'

    $case = New-FddTestInputs 'fdd-core'
    Save-TestInputs $case
    $before = Get-TestSnapshot $case.Compiled
    Merge-PbiPublishedArtifacts $case.Compiled $case.Staging 'Core' 'framework-dependent'
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host')) {
        $config = Get-Content -LiteralPath (Join-Path $case.Staging "$entry.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
        Assert (!$config.runtimeOptions.Contains('includedFrameworks') -and !$config.runtimeOptions.Contains('framework') -and @($config.runtimeOptions.frameworks).Count -eq 2) 'FDD 必须统一两个共享框架且不携带自包含声明'
    }
    Assert ((Get-TestSnapshot $case.Compiled) -ceq $before) 'FDD 聚合不得修改独立输出'
    $null = Write-TestManifest $case.Staging 'framework-dependent'
    $null = Assert-PbiPublishedArtifacts $case.Staging
    Assert-TestManifestRejected $case.Staging 'self-contained' '自包含|必需文件|运行时'
    Set-Content -LiteralPath (Join-Path $case.Staging 'coreclr.dll') -Value 'unexpected runtime'
    Assert-TestManifestRejected $case.Staging 'framework-dependent' '运行时|自包含'

    $case = New-FddTestInputs 'fdd-framework-mismatch'
    $case.Host.Config.runtimeOptions.frameworks[0].version = '9.0.2'
    Assert-TestRejected $case '框架|不一致' 'framework-dependent'

    $case = New-FddTestInputs 'fdd-conflict'
    Set-Content -LiteralPath (Join-Path $case.Host.Directory 'Shared.dll') -Value 'different dependency'
    Assert-TestRejected $case '冲突' 'framework-dependent'

    $case = New-FddTestInputs 'fdd-missing-dependency'
    Remove-Item -LiteralPath (Join-Path $case.Cli.Directory 'Shared.dll')
    Assert-TestRejected $case '缺失' 'framework-dependent'

    $case = New-FddTestInputs 'fdd-runtimepack'
    $case.Cli.Deps.libraries['unexpected/1.0.0'] = @{ type = 'runtimepack' }
    Assert-TestRejected $case 'runtimepack|运行时' 'framework-dependent'

    $case = New-FddTestInputs 'fdd-config-forgery'
    Save-TestInputs $case
    Merge-PbiPublishedArtifacts $case.Compiled $case.Staging 'Core' 'framework-dependent'
    $config = Get-Content -LiteralPath (Join-Path $case.Staging 'pbi-utils.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $config.runtimeOptions.includedFrameworks = $config.runtimeOptions.frameworks
    Write-TestJson (Join-Path $case.Staging 'pbi-utils.runtimeconfig.json') $config
    Assert-TestManifestRejected $case.Staging 'framework-dependent' '声明|自包含|框架'

    $case = New-FddTestInputs 'fdd-asset-metadata-forgery'
    Save-TestInputs $case
    Merge-PbiPublishedArtifacts $case.Compiled $case.Staging 'Core' 'framework-dependent'
    $deps = Get-Content -LiteralPath (Join-Path $case.Staging 'PbiUtils.Host.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $deps.targets[$targetName]['Shared/1.0.0'].runtime['Shared.dll'].fileVersion = '9.0.2.0'
    Write-TestJson (Join-Path $case.Staging 'PbiUtils.Host.deps.json') $deps
    Assert-TestManifestRejected $case.Staging 'framework-dependent' '元数据|版本|不一致'

    $case = New-FddTestInputs 'fdd-target-framework-forgery'
    $case.Cli.Config.runtimeOptions.tfm = 'net10.0'
    Assert-TestRejected $case '目标|框架|不一致' 'framework-dependent'

    $case = New-FddTestInputs 'fdd-incomplete-dependency-edge'
    $case.Cli.Deps.targets[$targetName]['Shared/1.0.0'].dependencies.Missing = '1.0.0'
    Assert-TestRejected $case '闭包缺失' 'framework-dependent'

    $case = New-FddTestInputs 'fdd-all'
    Save-TestInputs $case
    Merge-PbiPublishedArtifacts $case.Compiled $case.Staging 'All' 'framework-dependent'
    Assert ([IO.File]::Exists((Join-Path $case.Staging 'PbiUtils.Desktop.exe'))) 'FDD All 必须包含桌面入口'
    $config = Get-Content -LiteralPath (Join-Path $case.Staging 'pbi-utils.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($config.runtimeOptions.configProperties['cli-only'] -and !$config.runtimeOptions.configProperties.Contains('desktop-only')) 'FDD 必须保留每个入口自己的运行配置'

    $case = New-TestInputs 'sc-manifest'
    Save-TestInputs $case
    Merge-PbiPublishedArtifacts $case.Compiled $case.Staging 'Core' 'self-contained'
    $manifest = Write-TestManifest $case.Staging 'self-contained'
    $null = Assert-PbiPublishedArtifacts $case.Staging
    $manifest.schemaVersion = 2
    Write-TestJson (Join-Path $case.Staging 'manifest.json') $manifest
    $failure = $null
    try { $null = Assert-PbiPublishedArtifacts $case.Staging } catch { $failure = $_ }
    Assert ($null -ne $failure) '旧 schema2 发布清单必须拒绝'
    $deps = Get-Content -LiteralPath (Join-Path $case.Staging 'pbi-utils.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $null = $deps.targets[$targetName].Remove("$windowsId/$frameworkVersion")
    Write-TestJson (Join-Path $case.Staging 'pbi-utils.deps.json') $deps
    Assert-TestManifestRejected $case.Staging 'self-contained' '闭包缺失|运行时包'

    Write-Output 'PASS 共享发布双部署模式、框架闭包、精确清单、冲突拒绝及独立编译输出保护'
}
finally {
    [Console]::OutputEncoding = $savedOutputEncoding
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-shared-[0-9a-f]{32}$') { throw '共享发布测试清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
