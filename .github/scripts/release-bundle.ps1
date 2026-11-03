# FilePath    : pbi-utils\.github\scripts\release-bundle.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 从唯一 EXE ZIP 生成或验证共享说明与完整性清单, 错误终止发布.

<#
.SYNOPSIS 创建或验证同一次构建的发布包, 发布端不重新生成说明.
.PARAMETER Mode Download 下载并验证已有发行版; Create 生成说明和清单; Verify 校验完整共享包.
.PARAMETER Directory 已包含精确 ZIP 的目录.
.OUTPUTS 验证结果; 输入、归属、内容或摘要不符时抛错.
#>
param([Parameter(Mandatory)][ValidateSet('Create', 'Verify', 'Download')][string]$Mode,
    [Parameter(Mandatory)][string]$Directory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/../../scripts/ReleaseAutomation.psm1" -Force
$context = Get-PbiReleaseContext -RepositoryRoot $PWD -Ref "refs/tags/$env:RELEASE_TAG"
if ($env:ARCHIVE_NAME -cne $context.ArchiveName) { throw 'ZIP 文件名与发布上下文不符' }
$tagType = git cat-file -t "refs/tags/$env:RELEASE_TAG"
if ($LASTEXITCODE -ne 0 -or $tagType -cne 'tag') { throw '必须使用已存在的注释标签' }
$commit = git rev-parse "refs/tags/$env:RELEASE_TAG^{commit}"
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') { throw '标签提交无效' }
git merge-base --is-ancestor $commit "refs/remotes/origin/$($context.SourceBranch)"
if ($LASTEXITCODE -ne 0) { throw '标签不属于指定来源分支' }
$installerName = "pbi-utils-$($context.Version)-win-x64-without-dotnet-setup.exe"
$zipPath = Join-Path $Directory $context.ArchiveName
if ($Mode -ceq 'Download') {
    $json = (& gh api "repos/$env:GITHUB_REPOSITORY/releases/tags/$env:RELEASE_TAG") -join "`n"
    if ($LASTEXITCODE -ne 0) { throw '已有 GitHub 发行版读取失败, 不重新构建或创建发行版' }
    $release = $json | ConvertFrom-Json
    $url = "https://github.com/$env:GITHUB_REPOSITORY/releases/download/$env:RELEASE_TAG/$env:ARCHIVE_NAME"
    if ($release.tag_name -cne $env:RELEASE_TAG -or $release.name -cne $env:RELEASE_TAG -or
        $release.draft -isnot [bool] -or $release.draft -or $release.prerelease -isnot [bool] -or
        $release.prerelease -ne $context.Prerelease -or @($release.assets).Count -ne 1 -or
        $release.assets[0].name -cne $env:ARCHIVE_NAME -or $release.assets[0].browser_download_url -cne $url -or
        $release.assets[0].size -le 0 -or $release.assets[0].size -gt 100000000) { throw '已有发行版身份或附件不符' }
    $null = New-Item -ItemType Directory -Path $Directory -Force
    & gh release download $env:RELEASE_TAG --pattern $env:ARCHIVE_NAME --dir $Directory --repo $env:GITHUB_REPOSITORY
    if ($LASTEXITCODE -ne 0) { throw '已有发布 ZIP 下载失败' }
    if ((Get-Item $zipPath).Length -ne $release.assets[0].size -or
        $release.assets[0].digest -cne ('sha256:' + (Get-FileHash $zipPath).Hash.ToLowerInvariant())) { throw '已有发布 ZIP 长度或 SHA256 不符' }
}
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $zipPath))
try {
    if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne $installerName -or $zip.Entries[0].Length -le 0) { throw 'ZIP 必须仅包含当前非空 EXE' }
    $stream = $zip.Entries[0].Open()
    try { $installerHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
} finally { $zip.Dispose() }
$notesPath = Join-Path $Directory 'release-notes.md'
$manifestPath = Join-Path $Directory 'release-manifest.json'
$expectedNotes = Get-PbiReleaseNotes -RepositoryRoot $PWD -Tag $env:RELEASE_TAG -InstallerSha256 $installerHash
if ($Mode -ceq 'Download' -and $release.body.TrimEnd() -cne $expectedNotes.TrimEnd()) { throw '已有发布说明与 EXE 摘要不符' }
if ($Mode -cne 'Verify') { [IO.File]::WriteAllText($notesPath, $expectedNotes) }
if ([IO.File]::ReadAllText($notesPath) -cne $expectedNotes) { throw '共享发布说明与标签或 EXE 摘要不符' }
$actual = [ordered]@{
    tag = $env:RELEASE_TAG; commit = $commit; archive = $context.ArchiveName
    size = (Get-Item $zipPath).Length; sha256 = (Get-FileHash $zipPath).Hash
    installer = $installerName; installerSha256 = $installerHash; notesSha256 = (Get-FileHash $notesPath).Hash
}
if ($Mode -cne 'Verify') {
    $actual | ConvertTo-Json | Set-Content $manifestPath -Encoding utf8
} else {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.Count -ne $actual.Count) { throw '发布清单字段无效' }
    foreach ($key in $actual.Keys) {
        if (!$manifest.ContainsKey($key) -or $manifest[$key] -cne $actual[$key]) { throw "发布清单校验失败: $key" }
    }
    Copy-Item -LiteralPath $notesPath -Destination ./release-notes.md
}
Write-Output "Verified $($context.ArchiveName): bytes=$($actual.size), SHA256=$($actual.sha256), EXE SHA256=$installerHash"
