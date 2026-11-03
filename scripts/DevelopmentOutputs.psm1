# FilePath    : pbi-utils\scripts\DevelopmentOutputs.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 核验 framework-dependent 开发输出并将真实 CLI/Host 入口及依赖闭包装入现有 Desktop bin.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PublishArtifacts.psm1')

<# .SYNOPSIS 从中央目标框架取得固定 Desktop Debug 目录, 不创建替代发布目录. #>
function Get-PbiDevelopmentDirectory([string]$Root) {
    $framework = ([xml](Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw)).Project.PropertyGroup.TargetFramework
    if ($framework -isnot [string] -or $framework -notmatch '^net[0-9.]+-windows$') { throw '开发目标框架无效' }
    return Join-Path $Root "src/PbiUtils.Desktop/bin/Debug/$framework"
}

<#
.SYNOPSIS 只读预检已有开发文件的真实占用, 不终止用户实例或删除目录.
.PARAMETER Directory 现有 Desktop Debug 输出目录.
.OUTPUTS 无; 链接、文件占用或不可写时明确失败.
#>
function Assert-PbiDevelopmentDirectoryAvailable([string]$Directory) {
    Assert-PbiPublishPath $Directory
    if (![IO.Directory]::Exists($Directory)) { return }
    foreach ($file in (Get-PbiPublishFiles $Directory)) {
        if ($file.Extension -notin @('.exe', '.dll', '.json', '.pdb')) { continue }
        try { $handle = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $handle.Dispose() }
        catch { throw '开发输出被占用或不可写, 请正常关闭使用该目录的开发实例后重试' }
    }
}

