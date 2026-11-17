# FilePath    : pbi-utils\scripts\ThirdPartyLicenses.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 离线生成和验证完整第三方许可分发包, 将实际发布依赖与全文及机器清单绑定.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ThirdPartyLicenseSources.psm1') -Force
$script:SharedResourceLicensePrefix = 'licenses/resource/shared/'

<# .SYNOPSIS 建立已写入文件的相对路径、长度和 SHA-256 清单项; 输入目录和文件, 失败直接抛错. #>
function Get-PbiLicenseFileRecord([string]$Root, [string]$Path) {
    $full = Get-PbiLicenseSafePath $Root $Path
    $item = Get-Item -LiteralPath $full
    return @{ path = [IO.Path]::GetRelativePath($Root, $full).Replace('\', '/'); length = $item.Length; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
}

<# .SYNOPSIS 将完整生态/名称/版本身份编码为稳定短目录标识; 输入不可为空, 返回 SHA-256 前 16 位小写十六进制. #>
function Get-PbiLicenseComponentId([string]$Ecosystem, [string]$Name, [string]$Version) {
    if (!$Ecosystem -or !$Name -or !$Version) { throw 'License component identity must not be empty.' }
    $bytes = [Text.Encoding]::UTF8.GetBytes("$Ecosystem/$Name/$Version")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 16).ToLowerInvariant()
}

<#
.SYNOPSIS 为指定范围内共用清单原文的静态资源生成短分发路径.
.PARAMETER Resources 当前发布范围内的静态资源条目.
.OUTPUTS 原文路径到分发路径的映射; 同一原文声明不同哈希或分发路径冲突时抛错.
#>
function Get-PbiSharedResourceLicensePaths($Resources) {
    $paths = @{}
    $destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $groups = $Resources | ForEach-Object { $_.files } | Group-Object -Property path
    foreach ($group in $groups) {
        if ($group.Count -lt 2) { continue }
        $file = $group.Group[0]
        if (@($group.Group | Where-Object sha256 -CNE $file.sha256).Count) { throw "Conflicting static resource license hashes: $($file.path)" }
        $id = Get-PbiLicenseComponentId 'resource' $file.path $file.sha256
        $extension = [IO.Path]::GetExtension($file.path).ToLowerInvariant()
        if (!$extension) { $extension = '.txt' }
        $destination = "$script:SharedResourceLicensePrefix$id$extension"
        if (!$destinations.Add($destination)) { throw "Conflicting shared resource license path: $destination" }
        $paths[$file.path] = $destination
    }
    return $paths
}

<##
.SYNOPSIS 从已合并 staging 的 deps、前端生产依赖闭包和固定来源清单生成离线许可包.
.PARAMETER RepositoryRoot 含 LICENSE、THIRD-PARTY-NOTICES.md、licenses/catalog.json 的仓库根.
.PARAMETER PublishRoot 已合并本轮发布的 staging 根, 不应已有生成的 licenses 目录.
.PARAMETER Scope Core 不包含前端和桌面资源; All 包含前端生产/peer 闭包及桌面资源.
.OUTPUTS 返回生成的清单对象; 缺失、未知许可、版本/哈希不匹配、根文档生成期间变化、链接、I/O 失败抛错.
##>
function Export-PbiLicenseBundle {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$PublishRoot, [Parameter(Mandatory)][ValidateSet('Core', 'All')][string]$Scope)
    $repository = Get-PbiLicenseSafePath $RepositoryRoot $RepositoryRoot
    $publish = Get-PbiLicenseSafePath $PublishRoot $PublishRoot
    if (!(Test-Path -LiteralPath $publish -PathType Container)) { throw 'License publish staging does not exist.' }
    $catalogPath = Get-PbiLicenseSafePath $repository (Join-Path $repository 'licenses/catalog.json')
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -AsHashtable
    if ($catalog.schemaVersion -ne 1) { throw 'Unsupported license catalog schema.' }
    $dependencies = Get-PbiPublishedLicenseDependencies $publish
    $components = @()
    foreach ($key in $dependencies.Packages.Keys | Sort-Object) { $components += Get-PbiNugetLicenseComponent $repository $key $dependencies.Packages[$key] $catalog }
    if ($Scope -eq 'All') { $components += @(Get-PbiNpmLicenseComponents $repository $catalog) }
    $resources = @($catalog.resources | Where-Object { $_.scope -ne 'All' -or $Scope -eq 'All' })
    foreach ($resource in $resources) {
        if ($resource.scope -notin @('Core', 'All') -or !$resource.name -or !$resource.version -or !$resource.license) { throw 'Invalid static license resource.' }
        $components += @{ ecosystem = 'resource'; name = $resource.name; version = $resource.version; license = $resource.license; copyright = ''; source = $resource.source; documents = @(Get-PbiCatalogDocuments $repository $resource) }
    }
    $sharedResourcePaths = Get-PbiSharedResourceLicensePaths $resources
    $writtenShared = @{}
    $rootDocumentHashes = @{}
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        $file = Get-PbiLicenseSafePath $repository (Join-Path $repository $name)
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Missing distribution document: $name" }
        $rootDocumentHashes[$name] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    $licenseRoot = Get-PbiLicenseSafePath $publish (Join-Path $publish 'licenses')
    if (Test-Path -LiteralPath $licenseRoot) { throw 'License output directory already exists; use a fresh publish staging.' }
    $null = New-Item -ItemType Directory -Path $licenseRoot
    $files = [Collections.Generic.List[object]]::new()
    $manifestComponents = [Collections.Generic.List[object]]::new()
    $componentIds = @{}
    # 先固定根文档副本; 独立文件与聚合正文使用相同字节, 不在聚合完成后重新读取仓库文件.
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        $destination = Get-PbiLicenseSafePath $publish (Join-Path $publish $name)
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $destination
        $fileRecord = Get-PbiLicenseFileRecord $publish $destination
        if ($fileRecord.sha256 -cne $rootDocumentHashes[$name]) { throw "Distribution document changed during generation: $name" }
        $files.Add($fileRecord)
    }
    $text = [Text.StringBuilder]::new()
    $null = $text.AppendLine('pbi-utils — Licenses and notices / 许可与声明').AppendLine()
    $null = $text.AppendLine('PROJECT LICENSE / 项目许可证').AppendLine((Get-Content -LiteralPath (Join-Path $publish 'LICENSE') -Raw)).AppendLine()
    $null = $text.AppendLine('THIRD-PARTY LICENSES AND NOTICES / 第三方许可与声明').AppendLine()
    $null = $text.AppendLine('Important Microsoft terms / 重要 Microsoft 条款: Microsoft.AnalysisServices and Microsoft.AnalysisServices.AdomdClient use the separate Microsoft terms reproduced below in English and Chinese. For a self-contained package, the applicable .NET Library and Windows SDK terms for the Windows runtime binaries are also reproduced below.').AppendLine()
    $null = $text.AppendLine('This offline document contains the source notices, an exact version index, and the full license/NOTICE/COPYING texts. npm entries conservatively cover the installed production dependency and peer closure; inclusion does not assert every module is present in a JavaScript chunk. Development-only root dependencies are excluded.').AppendLine()
    $null = $text.AppendLine((Get-Content -LiteralPath (Join-Path $publish 'THIRD-PARTY-NOTICES.md') -Raw)).AppendLine()
    $null = $text.AppendLine('INDEX / 索引')
    foreach ($component in $components) { $null = $text.AppendLine("$($component.ecosystem) | $($component.name) | $($component.version) | $($component.license)") }
    foreach ($component in $components) {
        $componentId = Get-PbiLicenseComponentId $component.ecosystem $component.name $component.version
        $identity = "$($component.ecosystem)/$($component.name)/$($component.version)"
        if ($componentIds.ContainsKey($componentId)) { throw "Duplicate or conflicting license component id: $identity" }
        $componentIds[$componentId] = $identity
        $null = $text.AppendLine().AppendLine(('=' * 78)).AppendLine("$($component.ecosystem): $($component.name) / $($component.version)").AppendLine("License: $($component.license)").AppendLine("Copyright: $($component.copyright)").AppendLine("Source: $($component.source | ConvertTo-Json -Compress -Depth 5)")
        $record = @{ ecosystem = $component.ecosystem; name = $component.name; version = $component.version; license = $component.license; copyright = $component.copyright; source = $component.source; files = @() }
        if ($component.ContainsKey('dependencies')) { $record.dependencies = $component.dependencies }
        $index = 0
        foreach ($document in $component.documents | Sort-Object Relative, Source) {
            $index++
            $extension = [IO.Path]::GetExtension($document.Path).ToLowerInvariant()
            if (!$extension) { $extension = '.txt' }
            $shared = $component.ecosystem -ceq 'resource' -and $sharedResourcePaths.ContainsKey($document.CatalogPath)
            $relative = if ($shared) { $sharedResourcePaths[$document.CatalogPath] } else { "licenses/$($component.ecosystem)/$componentId/" + ('{0:D2}' -f $index) + $extension }
            $destination = Get-PbiLicenseSafePath $publish (Join-Path $publish $relative)
            $alreadyWritten = $shared -and $writtenShared.ContainsKey($relative)
            if ($alreadyWritten) { $fileRecord = $writtenShared[$relative] }
            else {
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
                Copy-Item -LiteralPath $document.Path -Destination $destination
                $fileRecord = Get-PbiLicenseFileRecord $publish $destination
                $files.Add($fileRecord)
                if ($shared) { $writtenShared[$relative] = $fileRecord }
            }
            $record.files += @{ path = $fileRecord.path; sourceDocument = $document.Relative; source = $document.Source; aggregate = $document.Aggregate }
            if ($document.Aggregate) {
                $null = $text.AppendLine().AppendLine("--- $($document.Relative) [$($fileRecord.path)] ---")
                if ($alreadyWritten) { $null = $text.AppendLine('Full text appears above at the first occurrence of this file. / 全文见上方首次列出此文件的位置。') }
                else { $null = $text.AppendLine((Get-Content -LiteralPath $destination -Raw)) }
            }
        }
        if (!$record.files.Count) { throw "Empty component license documents: $($component.name)" }
        $manifestComponents.Add($record)
    }
    $aggregatePath = Get-PbiLicenseSafePath $publish (Join-Path $publish 'THIRD-PARTY-NOTICES.txt')
    [IO.File]::WriteAllText($aggregatePath, $text.ToString(), [Text.UTF8Encoding]::new($true))
    $files.Add((Get-PbiLicenseFileRecord $publish $aggregatePath))
    $inputFiles = @('licenses/catalog.json')
    if ($Scope -eq 'All') { $inputFiles += @('web/package.json', 'web/pnpm-lock.yaml') }
    foreach ($relative in $inputFiles) {
        $source = Get-PbiLicenseSafePath $repository (Join-Path $repository $relative)
        $destination = Get-PbiLicenseSafePath $publish (Join-Path $licenseRoot ('inputs/' + [IO.Path]::GetFileName($relative)))
        $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
        Copy-Item -LiteralPath $source -Destination $destination
        $files.Add((Get-PbiLicenseFileRecord $publish $destination))
    }
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        $source = Get-PbiLicenseSafePath $repository (Join-Path $repository $name)
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $rootDocumentHashes[$name]) { throw "Distribution document changed during generation: $name" }
    }
    $manifest = @{ schemaVersion = 1; scope = $Scope; dependencyDescriptors = @($dependencies.Descriptors); components = @($manifestComponents.ToArray()); files = @($files.ToArray() | Sort-Object path) }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $licenseRoot 'manifest.json') -Encoding utf8
    Assert-PbiLicenseBundle -PublishRoot $publish
    return $manifest
}

