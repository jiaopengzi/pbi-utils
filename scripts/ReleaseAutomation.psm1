# FilePath    : pbi-utils\scripts\ReleaseAutomation.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 复用产品版本生成受限发布上下文、精确更新说明与双文件 ZIP, 不创建远端发行版.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS 读取当前产品版本, 将明确 Git ref 映射到构建或正式/预发布策略.
.PARAMETER RepositoryRoot 含 Directory.Build.props 的仓库根目录.
.PARAMETER Ref GitHub 完整 ref; PR merge ref 仅构建, 稳定标签要求 main, v2.N 标签要求 v2.
.OUTPUTS 包含 Version、ArchiveName、ReleaseTag、Prerelease、SourceBranch 的对象; 未知 ref 或版本不符抛错.
.NOTES 本函数只确定策略, 调用者仍须核验提交属于 SourceBranch, 不能以标签名证明提交归属.
#>
function Get-PbiReleaseContext([string]$RepositoryRoot, [string]$Ref) {
    $version = [string]([xml](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?$') { throw '产品版本必须为三段或四段数字' }
    $tag = ''; $branch = ''; $prerelease = $false
    if ($Ref -cmatch '^refs/heads/(main|v2)$') { $branch = $Matches[1] }
    elseif ($Ref -cmatch '^refs/pull/[1-9][0-9]*/merge$') { $branch = '' }
    elseif ($Ref -ceq "refs/tags/desktop-v$version") { $tag = "desktop-v$version"; $branch = 'main' }
    elseif ($Ref -cmatch ('^refs/tags/(desktop-v' + [regex]::Escape($version) + '-v2\.[1-9][0-9]*)$')) {
        $tag = $Matches[1]; $branch = 'v2'; $prerelease = $true
    } else { throw '发布 ref 无效或标签版本与当前产品不一致' }
    [pscustomobject]@{
        Version = $version; ArchiveName = "pbi-utils-$version-win-x64-without-dotnet.zip"
        ReleaseTag = $tag; Prerelease = $prerelease; SourceBranch = $branch
    }
}

<#
.SYNOPSIS 从 CHANGELOG 精确选取唯一、含真实内容的发布章节, 不回退到其他版本.
.PARAMETER RepositoryRoot 仓库根目录.
.PARAMETER Tag 精确发布标签, 不含 refs/tags 前缀.
.OUTPUTS 原章节正文, 找不到、重复、日期格式无效或内容为空时抛错.
#>
function Get-PbiReleaseNotes([string]$RepositoryRoot, [string]$Tag) {
    $lines = @(Get-Content -LiteralPath (Join-Path $RepositoryRoot 'CHANGELOG.md'))
    $marker = '^## \[' + [regex]::Escape($Tag) + '\] - (\d{4}-\d{2}-\d{2})$'
    $indices = @(for ($index = 0; $index -lt $lines.Count; $index++) { if ($lines[$index] -cmatch $marker) { $index } })
    if ($indices.Count -ne 1) { throw 'CHANGELOG 必须有唯一且带日期的精确发布章节' }
    $start = $indices[0]
    $dateText = [regex]::Match($lines[$start], $marker).Groups[1].Value
    $null = [DateTime]::ParseExact($dateText, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $section = [Collections.Generic.List[string]]::new()
    for ($index = $start + 1; $index -lt $lines.Count -and $lines[$index] -notmatch '^##\s'; $index++) { $section.Add($lines[$index]) }
    $notes = ($section -join "`n").Trim()
    $content = [regex]::Replace($notes, '(?s)<!--.*?-->', '')
    $contentLines = @($content -split '\r?\n' | Where-Object { $_.Trim() -and $_ -notmatch '^\s*#' -and $_ -notmatch '^\s*(?:[-*+]\s*)?$' })
    if (!$contentLines.Count) { throw 'CHANGELOG 发布章节没有更新内容' }
    return $notes + "`n"
}

<#
.SYNOPSIS 校验当前 without-dotnet 安装器和 SHA256, 创建仅含两文件的 ZIP 并核对归档原字节.
.PARAMETER RepositoryRoot 仓库根目录, 安装器必须已由现有 package/release 成功生成.
.PARAMETER Destination ZIP 输出目录; 临时归档校验完成后才替换本版本 ZIP.
.OUTPUTS ZIP 的绝对路径; 缺失、空安装器、错误校验和、归档写入/校验失败时抛错, 不交付半包.
#>
function New-PbiReleaseArchive([string]$RepositoryRoot, [string]$Destination) {
    $context = Get-PbiReleaseContext $RepositoryRoot 'refs/heads/main'
    $baseName = "pbi-utils-$($context.Version)-win-x64-without-dotnet-setup"
    $packageRoot = Join-Path $RepositoryRoot 'artifacts/release/win-x64/without-dotnet'
    $exe = Join-Path $packageRoot "$baseName.exe"
    $checksum = Join-Path $packageRoot "$baseName.sha256"
    foreach ($path in @($exe, $checksum)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) { throw '缺少非空的当前版本安装器或 SHA256' }
    }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $expectedChecksum = "$hash  $baseName.exe"
    if ((Get-Content -LiteralPath $checksum -Raw).Trim() -cne $expectedChecksum) { throw '安装器 SHA256 或文件名不匹配' }
    $null = New-Item -ItemType Directory -Path $Destination -Force
    $destinationRoot = (Get-Item -LiteralPath $Destination).FullName
    $archivePath = Join-Path $destinationRoot $context.ArchiveName
    $temporary = Join-Path $destinationRoot ('.release-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Compress-Archive -LiteralPath @($exe, $checksum) -DestinationPath $temporary -CompressionLevel Optimal
        $archive = [IO.Compression.ZipFile]::OpenRead($temporary)
        try {
            $names = @($archive.Entries.FullName | Sort-Object)
            if (($names -join '|') -cne "$baseName.exe|$baseName.sha256") { throw 'ZIP 必须在根目录恰好包含安装器和 SHA256' }
            foreach ($file in @($exe, $checksum)) {
                $entry = $archive.GetEntry([IO.Path]::GetFileName($file))
                $stream = $entry.Open()
                try { $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
                if ($actualHash -cne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) { throw 'ZIP 内容与原始文件不一致' }
                if ($file -ceq $exe -and $actualHash -cne $hash) { throw '归档期间安装器发生变化' }
            }
            $reader = [IO.StreamReader]::new($archive.GetEntry("$baseName.sha256").Open())
            try { if ($reader.ReadToEnd().Trim() -cne $expectedChecksum) { throw '归档期间 SHA256 发生变化' } } finally { $reader.Dispose() }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $archivePath -Force
        return $archivePath
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Export-ModuleMember -Function Get-PbiReleaseContext, Get-PbiReleaseNotes, New-PbiReleaseArchive
