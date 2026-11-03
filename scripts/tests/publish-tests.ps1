# FilePath    : pbi-utils\scripts\tests\publish-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离目录模拟发布工具, 验证配置与模式选择、精确清单和单目录交付失败保护.

<# .SYNOPSIS 用 fake dotnet 验证发布边界, 不写用户工程或启动真实应用. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils publish ' + [Guid]::NewGuid().ToString('N'))
$savedDirectory = Get-Location
$savedOutputEncoding = [Console]::OutputEncoding
$script:NativeCommands = [Collections.Generic.List[object]]::new()
$script:BuildEvents = [Collections.Generic.List[string]]::new()
$script:Messages = [Collections.Generic.List[object]]::new()

<# .SYNOPSIS 固定契约断言. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 记录发布状态及等级, 保持隔离测试不依赖终端着色. #>
function Write-PbiMessage([string]$Message, [string]$Level = 'Info') { $script:Messages.Add(@{ Message = $Message; Level = $Level }) }
<# .SYNOPSIS 保留独立 C# 构建缺失前端等前置检查的真实提示. #>
function Stop-Pbi([int]$Code, [string]$Message) { throw $Message }
<#
.SYNOPSIS 按部署模式生成 dotnet publish 的最小入口、框架及共享依赖元数据.
.PARAMETER Output 测试拥有的独立 publish 目录.
.PARAMETER Kind CLI、业务后台或桌面入口; 后两者包含 WindowsDesktop 框架声明.
.PARAMETER SelfContained 是否生成随包运行时; false 时只声明共享框架需求.
.NOTES 文件内容为明确的测试替身, 只验证合并边界, 不宣称可执行或真实程序集兼容.
#>
function New-TestPublishFiles([string]$Output, [string]$Kind, [bool]$SelfContained) {
    $entry = switch ($Kind) { 'cli' { 'pbi-utils' }; 'host' { 'PbiUtils.Host' }; 'desktop' { 'PbiUtils.Desktop' } }
    foreach ($extension in @('exe', 'dll')) { Set-Content -LiteralPath (Join-Path $Output "$entry.$extension") -Value "synthetic $entry $extension" }
    if ($SelfContained) {
        foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) { Set-Content -LiteralPath (Join-Path $Output $name) -Value "synthetic runtime $name" }
    }
    $dependencyContent = if ($Kind -eq 'desktop' -and ($script:ConflictingDependency -or (!$SelfContained -and $script:ConflictingFrameworkDependency))) { 'conflicting desktop dependency' } else { 'shared dependency' }
    Set-Content -LiteralPath (Join-Path $Output 'Shared.Dependency.dll') -Value $dependencyContent
    $framework = 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64'
    $dependencies = @{ $framework = '10.0.13'; 'Shared.Dependency' = '1.2.3' }
    $target = @{
        "$entry/1.2.3" = @{ dependencies = $dependencies; runtime = @{ "$entry.dll" = @{} } }
        "$framework/10.0.13" = @{
            runtime = @{ 'System.Private.CoreLib.dll' = @{ assemblyVersion = '10.0.0.0'; fileVersion = '10.0.1326.50504' } }
            native = @{ 'coreclr.dll' = @{ fileVersion = '10.0.1326.50504' }; 'hostfxr.dll' = @{ fileVersion = '10.0.1326.50504' }; 'hostpolicy.dll' = @{ fileVersion = '10.0.1326.50504' } }
        }
        'Shared.Dependency/1.2.3' = @{ runtime = @{ 'Shared.Dependency.dll' = @{ assemblyVersion = '1.2.3.0'; fileVersion = '1.2.3.0' } } }
    }
    $libraries = @{
        "$entry/1.2.3" = @{ type = 'project'; serviceable = $false; sha512 = '' }
        "$framework/10.0.13" = @{ type = 'runtimepack'; serviceable = $false; sha512 = '' }
        'Shared.Dependency/1.2.3' = @{ type = 'package'; serviceable = $false; sha512 = ''; path = 'shared.dependency/1.2.3'; hashPath = 'shared.dependency.1.2.3.nupkg.sha512' }
    }
    $frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.13' })
    if ($Kind -ne 'cli') {
        $windowsFramework = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'
        $dependencies[$windowsFramework] = '10.0.13'
        $target["$windowsFramework/10.0.13"] = @{}
        $libraries["$windowsFramework/10.0.13"] = @{ type = 'runtimepack'; serviceable = $false; sha512 = '' }
        $frameworks += @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.13' }
    }
    if (!$SelfContained) {
        foreach ($name in @($dependencies.Keys | Where-Object { $_ -like 'runtimepack.*' })) { $dependencies.Remove($name) }
        foreach ($name in @($target.Keys | Where-Object { $_ -like 'runtimepack.*' })) { $target.Remove($name); $libraries.Remove($name) }
    }
    @{ runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/win-x64'; signature = '' }; compilationOptions = @{}; targets = @{ '.NETCoreApp,Version=v10.0' = @{}; '.NETCoreApp,Version=v10.0/win-x64' = $target }; libraries = $libraries; runtimes = @{ 'win-x64' = @('win', 'any', 'base') } } |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Output "$entry.deps.json") -Encoding utf8
    $runtimeOptions = @{ tfm = 'net10.0'; configProperties = @{ 'System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization' = $false } }
    if ($SelfContained) { $runtimeOptions.includedFrameworks = $frameworks }
    elseif ($Kind -eq 'cli') { $runtimeOptions.framework = $frameworks[0] }
    else { $runtimeOptions.frameworks = $frameworks }
    @{ runtimeOptions = $runtimeOptions } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output "$entry.runtimeconfig.json") -Encoding utf8
}
<# .SYNOPSIS 仅模拟原生命令的确定性输出或失败, 不调用 SDK. #>
function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
    $script:NativeCommands.Add(@{ File = $File; Arguments = $Arguments.Clone() })
    Assert ($File -eq 'dotnet' -and $Arguments[0] -eq 'publish') 'C# 发布不得额外执行 build 或其他工具'
    Assert ($Arguments -contains '--no-restore') 'C# 发布不得隐式还原依赖'
    $desktop = $Arguments[1] -like '*Desktop'
    $kind = if ($desktop) { 'desktop' } elseif ($Arguments[1] -like '*Host') { 'host' } else { 'cli' }
    $modeIndex = [Array]::IndexOf($Arguments, '--self-contained')
    Assert ($modeIndex -ge 0 -and $Arguments[$modeIndex + 1] -in @('true', 'false')) '发布必须显式声明部署模式'
    $selfContained = $Arguments[$modeIndex + 1] -eq 'true'
    $script:BuildEvents.Add("publish $kind")
    if ($script:FailPublish) { throw '模拟 publish 失败' }
    if (!$selfContained -and $kind -eq 'host' -and $script:FailFrameworkSecondPublish) { throw '模拟 FDD 第二次 publish 失败' }
    $output = $Arguments[[Array]::IndexOf($Arguments, '-o') + 1]
    $absoluteOutput = [IO.Path]::GetFullPath((Join-Path (Get-Location) $output))
    if (!$absoluteOutput.StartsWith($sandbox + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '测试产物超出隔离目录' }
    $null = New-Item -ItemType Directory -Path $output -Force
    Assert ([IO.Path]::GetFileName($output) -eq $kind) '三个 publish 必须使用不同的临时 cli/host/desktop 目录'
    New-TestPublishFiles $output $kind $selfContained
    if ($desktop -and $Arguments -notcontains '-p:IncludePbiFrontend=false') {
        $null = New-Item -ItemType Directory -Path (Join-Path $output 'web') -Force
        foreach ($entry in Get-ChildItem -LiteralPath 'web/dist' -Force) { Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $output 'web') -Recurse -Force }
    }
    if ($script:IncludeRemovedResource) { Set-Content -LiteralPath (Join-Path $output 'removed-resource.dll') -Value 'removed next build' }
}
<# .SYNOPSIS 隔离单语言构建, 不替换发布逻辑. #>
function Invoke-PbiGroup { return $false }
<#
.SYNOPSIS 执行真实发布路径, 模拟已规范化的配置与模式参数, 可传入 C# 编译后的前端回调.
.PARAMETER Scope 当前测试选择的 Core 或 All.
.PARAMETER Configuration 编译配置; 默认 Debug.
.PARAMETER AfterCompile 可选前端回调, 失败不得交付.
.PARAMETER DotNetMode 指定一种部署模式, 未指定时按配置采用生产入口约定.
#>
function Build([string]$Scope, [string]$Configuration = 'Debug', [scriptblock]$AfterCompile = $null, [string]$DotNetMode = '') {
    $PbiInvocation = @{ Action = 'buildC#'; Options = @{ Scope = $Scope; Configuration = $Configuration } }
    $PbiInvocation.Options.DotNetMode = if ($DotNetMode) { $DotNetMode } elseif ($Configuration -eq 'Release') { 'without-dotnet' } else { 'with-dotnet' }
    if ($AfterCompile) { $PbiInvocation.AfterCompile = $AfterCompile }
    . (Join-Path $sandbox 'scripts/build.ps1')
}
<# .SYNOPSIS 只清理本次测试隔离目录中的前端产物, 模拟首次或干净构建. #>
function Remove-TestFrontend {
    $dist = [IO.Path]::GetFullPath((Join-Path $sandbox 'web/dist'))
    Assert ($dist.StartsWith($sandbox + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '前端清理必须位于隔离目录'
    if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
}
<#
.SYNOPSIS 读取测试目录的完整文件快照, 用于验证编译、前端或合并失败未交付任一包.
.PARAMETER Path 当前测试拥有且已经生成的发布目录.
.OUTPUTS 按相对路径排序的文件名和哈希文本; 枚举或读取失败直接终止测试.
#>
function Get-TestPublishSnapshot([string]$Path) {
    return (Get-PbiPublishFiles $Path | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Path, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
}
<#
.SYNOPSIS 验证选中模式的真实清单、框架声明和运行时文件, 不启动测试替身.
.PARAMETER Path 本轮交付的单个目录.
.PARAMETER Configuration 期望的 Debug 或 Release 配置.
.PARAMETER DotNetMode 期望的 with-dotnet 或 without-dotnet 模式.
.NOTES 模式、清单或依赖闭包不一致时明确失败, 不以目录名推断成功.
#>
function Assert-TestPublishedPackage([string]$Path, [string]$Configuration, [string]$DotNetMode) {
    $expectedMode = if ($DotNetMode -eq 'with-dotnet') { 'self-contained' } else { 'framework-dependent' }
    $manifest = Assert-PbiPublishedArtifacts $Path
    Assert ($manifest.schemaVersion -eq 3 -and $manifest.configuration -ceq $Configuration -and $manifest.deploymentMode -eq $expectedMode) '清单必须声明实际配置和选中模式'
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
        $config = Get-Content -LiteralPath (Join-Path $Path "$entry.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
        $deps = Get-Content -LiteralPath (Join-Path $Path "$entry.deps.json") -Raw | ConvertFrom-Json -AsHashtable
        if ($DotNetMode -eq 'with-dotnet') {
            Assert ($config.runtimeOptions.ContainsKey('includedFrameworks') -and !$config.runtimeOptions.ContainsKey('frameworks')) '自包含入口必须声明随包框架'
            Assert (@($deps.libraries.Keys | Where-Object { $_ -like 'runtimepack.*' }).Count -eq 2) '自包含三个入口必须统一两项 runtimepack'
        } else {
            Assert (!$config.runtimeOptions.ContainsKey('includedFrameworks') -and $config.runtimeOptions.frameworks.Count -eq 2) 'FDD 三个入口必须统一外部框架需求'
            Assert (@($deps.libraries.Keys | Where-Object { $_ -like 'runtimepack.*' }).Count -eq 0) 'FDD 不得声明随包 runtimepack'
        }
    }
    Assert ((Test-Path -LiteralPath (Join-Path $Path 'coreclr.dll')) -eq ($DotNetMode -eq 'with-dotnet')) '核心运行时文件仅在选择 with-dotnet 时交付'
}

try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'scripts') -Force
    foreach ($file in @('build.ps1', 'PublishArtifacts.psm1', 'SharedPublish.psm1', 'PublishRuntime.psm1')) {
        if (Test-Path -LiteralPath (Join-Path $sourceRoot "scripts/$file")) { Copy-Item -LiteralPath (Join-Path $sourceRoot "scripts/$file") -Destination (Join-Path $sandbox 'scripts') }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LicenseBundleStub.psm1') -Destination (Join-Path $sandbox 'scripts/ThirdPartyLicenses.psm1')
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'web/dist') -Force
    Set-Content -LiteralPath (Join-Path $sandbox 'web/dist/index.html') -Value 'synthetic web'
    Set-Content -LiteralPath (Join-Path $sandbox 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $sandbox 'global.json') -Value '{"sdk":{"version":"10.0.403"}}'
    Set-Location -LiteralPath $sandbox
    $script:FailPublish = $false
    $script:ConflictingDependency = $false
    $script:ConflictingFrameworkDependency = $false
    $script:FailFrameworkSecondPublish = $false
    $script:IncludeRemovedResource = $true
    Build 'All' 'Debug'
    $published = Join-Path $sandbox 'artifacts/build/debug/win-x64'
    Assert (Test-Path -LiteralPath (Join-Path $published 'manifest.json') -PathType Leaf) 'Debug 构建必须交付独立的 artifacts/build/debug/win-x64 目录'
    $sharedManifest = Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw | ConvertFrom-Json
    $licenseCalls = @(Get-Content -LiteralPath (Join-Path $sandbox 'license-stub-calls.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    Assert ($licenseCalls.Count -eq 1 -and $licenseCalls[0].scope -ceq 'All') 'Debug All 发布必须恰好一次按当前范围生成许可'
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        $entry = @($sharedManifest.files | Where-Object { $_.file -ceq $license })
        Assert ($entry.Count -eq 1 -and $entry[0].sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $published $license)).Hash) 'Debug 发布须将本轮许可完整纳入交付清单'
    }
    Assert ($sharedManifest.PSObject.Properties['sdkVersion'] -and $sharedManifest.sdkVersion -ceq '10.0.403') '发布清单必须记录本轮构建的 SDK 基线, 供后续安装包核验'
    Assert ($sharedManifest.schemaVersion -eq 3 -and $sharedManifest.layout -eq 'shared' -and $sharedManifest.deploymentMode -eq 'self-contained') 'Debug 发布必须声明 schemaVersion=3、shared 布局与自包含模式'
    Assert ($script:NativeCommands.Count -eq 3) 'Debug All 只编译一套自包含入口'
    foreach ($command in $script:NativeCommands) {
        Assert ($command.Arguments[[Array]::IndexOf($command.Arguments, '--self-contained') + 1] -eq 'true') 'Debug 必须随包携带运行时'
        Assert ($command.Arguments[[Array]::IndexOf($command.Arguments, '-c') + 1] -ceq 'Debug') 'Debug 编译配置必须正确传递'
    }
    Assert (!(Test-Path -LiteralPath (Join-Path $sandbox 'artifacts/build/release'))) 'Debug 构建不得生成 Release 目录'
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
        foreach ($extension in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) { Assert (Test-Path -LiteralPath (Join-Path $published "$entry.$extension") -PathType Leaf) '两个入口及各自运行元数据必须位于共享根目录' }
    }
    foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Shared.Dependency.dll')) {
        Assert (@(Get-PbiPublishFiles $published | Where-Object Name -eq $name).Count -eq 1) "相同依赖必须只保留一份: $name"
    }
    Assert (Test-Path -LiteralPath (Join-Path $published 'web/index.html') -PathType Leaf) '前端入口必须位于共享根目录下的 web'
    Assert (!(Test-Path -LiteralPath (Join-Path $published 'cli/pbi-utils.exe')) -and !(Test-Path -LiteralPath (Join-Path $published 'desktop/PbiUtils.Desktop.exe'))) '最终发布不得包含旧子目录入口'
    $manifestBefore = Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw
    $script:ConflictingDependency = $true
    try {
        try { Build 'All'; throw '同路径不同依赖应拒绝' }
        catch { Assert ($_.Exception.Message -like '*冲突*Shared.Dependency.dll*' -or $_.Exception.Message -like '*Shared.Dependency.dll*冲突*') '依赖冲突必须明确指出路径' }
        Assert ((Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw) -eq $manifestBefore) '依赖冲突必须在交付前失败并保留原产物'
        $null = Assert-PbiPublishedArtifacts $published
    } finally { $script:ConflictingDependency = $false }
    $script:IncludeRemovedResource = $false
    Build 'All'
    Assert (!(Test-Path -LiteralPath (Join-Path $published 'removed-resource.dll'))) '连续发布必须删除上一轮已移除资源'
    Build 'Core' 'Debug'
    Assert (!(Test-Path -LiteralPath (Join-Path $published 'PbiUtils.Desktop.exe')) -and !(Test-Path -LiteralPath (Join-Path $published 'PbiUtils.Desktop.dll'))) 'All 到 Core 必须删除桌面入口与程序集'
    Assert (@(Get-PbiPublishFiles (Join-Path $published 'web')).Count -eq 0) 'All 到 Core 不得混入前端文件, 允许保留已打开的空目录'
    $manifestBefore = Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw
    $script:FailPublish = $true
    try { Build 'All'; throw '发布应失败' } catch { Assert ($_.Exception.Message -eq '模拟 publish 失败') '保留真实发布错误' }
    Assert ((Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw) -eq $manifestBefore) '失败保留上次有效产物'
    $script:FailPublish = $false
    Import-Module (Join-Path $sandbox 'scripts/PublishArtifacts.psm1') -Force
    $manifest = Assert-PbiPublishedArtifacts $published
    Assert ($manifest.scope -eq 'Core' -and $manifest.configuration -eq 'Debug') '清单声明本次范围与配置'
    # 清单不能通过同步哈希集合绕过布局版本、Core 范围或自包含必需文件约束.
    foreach ($invalidLayout in @(@{ schemaVersion = 2; layout = 'shared' }, @{ schemaVersion = 3; layout = 'split' })) {
        $altered = $manifestBefore | ConvertFrom-Json
        $altered.schemaVersion = $invalidLayout.schemaVersion
        $altered.layout = $invalidLayout.layout
        $altered | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $published 'manifest.json') -Encoding utf8
        try {
            $rejected = $false
            try { $null = Assert-PbiPublishedArtifacts $published }
            catch { $rejected = $true; Assert ($_.Exception.Message -like '*清单*') '旧 schema 或未知布局必须明确拒绝' }
            Assert $rejected '失效布局应拒绝'
        } finally { Set-Content -LiteralPath (Join-Path $published 'manifest.json') -Value $manifestBefore -NoNewline }
    }
    foreach ($forbidden in @('PbiUtils.Desktop.exe', 'PbiUtils.Desktop.dll', 'PbiUtils.Desktop.deps.json', 'PbiUtils.Desktop.runtimeconfig.json', 'Microsoft.Web.WebView2.Core.dll', 'WebView2Loader.dll', 'web/probe.bin', 'cli/probe.bin', 'desktop/probe.bin', 'host/probe.bin')) {
        $file = Join-Path $published $forbidden
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file)) -Force
        Set-Content -LiteralPath $file -Value 'out of Core scope'
        $altered = $manifestBefore | ConvertFrom-Json
        $altered.files += @{ file = $forbidden; sha256 = (Get-FileHash -LiteralPath $file).Hash }
        $altered | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $published 'manifest.json') -Encoding utf8
        try {
            $rejected = $false
            $expectedError = if ($forbidden.StartsWith('cli/') -or $forbidden.StartsWith('desktop/') -or $forbidden.StartsWith('host/')) { '*旧版独立目录*' } else { '*范围外文件*' }
            try { $null = Assert-PbiPublishedArtifacts $published }
            catch { $rejected = $true; Assert ($_.Exception.Message -like $expectedError) 'Core 范围及旧入口目录必须拒绝已列入清单的文件' }
            Assert $rejected '范围外文件应拒绝'
        } finally {
            Remove-Item -LiteralPath $file
            Set-Content -LiteralPath (Join-Path $published 'manifest.json') -Value $manifestBefore -NoNewline
        }
    }
    foreach ($required in @('pbi-utils.exe', 'pbi-utils.dll', 'pbi-utils.deps.json', 'pbi-utils.runtimeconfig.json', 'PbiUtils.Host.exe', 'PbiUtils.Host.dll', 'PbiUtils.Host.deps.json', 'PbiUtils.Host.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        $file = Join-Path $published $required
        $held = Join-Path $sandbox 'required-held.bin'
        Move-Item -LiteralPath $file -Destination $held
        $altered = $manifestBefore | ConvertFrom-Json
        $altered.files = @($altered.files | Where-Object file -ne $required)
        $altered | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $published 'manifest.json') -Encoding utf8
        try {
            $rejected = $false
            try { $null = Assert-PbiPublishedArtifacts $published }
            catch { $rejected = $true; Assert ($_.Exception.Message -like "*必需文件缺失: $required") "缺少自包含必需文件必须失败: $required" }
            Assert $rejected '必需文件缺失应拒绝'
        } finally {
            Move-Item -LiteralPath $held -Destination $file
            Set-Content -LiteralPath (Join-Path $published 'manifest.json') -Value $manifestBefore -NoNewline
        }
    }
    Set-Content -LiteralPath (Join-Path $published 'extra.dll') -Value 'unlisted'
    try { $null = Assert-PbiPublishedArtifacts $published; throw '额外文件应拒绝' }
    catch { Assert ($_.Exception.Message -like '*文件集合*') '精确集合拒绝额外文件' }
    Remove-Item -LiteralPath (Join-Path $published 'extra.dll')
    $runtime = Join-Path $published 'coreclr.dll'
    $runtimeBefore = Get-Content -LiteralPath $runtime -Raw
    # 重复交付保留未变化文件, 不能仅凭旧清单、长度或时间戳跳过真实内容比较.
    $runtimeHash = (Get-FileHash -LiteralPath $runtime).Hash
    $unchangedStamp = [DateTime]::new(2001, 2, 3, 4, 5, 6, [DateTimeKind]::Utc)
    [IO.File]::SetLastWriteTimeUtc($runtime, $unchangedStamp)
    Build 'Core' 'Debug'
    Assert ([IO.File]::GetLastWriteTimeUtc($runtime) -eq $unchangedStamp) '内容未变化的文件不得重新覆盖'
    $corruptBytes = [IO.File]::ReadAllBytes($runtime)
    $corruptBytes[0] = $corruptBytes[0] -bxor 1
    [IO.File]::WriteAllBytes($runtime, $corruptBytes)
    [IO.File]::SetLastWriteTimeUtc($runtime, $unchangedStamp)
    Build 'Core' 'Debug'
    Assert ((Get-FileHash -LiteralPath $runtime).Hash -eq $runtimeHash) '旧清单不变且长度/时间相同的篡改文件仍须被替换'
    $manifestBefore = Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw
    Set-Content -LiteralPath $runtime -Value 'tampered runtime'
    try { $null = Assert-PbiPublishedArtifacts $published; throw '篡改文件应拒绝' }
    catch { Assert ($_.Exception.Message -like '*哈希不匹配*') '哈希拒绝篡改文件' }
    Set-Content -LiteralPath $runtime -Value $runtimeBefore -NoNewline
    Move-Item -LiteralPath $runtime -Destination (Join-Path $sandbox 'runtime-held.dll')
    try {
        try { $null = Assert-PbiPublishedArtifacts $published; throw '缺失文件应拒绝' }
        catch { Assert ($_.Exception.Message -like '*文件集合*') '精确集合拒绝缺失文件' }
    } finally { Move-Item -LiteralPath (Join-Path $sandbox 'runtime-held.dll') -Destination $runtime }
    $locked = [IO.File]::Open((Join-Path $published 'coreclr.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        try { Build 'Core'; throw '占用产物应拒绝' }
        catch { Assert ($_.Exception.Message -like '*占用*') '占用目录应明确失败' }
        Assert ((Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw) -eq $manifestBefore) '占用失败保留上次有效产物'
    } finally { $locked.Dispose() }
    # 目录句柄允许文件读写但拒绝目录重命名, 模拟资源管理器打开发布根或后代目录.
    if (!('PbiPublishTestDirectoryLock' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>只在隔离测试中持有 Windows 目录句柄, 验证原目录覆盖不依赖目录重命名.</summary>
public static class PbiPublishTestDirectoryLock
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    /// <summary>允许读写但不允许删除共享; 打开失败时保留系统错误供测试诊断.</summary>
    public static SafeFileHandle Open(string path)
    {
        var handle = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(code);
        }
        return handle;
    }
}
'@
    }
    # 旧布局目录可被资源管理器打开, 迁移时同样保留其目录身份.
    foreach ($relative in @('web/assets', 'cli', 'desktop')) { $null = New-Item -ItemType Directory -Path (Join-Path $published $relative) -Force }
    foreach ($directory in @($published, (Join-Path $published 'web'), (Join-Path $published 'web/assets'), (Join-Path $published 'cli'), (Join-Path $published 'desktop'))) {
        $directoryLock = [PbiPublishTestDirectoryLock]::Open($directory)
        try {
            Build 'Core'
            Assert (Test-Path -LiteralPath $directory -PathType Container) '原目录覆盖不得删除或重建已打开目录'
            $null = Assert-PbiPublishedArtifacts $published
        } finally { $directoryLock.Dispose() }
    }
    $manifestBefore = Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw

    # 在写入新清单后注入失败, 验证已覆盖、已删除及新增文件均按旧快照恢复.
    $retired = Join-Path $published 'retired.dll'
    Set-Content -LiteralPath $retired -Value 'old file restored on failure'
    $manifest = $manifestBefore | ConvertFrom-Json
    $manifest.files += @{ file = 'retired.dll'; sha256 = (Get-FileHash -LiteralPath $retired).Hash }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $published 'manifest.json') -Encoding utf8
    $manifestBefore = Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw
    $rollbackStage = Join-Path (Split-Path $published -Parent) ('.pbi-utils-publish-' + [Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $published -Destination $rollbackStage -Recurse
    Set-Content -LiteralPath (Join-Path $rollbackStage 'coreclr.dll') -Value 'new runtime'
    Set-Content -LiteralPath (Join-Path $rollbackStage 'new.dll') -Value 'new file removed on failure'
    Remove-Item -LiteralPath (Join-Path $rollbackStage 'retired.dll')
    $manifest.files = @(Get-PbiPublishFiles $rollbackStage | Where-Object FullName -ne (Join-Path $rollbackStage 'manifest.json') | ForEach-Object {
        @{ file = [IO.Path]::GetRelativePath($rollbackStage, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $rollbackStage 'manifest.json') -Encoding utf8
    $publishModule = Get-Module PublishArtifacts
    & $publishModule {
        param($Source)
        $script:FailureSource = Join-Path $Source 'manifest.json'
        # 仅拦截本次 staging 的最后一次复制, 真实执行文件写入后模拟异常, 不拦截备份恢复.
        function script:Copy-Item {
            [CmdletBinding()]
            param([string]$LiteralPath, [string]$Destination)
            Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
            if ($LiteralPath -eq $script:FailureSource) { throw '模拟清单写入后失败' }
        }
    } $rollbackStage
    try {
        try { Set-PbiPublishedArtifacts $rollbackStage $published; throw '覆盖中途失败应报错' }
        catch { Assert ($_.Exception.Message -like '*已恢复本轮修改的文件*模拟清单写入后失败*') '覆盖失败应恢复并保留原始原因' }
        Assert ((Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw) -eq $manifestBefore) '恢复旧清单'
        Assert (!(Test-Path -LiteralPath (Join-Path $published 'new.dll'))) '移除本轮新增文件'
        $null = Assert-PbiPublishedArtifacts $published
    } finally {
        & $publishModule { Remove-Item Function:script:Copy-Item; Remove-Variable FailureSource -Scope Script }
        Remove-PbiPublishTemporary $rollbackStage (Split-Path $published -Parent)
    }
    $buildLock = [IO.File]::Open((Join-Path (Split-Path $published -Parent) '.pbi-utils-publish.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        try { Build 'Core'; throw '并发交付应拒绝' }
        catch { Assert ($_.Exception.Message -like '*另一构建*') '并发交付应明确失败' }
        Assert ((Get-Content -LiteralPath (Join-Path $published 'manifest.json') -Raw) -eq $manifestBefore) '并发交付失败保留上次产物'
    } finally { $buildLock.Dispose() }
    $outside = Join-Path $sandbox 'outside'
    $null = New-Item -ItemType Directory -Path $outside
    Set-Content -LiteralPath (Join-Path $outside 'keep.txt') -Value 'protected'
    $link = Join-Path $published 'linked'
    $null = New-Item -ItemType Junction -Path $link -Target $outside
    try {
        try { Build 'Core'; throw '链接产物应拒绝' }
        catch { Assert ($_.Exception.Message -like '*链接*') '路径链接应明确失败' }
        Assert ((Get-Content -LiteralPath (Join-Path $outside 'keep.txt') -Raw).Trim() -eq 'protected') '保留链接外部文件'
    } finally { Remove-Item -LiteralPath $link -Force }

    # 没有旧 dist 时, 默认 Release 也只编译 without-dotnet 的三个入口后构建一次前端.
    $debugPublished = $published
    $debugBefore = Get-TestPublishSnapshot $debugPublished
    $published = Join-Path $sandbox 'artifacts/build/release/win-x64/without-dotnet'
    $otherPublished = Join-Path $sandbox 'artifacts/build/release/win-x64/with-dotnet'
    Remove-TestFrontend
    $script:NativeCommands.Clear()
    $script:BuildEvents.Clear()
    Build 'All' 'Release' {
        Assert (($script:BuildEvents -join ',') -eq 'publish cli,publish host,publish desktop') 'Release 组合构建必须只编译选中模式的三个入口再开始前端'
        Assert ($script:NativeCommands.Count -eq 3) 'Release All 每次只能发布一套入口'
        foreach ($command in $script:NativeCommands) { Assert ($command.Arguments -contains '-p:IncludePbiFrontend=false') '组合 C# 发布不得提前打包旧前端资源' }
        $script:BuildEvents.Add('frontend')
        $null = New-Item -ItemType Directory -Path 'web/dist/assets' -Force
        Set-Content -LiteralPath 'web/dist/index.html' -Value 'current frontend'
        Set-Content -LiteralPath 'web/dist/assets/first.js' -Value 'first frontend asset'
    }
    Assert (($script:BuildEvents -join ',') -eq 'publish cli,publish host,publish desktop,frontend') '三次 C# 发布后只能构建一次前端'
    foreach ($command in $script:NativeCommands) {
        Assert ($command.Arguments[[Array]::IndexOf($command.Arguments, '-c') + 1] -ceq 'Release') '配置必须传递给选中模式的所有入口'
        Assert ($command.Arguments[[Array]::IndexOf($command.Arguments, '--self-contained') + 1] -eq 'false') '默认 Release 必须只发布 without-dotnet'
    }
    Assert-TestPublishedPackage $published 'Release' 'without-dotnet'
    Assert (!(Test-Path -LiteralPath $otherPublished)) '未选择的 Release 模式不得生成'
    Assert ((Get-TestPublishSnapshot $debugPublished) -ceq $debugBefore) 'Release 构建不得覆盖 Debug 产物'
    Assert ((Get-Content -LiteralPath (Join-Path $published 'web/index.html') -Raw).Trim() -eq 'current frontend') '交付必须使用本轮前端'
    Assert (Test-Path -LiteralPath (Join-Path $published 'web/assets/first.js')) '本轮前端资源应完整打包'

    # 显式选中 with-dotnet 只写对应目录, 已有 without-dotnet 保持字节不变.
    $selectedBefore = Get-TestPublishSnapshot $published
    $script:NativeCommands.Clear()
    Build 'All' 'Release' -DotNetMode 'with-dotnet'
    Assert ($script:NativeCommands.Count -eq 3) '显式 with-dotnet 也只编译一次各入口'
    foreach ($command in $script:NativeCommands) { Assert ($command.Arguments[[Array]::IndexOf($command.Arguments, '--self-contained') + 1] -eq 'true') '显式模式必须正确传给 dotnet publish' }
    Assert-TestPublishedPackage $otherPublished 'Release' 'with-dotnet'
    Assert ((Get-TestPublishSnapshot $published) -ceq $selectedBefore) '构建 with-dotnet 不得改动另一模式'
    $otherBefore = Get-TestPublishSnapshot $otherPublished

    # 选中模式的第二入口编译或依赖合并失败, 原目录与未选模式均不得改变.
    foreach ($failure in @('compile', 'merge')) {
        $selectedBefore = Get-TestPublishSnapshot $published
        $script:FailFrameworkSecondPublish = $failure -eq 'compile'
        $script:ConflictingFrameworkDependency = $failure -eq 'merge'
        $script:BuildEvents.Clear()
        try {
            $rejected = $false
            try { Build 'All' 'Release' { $script:BuildEvents.Add('frontend') } }
            catch {
                $rejected = $true
                if ($failure -eq 'compile') { Assert ($_.Exception.Message -eq '模拟 FDD 第二次 publish 失败') '保留选中模式的原始编译失败' }
                else { Assert ($_.Exception.Message -like '*冲突*') '保留选中模式的原始合并失败' }
            }
            Assert $rejected '编译或合并失败必须阻止成功返回'
            Assert ((Get-TestPublishSnapshot $published) -ceq $selectedBefore) '编译或合并失败必须保留选中模式旧包'
            Assert ((Get-TestPublishSnapshot $otherPublished) -ceq $otherBefore) '失败不得影响未选模式'
            $expectedFrontendCalls = if ($failure -eq 'compile') { 0 } else { 1 }
            Assert (@($script:BuildEvents | Where-Object { $_ -eq 'frontend' }).Count -eq $expectedFrontendCalls) '失败时前端次数必须符合实际完成阶段'
        } finally { $script:FailFrameworkSecondPublish = $false; $script:ConflictingFrameworkDependency = $false }
    }

    Build 'All' 'Release' {
        Remove-TestFrontend
        $null = New-Item -ItemType Directory -Path 'web/dist/assets' -Force
        Set-Content -LiteralPath 'web/dist/index.html' -Value 'next frontend'
        Set-Content -LiteralPath 'web/dist/assets/next.js' -Value 'next frontend asset'
    }
    Assert (!(Test-Path -LiteralPath (Join-Path $published 'web/assets/first.js'))) '连续发布必须删除已移除的前端资源'
    Assert (Test-Path -LiteralPath (Join-Path $published 'web/assets/next.js')) '连续发布应包含新的前端资源'
    $selectedBefore = Get-TestPublishSnapshot $published
    try {
        Build 'All' 'Release' { Set-Content -LiteralPath 'web/dist/index.html' -Value 'unfinished frontend'; throw '模拟前端失败' }
        throw '前端回调失败时不得交付'
    } catch { Assert ($_.Exception.Message -eq '模拟前端失败') '前端回调失败应保留原始错误' }
    Assert ((Get-TestPublishSnapshot $published) -ceq $selectedBefore) '前端回调失败保留上次完整产物'
    try {
        Build 'All' 'Release' { Remove-TestFrontend; $null = New-Item -ItemType Directory -Path 'web/dist/assets' -Force }
        throw '前端缺少入口时不得交付'
    } catch { Assert ($_.Exception.Message -like '*前端构建缺少 web/dist/index.html*') '组合构建必须核验前端真实入口'
    }
    Assert ((Get-TestPublishSnapshot $published) -ceq $selectedBefore) '前端入口缺失保留上次完整产物'
    Assert ((Get-TestPublishSnapshot $otherPublished) -ceq $otherBefore) '连续发布和前端失败均不得修改未选模式'

    $script:NativeCommands.Clear()
    try { Build 'All'; throw '独立 C# 构建缺少前端时应拒绝' }
    catch { Assert ($_.Exception.Message -like '*桌面构建缺少前端产物*buildVue*buildAll*') '独立 C# 构建应说明前端准备步骤' }
    Assert ($script:NativeCommands.Count -eq 0) '缺少前端时不得开始独立 C# 发布'
    Set-Content -LiteralPath 'web/dist/index.html' -Value 'standalone frontend'
    Set-Content -LiteralPath 'web/dist/assets/standalone.js' -Value 'standalone frontend asset'
    Build 'All' 'Release'
    Assert ($script:NativeCommands.Count -eq 3) '独立 Release C# 构建也只发布选中的三个入口'
    foreach ($command in $script:NativeCommands) { Assert ($command.Arguments -notcontains '-p:IncludePbiFrontend=false') '独立 C# 发布应打包已准备的前端' }
    Assert (Test-Path -LiteralPath (Join-Path $published 'web/assets/standalone.js')) '独立 C# 发布应包含现有前端资源'
    Assert (!(Test-Path -LiteralPath (Join-Path $published 'web/assets/next.js'))) '独立发布不得残留旧前端资源'
    Assert-TestPublishedPackage $published 'Release' 'without-dotnet'
    $selectedBefore = Get-TestPublishSnapshot $published

    # Debug 两种显式模式复用同一开发目录, 切换时必须清理失效运行时文件与元数据.
    foreach ($mode in @('without-dotnet', 'with-dotnet')) {
        $script:NativeCommands.Clear()
        Build 'All' 'Debug' -DotNetMode $mode
        Assert ($script:NativeCommands.Count -eq 3) 'Debug 显式模式仅发布一套入口'
        Assert-TestPublishedPackage $debugPublished 'Debug' $mode
        Assert ((Get-TestPublishSnapshot $published) -ceq $selectedBefore -and (Get-TestPublishSnapshot $otherPublished) -ceq $otherBefore) 'Debug 模式切换不得影响任何 Release 目录'
    }
    Assert (@($script:Messages | Where-Object { $_.Message -eq 'C# 编译完成.' -and $_.Level -eq 'Success' }).Count -gt 0) 'C# 成功终态应以 Success 等级展示'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $sandbox 'artifacts/build') -Directory -Recurse | Where-Object Name -like '.pbi-utils-publish-*').Count -eq 0) '失败必须清理本轮 staging'
    Write-Output 'PASS C# 先编译、前端后打包、干净发布与失败保护契约'
}
finally {
    [Console]::OutputEncoding = $savedOutputEncoding
    Set-Location -LiteralPath $savedDirectory.Path
    # 目标为本次创建的唯一临时目录, 链接已在各场景 finally 中移除.
    if ([IO.Path]::GetFullPath($sandbox).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}
