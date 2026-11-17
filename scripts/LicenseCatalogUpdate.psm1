# FilePath    : pbi-utils\scripts\LicenseCatalogUpdate.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 从已还原的发布依赖同步精确许可身份, 审核字节变化后再更新固定清单.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ThirdPartyLicenseSources.psm1') -Force
. (Join-Path $PSScriptRoot 'LicenseDotNetUpdate.ps1')
. (Join-Path $PSScriptRoot 'LicenseNpmUpdate.ps1')

<#
.SYNOPSIS 读取 GitHub 固定提交的原文, 不跟随重定向.
.PARAMETER Uri 已由包元数据和清单规则绑定的 HTTPS 原文地址.
.OUTPUTS 原始字节; 失效地址、网络失败或异常体积时抛错.
#>
function Get-PbiLicenseUpdateBytes([uri]$Uri) {
    if ($Uri.Scheme -cne 'https' -or $Uri.Host -cne 'raw.githubusercontent.com' -or !$Uri.IsDefaultPort -or
        $Uri.UserInfo -or $Uri.Query -or $Uri.Fragment -or
        $Uri.AbsolutePath -cnotmatch '^/[\w.-]+/[\w.-]+/[0-9a-f]{40}/[\w./-]+$') {
        throw "不支持的许可原文地址: $Uri"
    }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        $client.MaxResponseContentBufferSize = 2MB
        return ,($client.GetByteArrayAsync($Uri).GetAwaiter().GetResult())
    }
    catch { throw "无法读取固定提交的许可原文 $Uri。$($_.Exception.Message)" }
    finally { $client.Dispose() }
}

<#
.SYNOPSIS 核对仓库及新来源的字节, 相同时更新候选记录的来源.
.PARAMETER RepositoryRoot 仓库根.
.PARAMETER Document 含文件路径、已审核 SHA-256 和来源的候选记录.
.PARAMETER Bytes 新来源的完整原始字节.
.PARAMETER Source 新的精确版本或提交来源.
.OUTPUTS 无; 来源不可用、离线原文有漂移或上游内容变化时要求人工审阅.
#>
function Confirm-PbiLicenseUpdateContent([string]$RepositoryRoot, $Document, [byte[]]$Bytes, [string]$Source) {
    $null = @(Get-PbiCatalogDocuments $RepositoryRoot @{ files = @($Document) })
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
    if (!$Bytes.Length -or $hash -cne $Document.sha256) {
        throw "许可原文已变化, 请审阅 $($Document.path)；来源 $Source；原 SHA-256 $($Document.sha256)，新 SHA-256 $hash。"
    }
    $Document.source = $Source
}

<#
.SYNOPSIS 从各发布入口的已还原 win-x64 目标取得 NuGet 闭包.
.PARAMETER RepositoryRoot 仓库根.
.PARAMETER Projects CLI、Host 及 Desktop 项目目录.
.OUTPUTS 精确包身份到还原元数据的映射; 目标缺失或同一身份的内容冲突时抛错.
#>
function Get-PbiSetupLicensePackages([string]$RepositoryRoot, [string[]]$Projects) {
    $packages = @{}
    foreach ($project in $Projects) {
        $path = Get-PbiLicenseSafePath $RepositoryRoot (Join-Path $RepositoryRoot "$project/obj/project.assets.json")
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "缺少 $project 的 NuGet 还原资产。请先运行 .\run.ps1 -Choice 'setupC#' -Scope All，再重新运行 setupLicense。"
        }
        $assets = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        $targets = @($assets.targets.Keys | Where-Object { $_.EndsWith('/win-x64', [StringComparison]::Ordinal) })
        if ($targets.Count -ne 1) { throw "无法确定 $project 的 win-x64 还原目标。" }
        foreach ($item in $assets.targets[$targets[0]].GetEnumerator()) {
            if ($item.Value.type -ne 'package') { continue }
            $runtimeAssets = foreach ($kind in @('runtime', 'native', 'resource', 'runtimeTargets')) {
                if ($item.Value.Contains($kind)) { $item.Value[$kind].Keys }
            }
            if (!@($runtimeAssets | Where-Object { $_ -notmatch '(^|/)_\._$' }).Count) { continue }
            $key = $item.Key
            $library = $assets.libraries[$key]
            if ($key -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.+-]+$' -or !$library -or !$library.sha512) {
                throw "无效的 NuGet 还原记录: $project, $key"
            }
            $restoredHash = 'sha512-' + $library.sha512
            if ($packages.ContainsKey($key) -and $packages[$key].sha512 -cne $restoredHash) {
                throw "NuGet 还原内容冲突: $key"
            }
            $packages[$key] = @{ sha512 = $restoredHash }
        }
    }
    return $packages
}

