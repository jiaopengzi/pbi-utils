# FilePath    : pbi-utils\scripts\InstallerMetadata.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 将已验证 Release 包的运行前提和微软 SDK 对应 Desktop Runtime 元数据固化为安装清单.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

<#
.SYNOPSIS 读取实际 runtimeconfig 框架要求, 返回两个框架共同适用的同系列最低版本.
.PARAMETER PublishRoot 已由发布模块完成文件清单与哈希验证的 Release 包目录.
.PARAMETER DeploymentMode 发布清单里的 self-contained 或 framework-dependent.
.OUTPUTS 三个入口中最高的实际最低版本; 缺框架、跨系列或特殊滚动策略直接报错.
#>
function Get-PbiInstallerMinimumRuntime([string]$PublishRoot, [string]$DeploymentMode) {
    $versions = New-Object 'Collections.Generic.List[Version]'
    $names = New-Object 'Collections.Generic.HashSet[string]'
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
        $options = (Get-Content -LiteralPath (Join-Path $PublishRoot ($entry + '.runtimeconfig.json')) -Raw -Encoding UTF8 | ConvertFrom-Json).runtimeOptions
        if ($options.PSObject.Properties['rollForward'] -and $options.rollForward -notin @('Minor', 'LatestPatch')) { throw 'INSTALLER_RUNTIME_ROLL_FORWARD_UNSUPPORTED' }
        if ($options.PSObject.Properties['applyPatches'] -and !$options.applyPatches) { throw 'INSTALLER_RUNTIME_PATCH_POLICY_UNSUPPORTED' }
        $frameworks = @()
        if ($DeploymentMode -eq 'self-contained') { $frameworks = @($options.includedFrameworks) }
        else {
            if ($options.PSObject.Properties['frameworks']) { $frameworks += @($options.frameworks) }
            if ($options.PSObject.Properties['framework']) { $frameworks += @($options.framework) }
        }
        if ($frameworks.Count -eq 0) { throw 'INSTALLER_RUNTIME_FRAMEWORK_MISSING' }
        foreach ($framework in $frameworks) {
            if ($framework.name -notin @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') -or $framework.version -notmatch '^\d+\.\d+\.\d+$') { throw 'INSTALLER_RUNTIME_FRAMEWORK_INVALID' }
            if ($framework.PSObject.Properties['rollForward'] -or $framework.PSObject.Properties['applyPatches']) { throw 'INSTALLER_RUNTIME_FRAMEWORK_POLICY_UNSUPPORTED' }
            $null = $names.Add($framework.name)
            $versions.Add([Version]$framework.version)
        }
    }
    if ($names.Count -ne 2) { throw 'INSTALLER_RUNTIME_FRAMEWORK_MISSING' }
    $minimum = @($versions | Sort-Object -Descending)[0]
    foreach ($version in $versions) {
        if ($version.Major -ne $minimum.Major -or $version.Minor -ne $minimum.Minor) { throw 'INSTALLER_RUNTIME_SERIES_MISMATCH' }
    }
    return $minimum.ToString()
}

<#
.SYNOPSIS 按 global.json 绑定的稳定 SDK 返回官方 .NET 发布元数据地址.
.PARAMETER RepositoryRoot 包含 global.json 的仓库根.
.OUTPUTS 该 SDK 主次版本的 releases.json 地址; SDK 非稳定版本时抛出 INSTALLER_STABLE_SDK_REQUIRED.
#>
function Get-PbiReleaseMetadataUri([string]$RepositoryRoot) {
    $sdkVersion = [string](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw -Encoding UTF8 | ConvertFrom-Json).sdk.version
    if ($sdkVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'INSTALLER_STABLE_SDK_REQUIRED' }
    $sdk = [Version]$sdkVersion
    return ('https://builds.dotnet.microsoft.com/dotnet/release-metadata/{0}.{1}/releases.json' -f $sdk.Major, $sdk.Minor)
}

