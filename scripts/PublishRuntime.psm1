# FilePath    : pbi-utils\scripts\PublishRuntime.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 核验发布入口的部署模式、框架声明和实际运行资产, 不运行产品或修改文件.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<# .SYNOPSIS 返回 FDD 禁止附带的核心运行时文件名, 无参数, 不访问文件系统. #>
function Get-PbiSelfContainedRuntimeFiles {
    return @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'clrjit.dll', 'mscordaccore.dll', 'mscordbi.dll', 'createdump.exe', 'mscorrc.dll')
}

<#
.SYNOPSIS 读取一个入口的部署描述并核验模式、框架和依赖边, 不改写文件.
.PARAMETER Root 已确认无链接的独立输出或最终共享目录.
.PARAMETER Entry 产品程序集名.
.PARAMETER DeploymentMode self-contained 或 framework-dependent, 无隐式旧模式.
.OUTPUTS 描述字典; 文件缺失、框架歧义、依赖闭包或部署模式错误时抛错.
#>
function Read-PbiDeploymentDescription([string]$Root, [string]$Entry, [string]$DeploymentMode) {
    if ($DeploymentMode -cnotin @('self-contained', 'framework-dependent')) { throw '发布部署模式无效' }
    foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
        if (![IO.File]::Exists((Join-Path $Root "$Entry.$suffix"))) { throw "独立发布缺少入口文件: $Entry.$suffix" }
    }
    $deps = Get-Content -LiteralPath (Join-Path $Root "$Entry.deps.json") -Raw | ConvertFrom-Json -AsHashtable
    $config = Get-Content -LiteralPath (Join-Path $Root "$Entry.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
    $targetName = $deps.runtimeTarget.name
    if ($targetName -isnot [string] -or !$targetName.EndsWith('/win-x64', [StringComparison]::Ordinal) -or !$deps.targets.Contains($targetName)) { throw '独立发布依赖目标无效' }
    $target = $deps.targets[$targetName]
    $projects = @($target.Keys | Where-Object { $_.StartsWith($Entry + '/', [StringComparison]::Ordinal) })
    if ($projects.Count -ne 1 -or !$deps.libraries.Contains($projects[0]) -or $deps.libraries[$projects[0]].type -cne 'project') { throw '独立发布产品依赖描述无效' }
    $options = $config.runtimeOptions
    if ($options.tfm -isnot [string] -or $options.tfm -cnotmatch '^net(?<version>\d+\.\d+)$' -or
        $targetName -cne ".NETCoreApp,Version=v$($Matches.version)/win-x64") { throw '发布框架目标缺失或与依赖目标不一致' }
    $targetVersion = [Version]$Matches.version
    if ($DeploymentMode -ceq 'self-contained') {
        if (!$options.Contains('includedFrameworks') -or $options.Contains('framework') -or $options.Contains('frameworks')) { throw '自包含发布框架声明无效' }
        $frameworks = @($options.includedFrameworks)
    } else {
        if ($options.Contains('includedFrameworks') -or $options.Contains('framework') -eq $options.Contains('frameworks')) { throw '框架依赖发布不能包含自包含或歧义框架声明' }
        $frameworks = if ($options.Contains('framework')) { @($options.framework) } else { @($options.frameworks) }
        foreach ($key in $deps.libraries.Keys) {
            if ($deps.libraries[$key].type -ceq 'runtimepack' -or $key.StartsWith('runtimepack.', [StringComparison]::OrdinalIgnoreCase)) { throw '框架依赖发布不能包含 runtimepack 运行时' }
        }
        foreach ($file in Get-PbiSelfContainedRuntimeFiles) {
            if ([IO.File]::Exists((Join-Path $Root $file))) { throw "框架依赖发布不能包含自包含运行时文件: $file" }
        }
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($framework in $frameworks) {
        $version = $null
        if ($framework.name -cnotin @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') -or !$names.Add($framework.name) -or
            ![Version]::TryParse($framework.version, [ref]$version) -or $version.Major -ne $targetVersion.Major -or $version.Minor -ne $targetVersion.Minor) { throw '发布框架名称或版本无效或重复' }
    }
    if (!$names.Contains('Microsoft.NETCore.App') -or $frameworks.Count -notin @(1, 2) -or
        ($Entry -cne 'pbi-utils' -and !$names.Contains('Microsoft.WindowsDesktop.App'))) { throw '发布框架集合缺失或不一致' }
    foreach ($library in $target.GetEnumerator()) {
        if (!$deps.libraries.Contains($library.Key)) { throw "发布依赖 library 缺失: $($library.Key)" }
        if ($DeploymentMode -ceq 'framework-dependent' -and $library.Key.StartsWith('runtimepack.', [StringComparison]::OrdinalIgnoreCase)) { throw '框架依赖发布不能包含 runtimepack 运行时' }
        if ($library.Value.Contains('dependencies')) {
            foreach ($dependency in $library.Value.dependencies.GetEnumerator()) {
                $key = "$($dependency.Key)/$($dependency.Value)"
                if (!$target.Contains($key) -or !$deps.libraries.Contains($key)) { throw "发布依赖闭包缺失: $key" }
            }
        }
    }
    return @{ Root = $Root; Entry = $Entry; Deps = $deps; Config = $config; Target = $targetName; Project = $projects[0]; Frameworks = $frameworks }
}

<#
.SYNOPSIS 核验每个托管和原生运行资产有唯一拥有者及真实文件.
.PARAMETER Description 已读取并核验依赖闭包的入口描述.
.OUTPUTS 按文件名索引的拥有者字典; 路径越界、重复拥有者或缺失文件抛错.
#>
function Get-PbiDeploymentAssetOwners([hashtable]$Description) {
    $owners = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $Description.Deps.targets[$Description.Target].GetEnumerator()) {
        foreach ($kind in @('runtime', 'native')) {
            if (!$library.Value.Contains($kind)) { continue }
            foreach ($asset in $library.Value[$kind].GetEnumerator()) {
                $name = [IO.Path]::GetFileName($asset.Key)
                if (!$name -or $asset.Key.Contains('\') -or $asset.Key.Contains(':') -or $asset.Key.StartsWith('/') -or $asset.Key.Split('/') -contains '..' -or
                    ![IO.File]::Exists((Join-Path $Description.Root $name))) { throw "运行时资产文件缺失或路径无效: $($asset.Key)" }
                if (!$owners.TryAdd($name, @{ Library = $library.Key; Kind = $kind; Path = $asset.Key; Metadata = $asset.Value })) { throw "运行时资产拥有者重复: $name" }
            }
        }
    }
    return ,$owners
}

<#
.SYNOPSIS 核验自包含入口的框架版本、runtimepack 和根依赖一致.
.PARAMETER Description 已读取的自包含入口描述.
.OUTPUTS 无; 框架或包声明缺失、版本不一致、非法资产类型时抛错.
#>
function Assert-PbiSelfContainedFrameworks([hashtable]$Description) {
    $target = $Description.Deps.targets[$Description.Target]
    foreach ($framework in $Description.Frameworks) {
        $id = "runtimepack.$($framework.name).Runtime.win-x64"
        $key = "$id/$($framework.version)"
        if (!$target.Contains($key) -or !$Description.Deps.libraries.Contains($key) -or $Description.Deps.libraries[$key].type -cne 'runtimepack' -or
            !$target[$Description.Project].Contains('dependencies') -or !$target[$Description.Project].dependencies.Contains($id) -or
            $target[$Description.Project].dependencies[$id] -cne $framework.version) { throw '自包含框架运行时包与配置不一致' }
        foreach ($section in $target[$key].Keys) { if ($section -cnotin @('runtime', 'native')) { throw '自包含框架运行时包包含未支持资产' } }
    }
}

<#
.SYNOPSIS 复核多个共享入口对同一个依赖和运行文件的拥有者、版本及元数据一致.
.PARAMETER Descriptions 按入口名索引的已校验描述.
.PARAMETER AssetOwners 按相同入口名索引的实际资产拥有者.
.OUTPUTS 无; 共享 library 或运行资产出现冲突时抛错, 不修改描述.
#>
function Assert-PbiSharedDeploymentDependencies($Descriptions, $AssetOwners) {
    $libraries = @{}; $assets = @{}
    foreach ($entry in $Descriptions.Keys) {
        foreach ($library in $Descriptions[$entry].Deps.libraries.GetEnumerator()) {
            $id = $library.Key.Substring(0, $library.Key.LastIndexOf('/'))
            if ($libraries.ContainsKey($id)) {
                $previous = $libraries[$id]
                if ($previous.Key -cne $library.Key -or $previous.Value.Count -ne $library.Value.Count) { throw "共享依赖来源或版本不一致: $id" }
                foreach ($field in $previous.Value.Keys) {
                    if (!$library.Value.Contains($field) -or $previous.Value[$field] -cne $library.Value[$field]) { throw "共享依赖元数据不一致: $id" }
                }
            } else { $libraries[$id] = $library }
        }
        foreach ($name in $AssetOwners[$entry].Keys) {
            $owner = $AssetOwners[$entry][$name]
            if ($assets.ContainsKey($name)) {
                $previous = $assets[$name]
                if ($previous.Library -cne $owner.Library -or $previous.Kind -cne $owner.Kind -or $previous.Path -cne $owner.Path -or
                    $previous.Metadata.Count -ne $owner.Metadata.Count) { throw "共享运行资产来源不一致: $name" }
                foreach ($field in $previous.Metadata.Keys) {
                    if (!$owner.Metadata.Contains($field) -or $previous.Metadata[$field] -cne $owner.Metadata[$field]) { throw "共享运行资产元数据不一致: $name" }
                }
            } else { $assets[$name] = $owner }
        }
    }
}

Export-ModuleMember -Function Get-PbiSelfContainedRuntimeFiles, Read-PbiDeploymentDescription, Get-PbiDeploymentAssetOwners, Assert-PbiSelfContainedFrameworks, Assert-PbiSharedDeploymentDependencies