<#
.SYNOPSIS 对精确版本发生变化的已知 NuGet 补充记录核对新版本的许可来源.
.PARAMETER RepositoryRoot 仓库根.
.PARAMETER Catalog 内存候选清单.
.PARAMETER Packages 已还原的 win-x64 依赖.
.OUTPUTS 无; 声明、来源或上游原文变化时要求审阅.
#>
function Update-PbiNugetLicenseCatalog([string]$RepositoryRoot, [System.Collections.IDictionary]$Catalog, [hashtable]$Packages) {
    $baselines = @($Catalog.packages | Where-Object ecosystem -CEQ 'nuget')
    foreach ($key in $Packages.Keys | Sort-Object) {
        $name, $version = $key.Split('/')
        $entries = @($baselines | Where-Object name -IEQ $name)
        if (!$entries.Count -or @($entries | Where-Object version -CEQ $version).Count) { continue }
        $entry = $entries[0]
        $rawPattern = '^https://raw\.githubusercontent\.com/(?<repo>[\w.-]+/[\w.-]+)/[a-f0-9]{40}/(?<file>[\w./-]+)$'
        # 同一文本可对应多个精确版本. 候选不共享原文、许可声明和上游文件路径时, 由人工挑选正确的来源.
        $identity = $entry.files[0].source -creplace '/[a-f0-9]{40}/', '/{commit}/'
        $divergentEntries = @($entries | Where-Object {
            $_.files.Count -ne 1 -or $_.files[0].sha256 -cne $entry.files[0].sha256 -or
            $_.declaredLicense -cne $entry.declaredLicense -or $_.license -cne $entry.license -or
            ($_.files[0].source -creplace '/[a-f0-9]{40}/', '/{commit}/') -cne $identity
        })
        if ($divergentEntries.Count) {
            throw "NuGet 许可补充项需要审阅: $key。请核对新版本的许可及版权声明，再更新 licenses/catalog.json。"
        }
        $package = Get-PbiNugetLicensePackage $RepositoryRoot $key $Packages[$key]
        $license = $package.metadata.SelectSingleNode('*[local-name()="license"]')
        $repository = $package.metadata.SelectSingleNode('*[local-name()="repository"]')
        if (!$license -or $license.GetAttribute('type') -cne 'expression' -or $license.InnerText -cne $entry.declaredLicense -or
            !$repository -or $repository.GetAttribute('commit') -cnotmatch '^[a-f0-9]{40}$' -or $entry.files[0].source -cnotmatch $rawPattern) {
            throw "NuGet 许可声明或固定原文来源需要审阅: $key。请更新 licenses/catalog.json。"
        }
        $repo = $Matches.repo
        $file = $Matches.file
        $repositoryUrl = $repository.GetAttribute('url').TrimEnd('/') -replace '\.git$', ''
        if ($repositoryUrl -ine "https://github.com/$repo") { throw "NuGet 上游来源已变化: $key ($repositoryUrl)。请审阅许可来源。" }
        $commit = $repository.GetAttribute('commit')
        $source = "https://raw.githubusercontent.com/$repo/$commit/$file"
        # 同时分发多个版本时, 各精确身份保留各自的来源.
        $candidate = $entry | ConvertTo-Json -Depth 20 -Compress | ConvertFrom-Json -AsHashtable
        if ($source -cne $candidate.files[0].source) {
            Confirm-PbiLicenseUpdateContent $RepositoryRoot $candidate.files[0] (Get-PbiLicenseUpdateBytes $source) $source
        }
        $candidate.version = $version
        $replace = -1
        for ($index = 0; $index -lt $Catalog.packages.Count; $index++) {
            $prior = $Catalog.packages[$index]
            if ($prior.ecosystem -ceq 'nuget' -and $prior.name -ieq $name -and !$Packages.ContainsKey("$name/$($prior.version)")) {
                $replace = $index
                break
            }
        }
        if ($replace -ge 0) { $Catalog.packages[$replace] = $candidate }
        else { $Catalog.packages += $candidate }
    }
}

<#
.SYNOPSIS 同步已还原依赖和选定 SDK 的第三方许可清单.
.PARAMETER RepositoryRoot 含已还原依赖及许可来源的仓库根.
.OUTPUTS 清单写入时返回 true, 已是最新返回 false; 未知或变化条款、并发修改或 I/O 失败时抛错.
#>
function Update-PbiLicenseCatalog([string]$RepositoryRoot) {
    $root = Get-PbiLicenseSafePath $RepositoryRoot $RepositoryRoot
    $path = Get-PbiLicenseSafePath $root (Join-Path $root 'licenses/catalog.json')
    $bytes = [IO.File]::ReadAllBytes($path)
    $originalHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $catalog = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF) | ConvertFrom-Json -AsHashtable
    if ($catalog.schemaVersion -ne 1) { throw 'Unsupported license catalog schema.' }
    $original = $catalog | ConvertTo-Json -Depth 30
    $projects = @('src/PbiUtils.Cli', 'src/PbiUtils.Host', 'src/PbiUtils.Desktop')
    $packages = Get-PbiSetupLicensePackages $root $projects
    Update-PbiNugetLicenseCatalog $root $catalog $packages
    Update-PbiDotNetLicenseCatalog $root $catalog @{ Projects = $projects }
    Update-PbiNpmLicenseCatalog $root $catalog

    # 复用正式许可生成的包身份、许可全文与哈希校验, 通过后提交单一清单文件.
    foreach ($key in $packages.Keys | Sort-Object) {
        $null = Get-PbiNugetLicenseComponent $root $key $packages[$key] $catalog
    }
    $null = @(Get-PbiNpmLicenseComponents $root $catalog)
    foreach ($entry in @($catalog.resources) + @($catalog.packages)) { $null = @(Get-PbiCatalogDocuments $root $entry) }

    $updated = $catalog | ConvertTo-Json -Depth 30
    if ($updated -ceq $original) { return $false }
    $pending = Get-PbiLicenseSafePath $root "$path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $lineBreak = [string][char]10
        [IO.File]::WriteAllText($pending, $updated.Replace([Environment]::NewLine, $lineBreak) + $lineBreak)
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $originalHash) {
            throw '许可清单在同步期间发生变化, 请核对本地改动后重新运行 setupLicense。'
        }
        [IO.File]::Move($pending, $path, $true)
    }
    finally { if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) } }
    return $true
}

Export-ModuleMember -Function Update-PbiLicenseCatalog