<#
.SYNOPSIS 在后台线程下载官方发布元数据到本轮私有临时文件, release 打包时直接读取, 不阻塞门禁.
.PARAMETER RepositoryRoot 包含 global.json 的仓库根.
.OUTPUTS 预取上下文; 无法启动时返回 null, 打包阶段按原流程在线读取.
.NOTES 只下载公开元数据, 不缓存到下一轮; 结果在打包时仍经完整解析与校验.
#>
function Start-PbiReleaseMetadataPrefetch([string]$RepositoryRoot) {
    try { $uri = Get-PbiReleaseMetadataUri $RepositoryRoot }
    catch {
        # SDK 绑定无效时由打包阶段给出原有错误码, 预取不提前改变失败阶段.
        return $null
    }
    $directory = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-metadata-' + [Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($directory)
    $path = Join-Path $directory 'releases.json'
    $job = Start-ThreadJob -ArgumentList $uri, $path -ScriptBlock {
        param([string]$Uri, [string]$Path)
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Uri -OutFile $Path -TimeoutSec 60 -ErrorAction Stop
    }
    return [pscustomobject]@{ Uri = $uri; Directory = $directory; Path = $path; Job = $job }
}

<#
.SYNOPSIS 等待预取完成并返回可读取的元数据文件; 预取失败时返回 null, 由调用方在线读取并报告真实网络错误.
.PARAMETER Prefetch Start-PbiReleaseMetadataPrefetch 返回的上下文, 可为 null.
.PARAMETER RepositoryRoot 当前仓库根, 用于确认 SDK 绑定未在预取后改变.
.OUTPUTS 元数据文件路径或 null.
#>
function Receive-PbiReleaseMetadataPrefetch($Prefetch, [string]$RepositoryRoot) {
    if ($null -eq $Prefetch) { return $null }
    $null = Wait-Job -Job $Prefetch.Job
    if ($Prefetch.Job.State -ne 'Completed' -or ![IO.File]::Exists($Prefetch.Path)) {
        Write-Verbose '发布元数据预取未完成, 打包阶段改为在线读取.'
        return $null
    }
    # SDK 绑定在预取后改变时不使用旧系列元数据.
    try { if ((Get-PbiReleaseMetadataUri $RepositoryRoot) -cne $Prefetch.Uri) { return $null } }
    catch { return $null }
    return $Prefetch.Path
}

<#
.SYNOPSIS 结束并清理本轮元数据预取, 删除私有临时目录.
.PARAMETER Prefetch Start-PbiReleaseMetadataPrefetch 返回的上下文, 可为 null.
.NOTES 清理失败写诊断并保留目录, 不替换主流程的成功或失败结果.
#>
function Stop-PbiReleaseMetadataPrefetch($Prefetch) {
    if ($null -eq $Prefetch) { return }
    try {
        Remove-Job -Job $Prefetch.Job -Force
        if ([IO.Directory]::Exists($Prefetch.Directory)) { [IO.Directory]::Delete($Prefetch.Directory, $true) }
    }
    catch { [Console]::Error.WriteLine('发布元数据预取目录清理失败, 保留于: ' + $Prefetch.Directory + '. 原因: ' + $_.Exception.Message) }
}

