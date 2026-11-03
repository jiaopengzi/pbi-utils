# FilePath    : pbi-utils\scripts\ThirdPartyLicenseSources.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 解析发布依赖和离线许可来源, 统一路径边界、版权和固定版本核验.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<# .SYNOPSIS 核验路径位于根目录内且各级不是链接; 返回规范绝对路径, 不存在或越界抛错. #>
function Get-PbiLicenseSafePath([string]$Root, [string]$Path) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $base -and !$full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "License path escapes its root: $Path" }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "License path must not contain a link: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

<# .SYNOPSIS 从目录递归取得许可/NOTICE/COPYING/版权文档; 跳过子依赖目录, 链接或读取失败直接抛错. #>
function Get-PbiLicenseDocuments([string]$Directory) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push((Get-PbiLicenseSafePath $Directory $Directory))
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.PSIsContainer -and $item.Name -in @('node_modules', '.git')) { continue }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Package content must not contain a link: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            if ($item.Name -match '(?i)(licen[cs]e|notice|copying|copyright)' -and $item.Extension -notin @('.dll', '.exe', '.pdb', '.xml', '.json', '.js', '.map', '.ts', '.cs')) {
                if ($item.Length -eq 0) { throw "Empty license document: $($item.FullName)" }
                [pscustomobject]@{ Path = $item.FullName; Relative = [IO.Path]::GetRelativePath($Directory, $item.FullName).Replace('\', '/'); Source = 'package'; Aggregate = $item.Extension -notin @('.rtf', '.pdf') }
            }
        }
    }
}

