# FilePath    : pbi-utils\scripts\SharedPublish.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 预检 CLI、专用后台与桌面发布依赖, 按显式部署模式聚合共享目录并统一框架闭包.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PublishArtifacts.psm1')
Import-Module (Join-Path $PSScriptRoot 'PublishRuntime.psm1')

<#
.SYNOPSIS 获取与自包含配置和根依赖一致的框架运行时包.
.PARAMETER Description 已读取的独立发布描述.
.PARAMETER Name 框架名称, 不包含运行时包前缀.
.OUTPUTS 框架版本、包键与资产节点; 重复、缺失、目标不一致时抛错.
#>
function Get-PbiSharedFramework([hashtable]$Description, [string]$Name) {
    $frameworks = @($Description.Config.runtimeOptions.includedFrameworks | Where-Object name -CEQ $Name)
    if ($frameworks.Count -ne 1) { throw "自包含框架缺失或重复: $Name" }
    $version = $null
    if (![Version]::TryParse($frameworks[0].version, [ref]$version)) { throw "自包含框架版本无效: $Name" }
    $id = "runtimepack.$Name.Runtime.win-x64"
    $key = "$id/$($frameworks[0].version)"
    $target = $Description.Deps.targets[$Description.Target]
    if (!$target.Contains($key) -or !$Description.Deps.libraries.Contains($key) -or $Description.Deps.libraries[$key].type -ne 'runtimepack' -or
        !$target[$Description.Project].dependencies.Contains($id) -or $target[$Description.Project].dependencies[$id] -cne $frameworks[0].version) { throw "框架运行时包与配置不一致: $Name" }
    foreach ($section in $target[$key].Keys) { if ($section -notin @('runtime', 'native')) { throw "框架运行时包包含未支持资产: $Name/$section" } }
    return @{ Id = $id; Key = $key; Version = $version; Node = $target[$key]; Library = $Description.Deps.libraries[$key] }
}

<#
.SYNOPSIS 仅允许已知 NETCore facade 或单资产 ProtectedData 包由 WindowsDesktop 框架接管.
.PARAMETER Name 发生内容或资产来源变化的文件名.
.PARAMETER Source CLI 原资产拥有者.
.PARAMETER Destination 业务后台 Windows 框架权威资产拥有者.
.PARAMETER Cli CLI 描述, 用于核验包形状.
.PARAMETER Core CLI 的 NETCore 框架信息.
.PARAMETER Windows 业务后台的 WindowsDesktop 框架信息.
.OUTPUTS 无; 未知替代、来源错误、包形状变化或程序集/文件版本回退时抛错.
#>
function Assert-PbiSharedReplacement([string]$Name, $Source, $Destination, [hashtable]$Cli, $Core, $Windows) {
    if (!$Source -or !$Destination -or $Source.Kind -ne 'runtime' -or $Destination.Kind -ne 'runtime' -or $Destination.Library -cne $Windows.Key) { throw "共享依赖内容冲突或来源不允许替代: $Name" }
    if ($Name -ceq 'System.Security.Cryptography.ProtectedData.dll') {
        $library = $Cli.Deps.libraries[$Source.Library]
        $node = $Cli.Deps.targets[$Cli.Target][$Source.Library]
        if ($Source.Library -cnotmatch '^System\.Security\.Cryptography\.ProtectedData/[^/]+$' -or $library.type -ne 'package' -or
            $node.Count -ne 1 -or !$node.Contains('runtime') -or $node.runtime.Count -ne 1) { throw 'ProtectedData 包形状不允许框架替代' }
    } elseif ($Name -cnotin @('Microsoft.VisualBasic.dll', 'System.Drawing.dll', 'WindowsBase.dll') -or $Source.Library -cne $Core.Key) {
        throw "共享依赖内容冲突: $Name"
    }
    foreach ($field in @('assemblyVersion', 'fileVersion')) {
        $before = $null; $after = $null
        if (!$Source.Metadata.Contains($field) -or !$Destination.Metadata.Contains($field) -or
            ![Version]::TryParse($Source.Metadata[$field], [ref]$before) -or ![Version]::TryParse($Destination.Metadata[$field], [ref]$after) -or $after -lt $before) { throw "共享框架资产版本无效或回退: $Name/$field" }
    }
}