<#
.SYNOPSIS 从 global.json 与官方发布元数据生成终端安装清单, 不通过 SDK 版本算术猜测运行时版本或 URL.
.PARAMETER RepositoryRoot 包含 global.json 的仓库根.
.PARAMETER PublishRoot 已完成发布验证的完整 Release win-x64 包, 必须包含三个入口.
.PARAMETER OutputPath 要写入的 schema 1 JSON; 仅在全部解析和验证成功后写入.
.PARAMETER ReleaseMetadataPath 可选的本轮预取元数据文件; 为空时在线读取官方元数据.
.OUTPUTS 与输出文件一致的清单对象. 网络、记录缺失/歧义、版本不兼容和写入错误直接传播.
.NOTES SDK 仅用于追溯编译基线; 终端下载并安装对应的 WindowsDesktop Runtime EXE, 不要求安装 SDK.
#>
function Export-PbiInstallerPrerequisites {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [string]$ReleaseMetadataPath = ''
    )
    $publish = Get-Content -LiteralPath (Join-Path $PublishRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($publish.schemaVersion -ne 3 -or $publish.configuration -ne 'Release' -or $publish.scope -ne 'All' -or
        $publish.runtime -ne 'win-x64' -or $publish.layout -ne 'shared' -or
        $publish.deploymentMode -notin @('self-contained', 'framework-dependent')) { throw 'INSTALLER_RELEASE_PACKAGE_REQUIRED' }
    $sdkVersion = [string](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw -Encoding UTF8 | ConvertFrom-Json).sdk.version
    if ($sdkVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'INSTALLER_STABLE_SDK_REQUIRED' }
    $minimumVersion = Get-PbiInstallerMinimumRuntime $PublishRoot $publish.deploymentMode
    $metadataUrl = Get-PbiReleaseMetadataUri $RepositoryRoot
    $metadata = if ($ReleaseMetadataPath) { Get-Content -LiteralPath $ReleaseMetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 60 }
    $sdkMatches = New-Object 'Collections.Generic.List[object]'
    foreach ($release in $metadata.releases) {
        $sdks = @()
        if ($release.PSObject.Properties['sdks']) { $sdks = @($release.sdks) }
        elseif ($release.PSObject.Properties['sdk']) { $sdks = @($release.sdk) }
        foreach ($candidate in $sdks) {
            if ($candidate.version -ceq $sdkVersion) { $sdkMatches.Add([pscustomobject]@{ Release = $release; Sdk = $candidate }) }
        }
    }
    if ($sdkMatches.Count -ne 1) { throw 'INSTALLER_SDK_METADATA_MISSING_OR_AMBIGUOUS' }
    $match = $sdkMatches[0]
    $desktop = $match.Release.windowsdesktop
    $runtimeVersion = [string]$desktop.version
    if ($runtimeVersion -notmatch '^\d+\.\d+\.\d+$' -or $match.Sdk.'runtime-version' -cne $runtimeVersion) { throw 'INSTALLER_SDK_RUNTIME_METADATA_MISMATCH' }
    $recommended = [Version]$runtimeVersion; $minimum = [Version]$minimumVersion
    if ($recommended.Major -ne $minimum.Major -or $recommended.Minor -ne $minimum.Minor -or $recommended -lt $minimum) { throw 'INSTALLER_RECOMMENDED_RUNTIME_INCOMPATIBLE' }
    $files = @($desktop.files | Where-Object { $_.rid -ceq 'win-x64' -and $_.name -ceq 'windowsdesktop-runtime-win-x64.exe' })
    if ($files.Count -ne 1) { throw 'INSTALLER_RUNTIME_DOWNLOAD_MISSING_OR_AMBIGUOUS' }
    $file = $files[0]; $uri = [Uri]$file.url
    if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or
        $uri.Host -notin @('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com', 'dotnetcli.azureedge.net') -or
        $uri.AbsolutePath -notlike '*.exe' -or $file.hash -notmatch '^[A-Fa-f0-9]{128}$') { throw 'INSTALLER_RUNTIME_DOWNLOAD_INVALID' }
    $mode = if ($publish.deploymentMode -eq 'self-contained') { 'with-dotnet' } else { 'without-dotnet' }
    $result = [pscustomobject][ordered]@{
        schemaVersion = 1
        dotNetMode = $mode
        dotnet = [pscustomobject][ordered]@{
            sdkVersion = $sdkVersion; runtimeVersion = $runtimeVersion; minimumVersion = $minimumVersion
            frameworks = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'); architecture = 'x64'
            url = [string]$file.url; sha512 = [string]$file.hash
        }
        webView2 = [pscustomobject]@{ url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' }
    }
    $fullOutput = [IO.Path]::GetFullPath($OutputPath)
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($fullOutput)) -Force
    $json = $result | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($fullOutput, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    return $result
}

Export-ModuleMember -Function Export-PbiInstallerPrerequisites, Get-PbiReleaseMetadataUri, Start-PbiReleaseMetadataPrefetch, Receive-PbiReleaseMetadataPrefetch, Stop-PbiReleaseMetadataPrefetch