<# .SYNOPSIS 读取实际发布 deps 的 package/runtimepack 联集及依赖文件哈希; 空、身份冲突或读取失败抛错. #>
function Get-PbiPublishedLicenseDependencies([string]$PublishRoot) {
    $packages = @{}
    $descriptors = @()
    foreach ($file in Get-ChildItem -LiteralPath $PublishRoot -Filter '*.deps.json' -File | Sort-Object Name) {
        $null = Get-PbiLicenseSafePath $PublishRoot $file.FullName
        $deps = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if (!$deps.ContainsKey('libraries') -or !$deps.ContainsKey('targets')) { throw "Invalid published deps: $($file.Name)" }
        foreach ($entry in $deps.libraries.GetEnumerator()) {
            if ($entry.Value.type -notin @('package', 'runtimepack')) { continue }
            if ($entry.Key -notmatch '^([A-Za-z0-9_.-]+)/([A-Za-z0-9_.+-]+)$') { throw "Invalid published package identity: $($entry.Key)" }
            if ($packages.ContainsKey($entry.Key) -and ($packages[$entry.Key] | ConvertTo-Json -Compress) -cne ($entry.Value | ConvertTo-Json -Compress)) { throw "Conflicting published dependency metadata: $($entry.Key)" }
            $packages[$entry.Key] = $entry.Value
        }
        $descriptors += @{ path = $file.Name; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
    if (!$descriptors.Count) { throw 'Published dependency descriptors are missing.' }
    return @{ Packages = $packages; Descriptors = $descriptors }
}

<# .SYNOPSIS 将固定目录中的补充许可证按版本及哈希读取; 返回文档列表, 缺失或漂移时抛错. #>
function Get-PbiCatalogDocuments([string]$RepositoryRoot, $Entry) {
    foreach ($file in $Entry.files) {
        $full = Get-PbiLicenseSafePath $RepositoryRoot (Join-Path $RepositoryRoot $file.path)
        if (!(Test-Path -LiteralPath $full -PathType Leaf) -or (Get-Item -LiteralPath $full).Length -eq 0) { throw "Missing catalog license: $($file.path)" }
        if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $file.sha256) { throw "Catalog license hash mismatch: $($file.path)" }
        [pscustomobject]@{ Path = $full; Relative = [IO.Path]::GetFileName($full); Source = $file.source; Aggregate = [IO.Path]::GetExtension($full) -notin @('.rtf', '.pdf') }
    }
}

<# .SYNOPSIS 解析包缓存位置并核验 nuspec 精确身份、许可和依赖哈希; 返回可分发包记录, 未知许可抛错. #>
function Get-PbiNugetLicenseComponent([string]$RepositoryRoot, [string]$Key, $Library, $Catalog) {
    $parts = $Key.Split('/')
    $name = $parts[0] -replace '^runtimepack\.', ''
    $version = $parts[1]
    $relative = "$($name.ToLowerInvariant())/$($version.ToLowerInvariant())"
    $roots = @()
    if ($env:NUGET_PACKAGES) { $roots += $env:NUGET_PACKAGES }
    else {
        $assets = Join-Path $RepositoryRoot 'src/PbiUtils.Cli/obj/project.assets.json'
        if (Test-Path -LiteralPath $assets -PathType Leaf) { $roots += @((Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json -AsHashtable).packageFolders.Keys) }
        $roots += (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages')
    }
    $directory = $null
    foreach ($root in $roots) {
        $candidate = Get-PbiLicenseSafePath $root (Join-Path $root $relative)
        if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
    }
    if (!$directory) { throw "Missing restored NuGet package: $Key" }
    $specPath = Get-PbiLicenseSafePath $directory (Join-Path $directory "$($name.ToLowerInvariant()).nuspec")
    [xml]$spec = Get-Content -LiteralPath $specPath -Raw
    $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $id = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
    $actualVersion = $metadata.SelectSingleNode('*[local-name()="version"]').InnerText
    if ($id -ine $name -or $actualVersion -cne $version) { throw "NuGet identity mismatch: $Key" }
    if ($Library.ContainsKey('sha512') -and $Library.sha512) {
        $hashPath = Get-PbiLicenseSafePath $directory (Join-Path $directory '.nupkg.metadata')
        $cacheMetadata = Get-Content -LiteralPath $hashPath -Raw | ConvertFrom-Json -AsHashtable
        if (('sha512-' + $cacheMetadata.contentHash) -cne $Library.sha512) { throw "NuGet content identity mismatch: $Key" }
    }
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    $license = if ($licenseNode) { $licenseNode.InnerText } else { '' }
    $override = @($Catalog.packages | Where-Object { $_.ecosystem -eq 'nuget' -and $_.name -ieq $name -and $_.version -ceq $version })
    if ($override.Count -gt 1) { throw "Duplicate catalog package: $Key" }
    $documents = @(Get-PbiLicenseDocuments $directory)
    if ($override.Count -eq 1) {
        if ($licenseNode -and $licenseNode.GetAttribute('type') -eq 'expression' -and $license -cne $override[0].declaredLicense) { throw "NuGet declared license changed: $Key" }
        $license = $override[0].license
        $documents += @(Get-PbiCatalogDocuments $RepositoryRoot $override[0])
    } elseif (!$licenseNode -or $licenseNode.GetAttribute('type') -ne 'file') {
        if ($license -notin @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC') -or !@($documents | Where-Object Relative -Match '(?i)(licen[cs]e|copying)').Count) { throw "Unreviewed or missing NuGet license: $Key ($license)" }
    } else {
        $licenseFile = Get-PbiLicenseSafePath $directory (Join-Path $directory $license)
        if (!@($documents | Where-Object Path -EQ $licenseFile).Count) { throw "Missing declared NuGet license file: $Key" }
        $license = 'LicenseRef-' + $name
    }
    if (!@($documents | Where-Object Aggregate).Count) { throw "No readable license for NuGet package: $Key" }
    $copyrightNode = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $repositoryNode = $metadata.SelectSingleNode('*[local-name()="repository"]')
    return @{ ecosystem = 'nuget'; name = $parts[0]; version = $version; license = $license; copyright = $(if ($copyrightNode) { $copyrightNode.InnerText } else { '' }); source = $(if ($repositoryNode) { $repositoryNode.GetAttribute('url') } else { "https://www.nuget.org/packages/$name/$version" }); documents = $documents }
}

<# .SYNOPSIS 运行离线前端闭包解析并核验包内许可; 返回组件, 缺失文件/依赖或未知许可直接抛错. #>
function Get-PbiNpmLicenseComponents([string]$RepositoryRoot, $Catalog) {
    $web = Join-Path $RepositoryRoot 'web'
    $null = Get-PbiLicenseSafePath $web (Join-Path $web 'node_modules')
    $raw = & node (Join-Path $PSScriptRoot 'LicenseNpmDependencies.mjs') $web
    if ($LASTEXITCODE -ne 0) { throw "Frontend license dependency discovery failed ($LASTEXITCODE)." }
    $closure = $raw | ConvertFrom-Json -AsHashtable
    foreach ($package in $closure.packages) {
        $license = $package.license
        $override = @($Catalog.packages | Where-Object { $_.ecosystem -eq 'npm' -and $_.name -ceq $package.name -and $_.version -ceq $package.version })
        $documents = @($package.directories | ForEach-Object { Get-PbiLicenseDocuments $_ })
        if ($override.Count -eq 1) {
            if ($license -cne $override[0].declaredLicense) { throw "npm declared license changed: $($package.name)" }
            $license = $override[0].license
            $documents += @(Get-PbiCatalogDocuments $RepositoryRoot $override[0])
        } elseif ($license -notin @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '(MPL-2.0 OR Apache-2.0)')) { throw "Unreviewed npm license: $($package.name) ($license)" }
        if (!@($documents | Where-Object { $_.Aggregate -and ($override.Count -eq 1 -or $_.Relative -match '(?i)(licen[cs]e|copying)') }).Count) { throw "Missing npm license: $($package.name)/$($package.version)" }
        if ($package.name -eq 'dompurify') { $license = 'Apache-2.0 (selected from MPL-2.0 OR Apache-2.0)' }
        @{ ecosystem = 'npm'; name = $package.name; version = $package.version; license = $license; copyright = $package.copyright; source = $package.source; dependencies = @($package.dependencies); documents = $documents }
    }
}

Export-ModuleMember -Function Get-PbiLicenseSafePath, Get-PbiLicenseDocuments, Get-PbiPublishedLicenseDependencies, Get-PbiCatalogDocuments, Get-PbiNugetLicenseComponent, Get-PbiNpmLicenseComponents
