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
.SYNOPSIS 从 global.json 与官方发布元数据生成终端安装清单, 不通过 SDK 版本算术猜测运行时版本或 URL.
.PARAMETER RepositoryRoot 包含 global.json 的仓库根.
.PARAMETER PublishRoot 已完成发布验证的完整 Release win-x64 包, 必须包含三个入口.
.PARAMETER OutputPath 要写入的 schema 1 JSON; 仅在全部解析和验证成功后写入.
.OUTPUTS 与输出文件一致的清单对象. 网络、记录缺失/歧义、版本不兼容和写入错误直接传播.
.NOTES SDK 仅用于追溯编译基线; 终端下载并安装对应的 WindowsDesktop Runtime EXE, 不要求安装 SDK.
#>
function Export-PbiInstallerPrerequisites {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )
    $publish = Get-Content -LiteralPath (Join-Path $PublishRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($publish.schemaVersion -ne 3 -or $publish.configuration -ne 'Release' -or $publish.scope -ne 'All' -or
        $publish.runtime -ne 'win-x64' -or $publish.layout -ne 'shared' -or
        $publish.deploymentMode -notin @('self-contained', 'framework-dependent')) { throw 'INSTALLER_RELEASE_PACKAGE_REQUIRED' }
    $sdkVersion = [string](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw -Encoding UTF8 | ConvertFrom-Json).sdk.version
    if ($sdkVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'INSTALLER_STABLE_SDK_REQUIRED' }
    $minimumVersion = Get-PbiInstallerMinimumRuntime $PublishRoot $publish.deploymentMode
    $sdk = [Version]$sdkVersion
    $metadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/{0}.{1}/releases.json' -f $sdk.Major, $sdk.Minor
    $metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 60
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

Export-ModuleMember -Function Export-PbiInstallerPrerequisites