<#
.SYNOPSIS 在内存统一一个入口的框架依赖闭包, 保留产品配置及独立编译输出.
.PARAMETER Cli 待规范的 CLI 或 WPF 后台独立发布描述.
.PARAMETER Desktop 本范围框架权威, Core 为 WPF 后台, All 为完整桌面.
.PARAMETER CliOwners 待规范入口的实际独立资产索引.
.PARAMETER DesktopOwners 本范围框架权威的实际独立资产索引.
.OUTPUTS 无; 框架目标、版本或运行时资产替代不一致时抛错.
#>
function Set-PbiSharedRuntime([hashtable]$Cli, [hashtable]$Desktop, $CliOwners, $DesktopOwners) {
    if ($Cli.Target -cne $Desktop.Target -or $Cli.Config.runtimeOptions.tfm -cne $Desktop.Config.runtimeOptions.tfm) { throw '独立发布运行时目标不一致' }
    $core = Get-PbiSharedFramework $Cli 'Microsoft.NETCore.App'
    $desktopCore = Get-PbiSharedFramework $Desktop 'Microsoft.NETCore.App'
    $windows = Get-PbiSharedFramework $Desktop 'Microsoft.WindowsDesktop.App'
    $sourceFrameworkCount = @($Cli.Config.runtimeOptions.includedFrameworks).Count
    if ($core.Key -cne $desktopCore.Key -or $core.Version -ne $windows.Version -or
        $sourceFrameworkCount -notin @(1, 2) -or @($Desktop.Config.runtimeOptions.includedFrameworks).Count -ne 2) { throw '独立发布核心运行时版本或框架集合不一致' }
    if ($sourceFrameworkCount -eq 2 -and (Get-PbiSharedFramework $Cli 'Microsoft.WindowsDesktop.App').Key -cne $windows.Key) { throw '独立发布 Windows 框架版本不一致' }
    $target = $Cli.Deps.targets[$Cli.Target]
    $removed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $CliOwners.Keys) {
        $source = $CliOwners[$name]
        if (!$DesktopOwners.ContainsKey($name)) {
            if ($source.Library -ceq $core.Key) { throw "权威框架闭包缺少入口运行时资产: $name" }
            continue
        }
        $destination = $DesktopOwners[$name]
        if ($source.Library -ceq $destination.Library) {
            if ($source.Kind -cne $destination.Kind -or $source.Metadata.Count -ne $destination.Metadata.Count) { throw "共享资产元数据不一致: $name" }
            foreach ($field in $source.Metadata.Keys) {
                if (!$destination.Metadata.Contains($field) -or $source.Metadata[$field] -cne $destination.Metadata[$field]) { throw "共享资产版本不一致或回退: $name/$field" }
            }
            continue
        }
        if ($destination.Library -ceq $windows.Key) {
            Assert-PbiSharedReplacement $name $source $destination $Cli $core $windows
            if ($source.Library -cne $core.Key) { $null = $removed.Add($source.Library) }
        } else { throw "共享资产来源不一致: $name" }
    }
    foreach ($key in $removed) {
        $null = $target.Remove($key); $null = $Cli.Deps.libraries.Remove($key)
        $id = $key.Substring(0, $key.LastIndexOf('/'))
        foreach ($node in $target.Values) { if ($node.Contains('dependencies')) { $null = $node.dependencies.Remove($id) } }
    }
    foreach ($framework in @($desktopCore, $windows)) {
        $target[$framework.Key] = $framework.Node
        $Cli.Deps.libraries[$framework.Key] = $framework.Library
        $target[$Cli.Project].dependencies[$framework.Id] = $framework.Version.ToString()
    }
    $Cli.Config.runtimeOptions.includedFrameworks = $Desktop.Config.runtimeOptions.includedFrameworks
}

