# FilePath    : pbi-utils\scripts\build.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 按配置和显式 .NET 模式编译 C#, 装入本轮前端后校验并事务交付单一目录, 不隐式还原.

<# .SYNOPSIS 构建 Vue、C# 或全部产物, 每轮仅生成 DotNetMode 选中的部署模式. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    exit (Invoke-PbiEntry -Arguments $args -Action buildAll)
}
$options = $PbiInvocation.Options
$action = $PbiInvocation.Action
if (Invoke-PbiGroup 'build' $action $options) { return }
if ($action -eq 'buildVue') { Invoke-PbiFrontendCommand $action; return }
$afterCompile = if ($PbiInvocation.ContainsKey('AfterCompile')) { $PbiInvocation.AfterCompile } else { $null }
if (!$afterCompile -and $options.Scope -eq 'All' -and !(Test-Path -LiteralPath 'web/dist/index.html' -PathType Leaf)) {
    Stop-Pbi 3 '桌面构建缺少前端产物, 请先运行 -Choice buildVue 或使用 -Choice buildAll'
}
$entries = @(@('src/PbiUtils.Cli', 'cli'), @('src/PbiUtils.Host', 'host'))
if ($options.Scope -eq 'All') { $entries += ,@('src/PbiUtils.Desktop', 'desktop') }
Import-Module (Join-Path $PSScriptRoot 'PublishArtifacts.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SharedPublish.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ThirdPartyLicenses.psm1') -Force
$mode = if ($options.DotNetMode -eq 'with-dotnet') { 'self-contained' } else { 'framework-dependent' }
$label = if ($mode -eq 'self-contained') { '包含 .NET 运行时' } else { '不包含 .NET 运行时' }
$relativeDestination = if ($options.Configuration -eq 'Release') { "artifacts/build/release/win-x64/$($options.DotNetMode)" } else { 'artifacts/build/debug/win-x64' }
$destination = [IO.Path]::GetFullPath((Join-Path (Get-Location) $relativeDestination))
$parent = [IO.Path]::GetDirectoryName($destination)
Assert-PbiPublishPath $parent
$null = New-Item -ItemType Directory -Path $parent -Force
$staging = Join-Path $parent ('.pbi-utils-publish-' + [Guid]::NewGuid().ToString('N'))
$compiled = Join-Path $parent ('.pbi-utils-publish-' + [Guid]::NewGuid().ToString('N'))
$operationFailure = $null
try {
    $null = New-Item -ItemType Directory -Path $staging
    $null = New-Item -ItemType Directory -Path $compiled
    $stamp = [DateTime]::UtcNow.ToString('O')
    foreach ($entry in $entries) {
        $output = Join-Path $compiled $entry[1]
        $selfContained = if ($mode -eq 'self-contained') { 'true' } else { 'false' }
        # 由 SDK 生成选中模式的真实依赖闭包, 不删除运行时文件伪造框架依赖包.
        $publishArguments = @('publish', $entry[0], '--no-restore', '--nologo', '-c', $options.Configuration, '-r', 'win-x64', '--self-contained', $selfContained, '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishAot=false', '-o', $output)
        if ($afterCompile) { $publishArguments += '-p:IncludePbiFrontend=false' }
        Invoke-PbiNative dotnet $publishArguments
    }
    Write-PbiMessage 'C# 编译完成.' 'Success'
    # 三个入口完成后才执行本轮前端, 前端失败时不进入目录交付.
    if ($afterCompile) {
        & $afterCompile
    }
    $version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
    # 固化入口已核验的 SDK 基线, 后续安装包可拒绝来自其他工具链的旧产物.
    $sdkVersion = [string](Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    Write-PbiMessage '开始打包: 校验选中模式的完整文件清单后交付.'
    # 独立发布输入不改写, 在选中部署包中统一框架闭包并共享相同依赖.
    Merge-PbiPublishedArtifacts $compiled $staging $options.Scope $mode
    if ($afterCompile -and $options.Scope -eq 'All') { Set-PbiPublishedFrontend ([IO.Path]::GetFullPath((Join-Path (Get-Location) 'web/dist'))) $staging }
    # 基于本轮发布依赖离线收集完整许可, 缺失许可时在 manifest 与目录交付前终止.
    $null = Export-PbiLicenseBundle -RepositoryRoot (Get-Location).Path -PublishRoot $staging -Scope $options.Scope
    $independentBytes = [long](Get-PbiPublishFiles $compiled | Measure-Object Length -Sum).Sum
    if ($afterCompile -and $options.Scope -eq 'All') { $independentBytes += [long](Get-PbiPublishFiles ([IO.Path]::GetFullPath((Join-Path (Get-Location) 'web/dist'))) | Measure-Object Length -Sum).Sum }
    $sharedBytes = [long](Get-PbiPublishFiles $staging | Measure-Object Length -Sum).Sum
    Write-PbiMessage "共享打包体积 ($label): 独立输出 $independentBytes 字节, 共享目录 $sharedBytes 字节 (不含清单)."
    $files = @(Get-PbiPublishFiles $staging | Sort-Object FullName | ForEach-Object {
        @{ file = [IO.Path]::GetRelativePath($staging, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ schemaVersion = 3; layout = 'shared'; deploymentMode = $mode; version = $version; sdkVersion = $sdkVersion; builtAtUtc = $stamp; runtime = 'win-x64'; scope = $options.Scope; configuration = $options.Configuration; files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding utf8
    $null = Assert-PbiPublishedArtifacts $staging
    $publishedBytes = [long](Get-PbiPublishFiles $staging | Measure-Object Length -Sum).Sum
    Set-PbiPublishedArtifacts $staging $destination
}
catch {
    $operationFailure = $_
    throw
}
finally {
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    foreach ($temporary in @($staging, $compiled)) {
        if (![IO.Directory]::Exists($temporary)) { continue }
        try { Remove-PbiPublishTemporary $temporary $parent }
        catch { $cleanupFailures.Add("本次发布临时目录清理失败, 保留于: $temporary. 原因: $($_.Exception.Message)") }
    }
    if ($cleanupFailures.Count) {
        $cleanupMessage = $cleanupFailures -join [Environment]::NewLine
        if ($null -eq $operationFailure) { throw "发布文件已交付, 但$cleanupMessage" }
        # 所有本轮临时目录均尝试清理, 主操作失败时不替换原始阶段和退出码.
        [Console]::Error.WriteLine($cleanupMessage)
    }
}
$size = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F2} MiB ({1} 字节)', ($publishedBytes / 1MB), $publishedBytes)
Write-PbiMessage "发布产物 ($label): $destination; Scope=$($options.Scope); Configuration=$($options.Configuration); $size" 'Success'