<##
.SYNOPSIS 验证已生成许可包的精确文件集合、哈希、全文归属、静态来源和实际 deps 范围, 不访问网络或开发缓存.
.PARAMETER PublishRoot 已合并发布根目录.
.OUTPUTS 成功无输出; 文件缺失、额外、漂移、链接、身份不符或不可读时抛错.
##>
function Assert-PbiLicenseBundle {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublishRoot)
    $publish = Get-PbiLicenseSafePath $PublishRoot $PublishRoot
    $manifestPath = Get-PbiLicenseSafePath $publish (Join-Path $publish 'licenses/manifest.json')
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 1 -or $manifest.scope -cnotin @('Core', 'All')) { throw 'Unsupported license manifest.' }
    $expected = @{}
    foreach ($file in $manifest.files) {
        $relative = $file.path
        if ($relative -isnot [string] -or !$relative -or $relative.Contains('\') -or $relative.Contains(':') -or $relative.StartsWith('/') -or
            $relative.Split('/') -contains '..' -or $relative.Split('/') -contains '.' -or $relative.Split('/') -contains '' -or $relative -ieq 'licenses/manifest.json' -or
            ($relative -cnotin @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt') -and !$relative.StartsWith('licenses/', [StringComparison]::Ordinal))) { throw "Invalid license manifest path: $relative" }
        if ($expected.ContainsKey($file.path)) { throw "Duplicate license manifest file: $($file.path)" }
        $full = Get-PbiLicenseSafePath $publish (Join-Path $publish $file.path)
        if ($file.sha256 -isnot [string] -or $file.sha256 -notmatch '^[0-9A-F]{64}$' -or !(Test-Path -LiteralPath $full -PathType Leaf) -or
            (Get-Item -LiteralPath $full).Length -ne $file.length -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $file.sha256) { throw "License file integrity mismatch: $($file.path)" }
        $expected[$file.path] = $file
    }
    foreach ($required in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/inputs/catalog.json')) { if (!$expected.ContainsKey($required)) { throw "Required license file missing from manifest: $required" } }
    # 逐级核验后才遍历子目录, 空链接目录也不得逃过文件集合验证.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push((Join-Path $publish 'licenses'))
    while ($pending.Count) {
        foreach ($file in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $null = Get-PbiLicenseSafePath $publish $file.FullName
            if ($file.PSIsContainer) { $pending.Push($file.FullName); continue }
            if ($file.FullName -ieq $manifestPath) { continue }
            $relative = [IO.Path]::GetRelativePath($publish, $file.FullName).Replace('\', '/')
            if (!$expected.ContainsKey($relative)) { throw "Unlisted license file: $relative" }
        }
    }
    $published = Get-PbiPublishedLicenseDependencies $publish
    $requiredDescriptors = @('pbi-utils.deps.json', 'PbiUtils.Host.deps.json')
    if ($manifest.scope -ceq 'All') { $requiredDescriptors += 'PbiUtils.Desktop.deps.json' }
    if ((@($published.Descriptors.path | Sort-Object) -join "`n") -cne (@($requiredDescriptors | Sort-Object) -join "`n")) { throw 'Published dependency descriptor scope does not match the license manifest.' }
    # 哈希表字段枚举顺序可能随 PowerShell 进程改变, 只比较路径与内容身份, 不把 JSON 排版当作语义.
    $expectedDescriptors = @($manifest.dependencyDescriptors | Sort-Object path)
    $actualDescriptors = @($published.Descriptors | Sort-Object path)
    if ($expectedDescriptors.Count -ne $actualDescriptors.Count) { throw 'Published dependency descriptors do not match the license manifest.' }
    for ($index = 0; $index -lt $expectedDescriptors.Count; $index++) {
        if ($expectedDescriptors[$index].path -cne $actualDescriptors[$index].path -or
            $expectedDescriptors[$index].sha256 -cne $actualDescriptors[$index].sha256) { throw 'Published dependency descriptors do not match the license manifest.' }
    }
    $nuget = @{}
    $identities = @{}
    $resources = @{}
    foreach ($component in $manifest.components) {
        $identity = "$($component.ecosystem)/$($component.name)/$($component.version)"
        if ($component.ecosystem -cnotin @('nuget', 'npm', 'resource') -or !$component.name -or !$component.version -or
            $identities.ContainsKey($identity) -or !$component.license -or !$component.files.Count) { throw "Invalid license component: $identity" }
        $identities[$identity] = $true
        $componentId = Get-PbiLicenseComponentId ([string]$component.ecosystem) ([string]$component.name) ([string]$component.version)
        $prefix = "licenses/$($component.ecosystem)/$componentId/"
        $documentPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($document in $component.files) {
            $sourceDocument = $document.sourceDocument
            $path = $document.path
            if ($path -isnot [string] -or !$expected.ContainsKey($path)) { throw "Missing or invalid component license: $identity" }
            $shared = $component.ecosystem -ceq 'resource' -and $path.StartsWith($script:SharedResourceLicensePrefix, [StringComparison]::Ordinal)
            if ($sourceDocument -isnot [string] -or !$sourceDocument -or $sourceDocument.Contains('\') -or $sourceDocument.Contains(':') -or $sourceDocument.StartsWith('/') -or
                $sourceDocument.Split('/') -contains '..' -or $sourceDocument.Split('/') -contains '.' -or $sourceDocument.Split('/') -contains '' -or
                (!$shared -and !$path.StartsWith($prefix, [StringComparison]::Ordinal)) -or !$documentPaths.Add($path)) { throw "Missing or invalid component license: $identity" }
        }
        if ($component.ecosystem -eq 'nuget') { $nuget["$($component.name)/$($component.version)"] = $true }
        if ($component.ecosystem -eq 'resource') { $resources[$identity] = $component }
    }
    if (@($published.Packages.Keys | Where-Object { !$nuget.ContainsKey($_) }).Count -or @($nuget.Keys | Where-Object { !$published.Packages.ContainsKey($_) }).Count) { throw 'Published NuGet dependency closure does not match the license manifest.' }
    # 静态素材没有 deps 入口, 以随包固化且已校验哈希的来源清单核验适用资源及每份原文.
    $catalog = Get-Content -LiteralPath (Join-Path $publish 'licenses/inputs/catalog.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($catalog.schemaVersion -ne 1) { throw 'Unsupported static resource catalog.' }
    $applicableResources = @($catalog.resources | Where-Object { $_.scope -cne 'All' -or $manifest.scope -ceq 'All' })
    $sharedResourcePaths = Get-PbiSharedResourceLicensePaths $applicableResources
    foreach ($path in $expected.Keys) {
        if ($path.StartsWith($script:SharedResourceLicensePrefix, [StringComparison]::OrdinalIgnoreCase) -and $path -cnotin $sharedResourcePaths.Values) { throw "Unlisted shared resource license: $path" }
    }
    $requiredResources = @{}
    foreach ($resource in $catalog.resources) {
        if ($resource.scope -cnotin @('Core', 'All') -or !$resource.name -or !$resource.version -or !$resource.license -or !$resource.files.Count) { throw 'Static resource catalog is invalid.' }
        if ($resource.scope -ceq 'All' -and $manifest.scope -cne 'All') { continue }
        $identity = "resource/$($resource.name)/$($resource.version)"
        if ($requiredResources.ContainsKey($identity) -or !$resources.ContainsKey($identity) -or $resources[$identity].license -cne $resource.license) { throw "Static resource identity or license mismatch: $identity" }
        $requiredResources[$identity] = $true
        $catalogDocuments = @($resource.files | ForEach-Object {
            "$($_.sha256)|$($_.source)|$([IO.Path]::GetFileName($_.path))|$($sharedResourcePaths[$_.path])"
        } | Sort-Object)
        $componentDocuments = @($resources[$identity].files | ForEach-Object {
            $sharedPath = if ($_.path.StartsWith($script:SharedResourceLicensePrefix, [StringComparison]::Ordinal)) { $_.path }
            "$($expected[$_.path].sha256)|$($_.source)|$($_.sourceDocument)|$sharedPath"
        } | Sort-Object)
        if (($catalogDocuments -join "`n") -cne ($componentDocuments -join "`n")) { throw "Static resource documents do not match the catalog: $identity" }
    }
    if ($resources.Count -ne $requiredResources.Count) { throw 'Static resource component scope does not match the catalog.' }
    if ($manifest.scope -eq 'All') {
        if (!$expected.ContainsKey('licenses/inputs/package.json') -or !$expected.ContainsKey('licenses/inputs/pnpm-lock.yaml')) { throw 'Frontend license input snapshot is missing.' }
        $webPackage = Get-Content -LiteralPath (Join-Path $publish 'licenses/inputs/package.json') -Raw | ConvertFrom-Json -AsHashtable
        foreach ($dependency in $webPackage.dependencies.GetEnumerator()) { if (!$identities.ContainsKey("npm/$($dependency.Key)/$($dependency.Value)")) { throw "Frontend root dependency missing: $($dependency.Key)" } }
        foreach ($component in $manifest.components | Where-Object ecosystem -EQ 'npm') {
            foreach ($dependency in $component.dependencies) { if (!$identities.ContainsKey("npm/$dependency")) { throw "Frontend transitive dependency missing: $dependency" } }
        }
    } elseif (@($manifest.components | Where-Object ecosystem -EQ 'npm').Count) { throw 'Core license bundle must not contain the frontend dependency closure.' }
}

Export-ModuleMember -Function Export-PbiLicenseBundle, Assert-PbiLicenseBundle