<#
.SYNOPSIS 核验 FDD 共享依赖身份并将各入口框架声明统一为范围权威, 不改动源文件.
.PARAMETER Descriptions 各独立入口描述.
.PARAMETER Entries 本次范围入口名.
.PARAMETER AssetOwners 各入口真实资产拥有者.
.PARAMETER Authority 提供两个外部框架的后台或桌面入口.
.OUTPUTS 无; 框架版本、共享库来源或资产元数据冲突时抛错.
#>
function Set-PbiFrameworkDependentRuntime($Descriptions, [string[]]$Entries, $AssetOwners, [hashtable]$Authority) {
    Assert-PbiSharedDeploymentDependencies $Descriptions $AssetOwners
    foreach ($entry in $Entries) {
        $description = $Descriptions[$entry]
        if ($description.Target -cne $Authority.Target -or $description.Config.runtimeOptions.tfm -cne $Authority.Config.runtimeOptions.tfm) { throw '独立发布运行时目标不一致' }
        foreach ($framework in $description.Frameworks) {
            $match = @($Authority.Frameworks | Where-Object name -CEQ $framework.name)
            if ($match.Count -ne 1 -or $match[0].version -cne $framework.version) { throw '框架依赖发布框架版本不一致' }
        }
        $null = $description.Config.runtimeOptions.Remove('framework')
        $description.Config.runtimeOptions.frameworks = $Authority.Frameworks
    }
}