<#
.SYNOPSIS 预检三个真实 Debug 入口的依赖版本、资产与内容后仅复制必要入口及闭包, 不改 runtimeconfig.
.PARAMETER Root 当前仓库根, 使用现有 Desktop/bin/Debug/<中央TFM> 作为唯一目标.
.OUTPUTS 真实 Desktop.exe 绝对路径; 源缺失、自包含输入、框架/依赖/内容冲突、链接或占用时抛错.
#>
function Set-PbiDevelopmentOutputs([string]$Root) {
    $destination = [IO.Path]::GetFullPath((Get-PbiDevelopmentDirectory $Root))
    Assert-PbiDevelopmentDirectoryAvailable $destination
    $framework = [IO.Path]::GetFileName($destination)
    $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $packages = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $owners = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $frameworks = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $targetName = $null; $tfm = $null
    $entryFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($pair in @(@('Cli', 'pbi-utils'), @('Host', 'PbiUtils.Host'), @('Desktop', 'PbiUtils.Desktop'))) {
        $directory = Join-Path $Root "src/PbiUtils.$($pair[0])/bin/Debug/$framework"
        Assert-PbiPublishPath $directory
        $entry = $pair[1]
        $assets = [Collections.Generic.List[string]]::new()
        foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
            $name = "$entry.$suffix"
            if (![IO.File]::Exists((Join-Path $directory $name))) { throw "开发输出缺少入口: $name" }
            $assets.Add($name)
            if ($pair[0] -ne 'Desktop') { $null = $entryFiles.Add($name) }
        }
        $deps = Get-Content -LiteralPath (Join-Path $directory "$entry.deps.json") -Raw | ConvertFrom-Json -AsHashtable
        $config = Get-Content -LiteralPath (Join-Path $directory "$entry.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
        $options = $config.runtimeOptions
        if ($options.Contains('includedFrameworks') -or !$options.tfm -or !$deps.runtimeTarget.name -or $deps.runtimeTarget.name.Contains('/') -or !$deps.targets.Contains($deps.runtimeTarget.name)) { throw '开发输出必须是 framework-dependent 非 RID 构建' }
        if ($null -ne $targetName -and ($targetName -cne $deps.runtimeTarget.name -or $tfm -cne $options.tfm)) { throw '开发输出运行时目标不一致' }
        $targetName = $deps.runtimeTarget.name; $tfm = $options.tfm
        $references = if ($options.Contains('frameworks')) { @($options.frameworks) } elseif ($options.Contains('framework')) { @($options.framework) } else { @() }
        if (!$references.Count) { throw '开发输出缺少框架引用' }
        foreach ($reference in $references) {
            if ($frameworks.ContainsKey($reference.name) -and $frameworks[$reference.name] -cne $reference.version) { throw '开发输出框架版本不一致' }
            $frameworks[$reference.name] = $reference.version
        }
        foreach ($library in $deps.targets[$targetName].GetEnumerator()) {
            if (!$deps.libraries.Contains($library.Key)) { throw '开发输出依赖描述不完整' }
            $index = $library.Key.LastIndexOf('/')
            if ($index -lt 1) { throw '开发输出依赖身份无效' }
            $id = $library.Key.Substring(0, $index)
            $metadata = $deps.libraries[$library.Key]
            $signature = (@($library.Key) + @(@('type', 'sha512', 'path', 'hashPath') | ForEach-Object { if ($metadata.Contains($_)) { [string]$metadata[$_] } else { '' } })) -join '|'
            if ($packages.ContainsKey($id) -and $packages[$id] -cne $signature) { throw "开发共享依赖版本或来源冲突: $id" }
            $packages[$id] = $signature
            if ($library.Value.Contains('dependencies')) {
                foreach ($dependency in $library.Value.dependencies.GetEnumerator()) {
                    if (!$deps.targets[$targetName].Contains("$($dependency.Key)/$($dependency.Value)")) { throw "开发运行时依赖闭包缺失: $($dependency.Key)" }
                }
            }
            foreach ($kind in @('runtime', 'native', 'resources', 'runtimeTargets')) {
                if (!$library.Value.Contains($kind)) { continue }
                foreach ($asset in $library.Value[$kind].GetEnumerator()) {
                    $path = $asset.Key
                    if ($path.Contains('\') -or $path.Contains(':') -or $path.Split('/') -contains '..' -or $path.Split('/') -contains '.' -or [IO.Path]::IsPathRooted($path)) { throw '开发依赖资产路径无效' }
                    $name = [IO.Path]::GetFileName($path)
                    if ($name -ceq '_._') { continue }
                    if ($kind -eq 'runtimeTargets') {
                        if ($asset.Value.rid -notin @('win', 'win-x64')) { continue }
                        if ($asset.Value.assetType -notin @('runtime', 'native')) { throw '开发依赖运行时资产类型无效' }
                        $relative = $path
                    } elseif ($kind -eq 'resources') {
                        $locale = $asset.Value.locale
                        if ($locale -isnot [string] -or $locale -notmatch '^[a-zA-Z0-9-]+$') { throw '开发依赖资源语言无效' }
                        $relative = "$locale/$name"
                    } else { $relative = $name }
                    if ($owners.ContainsKey($relative) -and $owners[$relative] -cne $library.Key) { throw "开发依赖资产拥有者冲突: $relative" }
                    $owners[$relative] = $library.Key
                    $assets.Add($relative)
                }
            }
        }
        if ([IO.File]::Exists((Join-Path $directory "$entry.pdb"))) {
            $assets.Add("$entry.pdb")
            if ($pair[0] -ne 'Desktop') { $null = $entryFiles.Add("$entry.pdb") }
        }
        foreach ($relative in ($assets | Select-Object -Unique)) {
            $source = Join-Path $directory $relative
            Assert-PbiPublishPath $source
            if (![IO.File]::Exists($source)) { throw "开发依赖资产文件缺失: $relative" }
            $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            if ($files.ContainsKey($relative) -and $files[$relative].Hash -cne $hash) { throw "开发共享依赖内容冲突: $relative" }
            $files[$relative] = @{ Source = $source; Hash = $hash }
        }
    }
    # 所有依赖与目标先完成校验, 再写入缺少的资产; 已有共享依赖不接受静默替换.
    foreach ($relative in $files.Keys) {
        $target = Join-Path $destination $relative
        Assert-PbiPublishPath $target
        if ([IO.Directory]::Exists($target)) { throw "开发资产文件与目录冲突: $relative" }
        for ($ancestor = [IO.Path]::GetDirectoryName($target); $ancestor -ne $destination; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
            if ([IO.File]::Exists($ancestor)) { throw "开发资产目录与文件冲突: $relative" }
        }
        if ([IO.File]::Exists($target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $files[$relative].Hash -and !$entryFiles.Contains($relative)) { throw "开发目标依赖内容冲突: $relative" }
    }
    foreach ($relative in $files.Keys) {
        $target = Join-Path $destination $relative
        if ([IO.File]::Exists($target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ceq $files[$relative].Hash) { continue }
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $files[$relative].Source -Destination $target -ErrorAction Stop
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $files[$relative].Hash) { throw "开发复制后内容发生变化: $relative" }
    }
    return Join-Path $destination 'PbiUtils.Desktop.exe'
}

Export-ModuleMember -Function *-PbiDevelopment*