<#
.SYNOPSIS 预检后将独立编译输出合并为单目录完整包, 不修改输入.
.PARAMETER Compiled 包含独立 cli/host 及可选 desktop publish 输出的本任务临时根目录.
.PARAMETER Staging 已存在且为空的 .pbi-utils-publish-GUID 目录.
.PARAMETER Scope Core 包含 CLI 与无 UI 业务后台; All 另包含桌面, 两种范围都统一包内 CLI 框架闭包.
.PARAMETER DeploymentMode 显式 self-contained 或 framework-dependent, 决定框架闭包与运行时文件约束.
.OUTPUTS 无; 路径越界、链接、依赖冲突、版本或文件错误时抛错, 不交付正式发布目录.
#>
function Merge-PbiPublishedArtifacts([string]$Compiled, [string]$Staging, [string]$Scope, [string]$DeploymentMode) {
    $Compiled = [IO.Path]::GetFullPath($Compiled); $Staging = [IO.Path]::GetFullPath($Staging)
    Assert-PbiPublishPath $Compiled; Assert-PbiPublishPath $Staging
    if ($Scope -cnotin @('Core', 'All') -or $DeploymentMode -cnotin @('self-contained', 'framework-dependent') -or [IO.Path]::GetFileName($Staging) -notmatch '^\.pbi-utils-publish-[0-9a-f]{32}$' -or
        ![IO.Directory]::Exists($Staging) -or @(Get-ChildItem -LiteralPath $Staging -Force -ErrorAction Stop).Count -ne 0 -or
        $Staging.StartsWith($Compiled.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $Compiled.StartsWith($Staging.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $Compiled -eq $Staging) { throw '共享发布范围或空 staging 路径无效' }
    $entries = if ($Scope -eq 'All') { @('cli', 'host', 'desktop') } else { @('cli', 'host') }
    $descriptions = @{}; $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $collisions = [Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        $root = Join-Path $Compiled $entry
        $sourceFiles = @(Get-PbiPublishFiles $root)
        $name = switch ($entry) { 'cli' { 'pbi-utils' }; 'host' { 'PbiUtils.Host' }; 'desktop' { 'PbiUtils.Desktop' } }
        $descriptions[$entry] = Read-PbiDeploymentDescription $root $name $DeploymentMode
        foreach ($file in $sourceFiles) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ($files.ContainsKey($relative) -and $files[$relative].Hash -cne $hash) { $collisions.Add(@{ Name = $relative; Source = $files[$relative].Entry; Destination = $entry }) }
            $files[$relative] = @{ Source = $file.FullName; Hash = $hash; Entry = $entry }
        }
    }
    $assetOwners = @{}
    foreach ($entry in $entries) { $assetOwners[$entry] = Get-PbiDeploymentAssetOwners $descriptions[$entry] }
    $authorityEntry = if ($Scope -eq 'All') { 'desktop' } else { 'host' }
    $authority = $descriptions[$authorityEntry]
    if ($DeploymentMode -ceq 'framework-dependent') {
        if ($collisions.Count) { throw "框架依赖共享文件内容冲突: $($collisions[0].Name)" }
        foreach ($file in $files.Keys) {
            if ([IO.Path]::GetFileName($file) -iin (Get-PbiSelfContainedRuntimeFiles)) { throw "框架依赖发布不能包含自包含运行时文件: $file" }
        }
        Set-PbiFrameworkDependentRuntime $descriptions $entries $assetOwners $authority
    } else {
        $hostDescription = $descriptions.host
        $hostCore = Get-PbiSharedFramework $hostDescription 'Microsoft.NETCore.App'
        $hostWindows = Get-PbiSharedFramework $hostDescription 'Microsoft.WindowsDesktop.App'
        if ($hostCore.Version -ne $hostWindows.Version -or @($hostDescription.Config.runtimeOptions.includedFrameworks).Count -ne 2) { throw '业务后台框架闭包不一致' }
        # WPF 后台和 WinForms 桌面虽使用同一运行时包, 实际拥有的 facade/资产集合不同.
        # Core 采用后台 WPF 闭包, All 采用完整桌面闭包; 每次内容差异仍按真实前后拥有者严格验证.
        foreach ($collision in $collisions) {
            $name = $collision.Name
            $source = $descriptions[$collision.Source]
            $destination = $descriptions[$collision.Destination]
            $sourceOwners = $assetOwners[$collision.Source]
            $destinationOwners = $assetOwners[$collision.Destination]
            if (!$sourceOwners.ContainsKey($name) -or !$destinationOwners.ContainsKey($name) -or
                $source.Target -cne $destination.Target -or $source.Config.runtimeOptions.tfm -cne $destination.Config.runtimeOptions.tfm) { throw "共享依赖内容冲突: $name" }
            $core = Get-PbiSharedFramework $source 'Microsoft.NETCore.App'
            $destinationCore = Get-PbiSharedFramework $destination 'Microsoft.NETCore.App'
            $windows = Get-PbiSharedFramework $destination 'Microsoft.WindowsDesktop.App'
            if ($core.Key -cne $destinationCore.Key -or $core.Version -ne $windows.Version) { throw '共享框架运行时版本不一致' }
            Assert-PbiSharedReplacement $name $sourceOwners[$name] $destinationOwners[$name] $source $core $windows
        }
        foreach ($entry in $entries) {
            if ($entry -ceq $authorityEntry) { continue }
            Set-PbiSharedRuntime $descriptions[$entry] $authority $assetOwners[$entry] $assetOwners[$authorityEntry]
        }
    }
    foreach ($description in $descriptions.Values) {
        # 只检查描述拥有者唯一性; 文件存在性已按输入和完整聚合集合预检, staging 此时仍为空.
        $owners = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($node in $description.Deps.targets[$description.Target].Values) {
            foreach ($kind in @('runtime', 'native')) {
                if (!$node.Contains($kind)) { continue }
                foreach ($asset in $node[$kind].Keys) {
                    $name = [IO.Path]::GetFileName($asset)
                    if (!$owners.Add($name) -or !$files.ContainsKey($name)) { throw "聚合运行时资产重复或缺失: $name" }
                }
            }
        }
    }
    foreach ($relative in $files.Keys) {
        $parent = [IO.Path]::GetDirectoryName($relative).Replace('\', '/')
        while ($parent) {
            if ($files.ContainsKey($parent)) { throw "聚合文件与目录冲突: $parent" }
            $parent = [IO.Path]::GetDirectoryName($parent).Replace('\', '/')
        }
    }
    foreach ($relative in $files.Keys) {
        $target = Join-Path $Staging $relative
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $files[$relative].Source -Destination $target -ErrorAction Stop
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $files[$relative].Hash) { throw "聚合复制后文件发生变化: $relative" }
    }
    foreach ($entry in $entries) {
        if ($DeploymentMode -ceq 'self-contained' -and $entry -ceq $authorityEntry) { continue }
        $description = $descriptions[$entry]
        foreach ($item in @(@('deps.json', $description.Deps), @('runtimeconfig.json', $description.Config))) {
            $json = ($item[1] | ConvertTo-Json -Depth 100).Replace("`r`n", "`n") + "`n"
            [IO.File]::WriteAllText((Join-Path $Staging "$($description.Entry).$($item[0])"), $json, [Text.UTF8Encoding]::new($false))
        }
    }
}

Export-ModuleMember -Function Merge-PbiPublishedArtifacts
