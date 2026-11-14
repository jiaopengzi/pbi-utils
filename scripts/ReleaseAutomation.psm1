# FilePath    : pbi-utils\scripts\ReleaseAutomation.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 读取构建版本, 准备并核验发布身份与说明, 生成单文件 ZIP.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS 从精确标签提取 Windows 数字版本和发布类型; 格式或数值超界时抛错.
.PARAMETER Tag version 或 CHANGELOG 章节中的完整发布标签.
.PARAMETER Historical 读取已有标签原始发行版时, 也识别此前的 desktop 标签.
.OUTPUTS Version、Tag 和 Prerelease.
#>
function ConvertFrom-PbiReleaseTag([string]$Tag, [switch]$Historical) {
    $match = [regex]::Match($Tag, '\Av(?<version>(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2})(?:-rc(?<revision>[1-9][0-9]*))?\z')
    if (!$match.Success -and $Historical) {
        $match = [regex]::Match($Tag, '\Adesktop-v(?<version>(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2,3})(?:-v2\.(?<revision>[1-9][0-9]*))?\z')
    }
    if (!$match.Success) { throw '标签格式无效, 使用 vX.Y.Z 或 vX.Y.Z-rcN (N 为正整数)' }
    $version = $match.Groups['version'].Value
    foreach ($part in $version.Split('.')) {
        $number = [uint16]0
        if (![uint16]::TryParse($part, [ref]$number) -or $number -eq [uint16]::MaxValue) { throw '产品版本超出范围, 每段数字最多 65534' }
    }
    $revision = $match.Groups['revision']
    $number = 0
    if ($revision.Success -and ![int]::TryParse($revision.Value, [ref]$number)) { throw '预发布轮次超出范围' }
    [pscustomobject]@{ Version = $version; Tag = $Tag; Prerelease = $revision.Success }
}

<#
.SYNOPSIS 解码后的 version 只允许一个完整标签和可选的单个行尾; 不合规时报错.
.PARAMETER Text 本地版本文件或目标提交中版本文件的完整内容.
.OUTPUTS Version、Tag 和 Prerelease.
#>
function ConvertFrom-PbiVersionText([string]$Text) {
    try {
        return ConvertFrom-PbiReleaseTag ([regex]::Replace($Text, '\r?\n\z', ''))
    } catch {
        throw "version 必须包含单行完整发布标签: $($_.Exception.Message)"
    }
}

<#
.SYNOPSIS 仅从根目录 version 读取当前构建身份; 文件缺失或格式错误时抛错.
.PARAMETER RepositoryRoot 包含 version 的仓库根目录.
.OUTPUTS Version、Tag 和 Prerelease.
#>
function Get-PbiCurrentRelease([string]$RepositoryRoot) {
    $path = Join-Path $RepositoryRoot 'version'
    if (![IO.File]::Exists($path)) { throw '缺少根目录 version, 请先使用 git savetag --prepare 生成' }
    return ConvertFrom-PbiVersionText ([IO.File]::ReadAllText($path))
}

<#
.SYNOPSIS 解析最新 CHANGELOG 章节, 同时校验其版本、日期、唯一性和实际正文.
.PARAMETER Lines 工作区或目标提交的日志行.
.OUTPUTS Release 和 Notes; 缺少有效章节时抛错.
#>
function Get-PbiChangelogReleaseSpec([string[]]$Lines) {
    $heading = $Lines | Where-Object { $_ -cmatch '^##\s' } | Select-Object -First 1
    if ($heading -cnotmatch '^## \[([^\]]+)\] - \d{4}-\d{2}-\d{2}$') { throw 'CHANGELOG 版本标题格式无效, 必须精确使用 ## [vX.Y.Z] - YYYY-MM-DD 或 ## [vX.Y.Z-rcN] - YYYY-MM-DD' }
    $release = ConvertFrom-PbiReleaseTag $Matches[1]
    [pscustomobject]@{ Release = $release; Notes = Get-PbiReleaseSection $Lines $release.Tag }
}

<#
.SYNOPSIS 验证工作区最新发布说明后, 原子写入单行 UTF-8 version.
.PARAMETER RepositoryRoot 包含工作区 CHANGELOG.md 的仓库根目录.
.PARAMETER Branch 当前发布分支, 决定允许的标签类型.
.OUTPUTS Version、Tag 和 Prerelease; 输入无效或写入失败时抛错.
#>
function Initialize-PbiReleaseVersion([string]$RepositoryRoot, [string]$Branch) {
    if ($Branch -cnotin @('main', 'v2')) { throw '发布仅支持 main 或 v2 分支' }
    $release = (Get-PbiChangelogReleaseSpec ([IO.File]::ReadAllLines((Join-Path $RepositoryRoot 'CHANGELOG.md')))).Release
    if ($Branch -ceq 'v2' -and !$release.Prerelease) { throw 'v2 分支仅允许 vX.Y.Z-rcN 预发布标签' }
    if ($Branch -ceq 'main' -and $release.Prerelease) { throw 'main 分支仅允许 vX.Y.Z 正式标签' }
    $path = Join-Path $RepositoryRoot 'version'
    $temporary = Join-Path $RepositoryRoot ('.version-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($temporary, "$($release.Tag)`n")
        [IO.File]::Move($temporary, $path, $true)
    } finally {
        [IO.File]::Delete($temporary)
    }
    return $release
}

<#
.SYNOPSIS 读取指定提交的文件全文, 保留行尾以便执行与本地版本文件相同的校验.
.PARAMETER RepositoryRoot Git 仓库根目录.
.PARAMETER Commit 已校验的完整提交 ID.
.PARAMETER File 发布元数据的根目录文件名.
.OUTPUTS 解码的文件内容; 读取失败时抛错.
#>
function Read-PbiCommitFile([string]$RepositoryRoot, [string]$Commit, [string]$File) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = $RepositoryRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    foreach ($argument in @('--no-replace-objects', 'show', "${Commit}:$File")) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        $content = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        $null = $errorRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "无法读取发布提交中的 $File (Git 退出码 $($process.ExitCode))" }
        return $content
    } finally { $process.Dispose() }
}

<#
.SYNOPSIS 从同一个 Git 提交核验 version、CHANGELOG 和可选预期标签, 返回精确发布上下文与说明.
.PARAMETER RepositoryRoot Git 仓库根目录.
.PARAMETER Commit 已冻结的完整提交 ID.
.PARAMETER ExpectedTag 可选外部 Git 标签, 必须与提交中的两处版本完全一致.
.OUTPUTS Context 和 Notes; 文件缺失、无效或身份不一致时抛错.
#>
function Get-PbiCommitReleaseSpec([string]$RepositoryRoot, [string]$Commit, [string]$ExpectedTag) {
    if ($Commit -cnotmatch '\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z') { throw '发布提交 ID 无效' }
    $release = ConvertFrom-PbiVersionText (Read-PbiCommitFile $RepositoryRoot $Commit 'version')
    $changelog = Get-PbiChangelogReleaseSpec ((Read-PbiCommitFile $RepositoryRoot $Commit 'CHANGELOG.md') -split '\r\n|\r|\n')
    if ($release.Tag -cne $changelog.Release.Tag) { throw '提交的 version 与 CHANGELOG 最新发布标签不一致, 请运行 git savetag --prepare 并提交' }
    if ($PSBoundParameters.ContainsKey('ExpectedTag') -and $ExpectedTag -cne $release.Tag) { throw 'Git 标签与提交的 version 和 CHANGELOG 最新发布标签不一致' }
    [pscustomobject]@{ Context = New-PbiReleaseContext $release -Tagged; Notes = $changelog.Notes }
}

<#
.SYNOPSIS 将 Git ref 映射到构建或发布策略; 只有新标签发布读取对应提交中的 CHANGELOG.
.PARAMETER RepositoryRoot 仓库根目录; 标签发布需要对应的 Git 标签及检出提交.
.PARAMETER Ref GitHub 完整 ref; PR merge ref 仅构建, 稳定标签要求 main, rcN 标签要求 v2.
.OUTPUTS 包含 Version、ArchiveName、InstallerName、ReleaseTag、Prerelease、SourceBranch 的对象; 未知 ref 或版本不符抛错.
.NOTES 本函数只确定策略, 调用者仍须核验提交属于 SourceBranch, 不能以标签名证明提交归属.
#>
function Get-PbiReleaseContext([string]$RepositoryRoot, [string]$Ref) {
    if ($Ref.StartsWith('refs/tags/', [StringComparison]::Ordinal)) {
        $tag = $Ref.Substring('refs/tags/'.Length)
        $null = ConvertFrom-PbiReleaseTag $tag
        $PSNativeCommandUseErrorActionPreference = $false
        $type = & git --no-replace-objects -C $RepositoryRoot cat-file -t $Ref 2>$null
        if ($LASTEXITCODE -ne 0 -or $type -cne 'tag') { throw '必须使用已存在的注释标签' }
        $commit = & git --no-replace-objects -C $RepositoryRoot rev-parse --verify "${Ref}^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) { throw '无法解析 Git 发布标签的目标提交' }
        $head = & git --no-replace-objects -C $RepositoryRoot rev-parse --verify HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or $head -cne $commit) { throw 'Git 发布标签目标与当前检出的提交不一致' }
        return (Get-PbiCommitReleaseSpec $RepositoryRoot $commit $tag).Context
    }
    $branch = ''
    if ($Ref -cmatch '^refs/heads/(main|v2)$') { $branch = $Matches[1] }
    elseif ($Ref -cnotmatch '^refs/pull/[1-9][0-9]*/merge$') { throw '发布 ref 无效' }
    return New-PbiReleaseContext (Get-PbiCurrentRelease $RepositoryRoot) -SourceBranch $branch
}

<#
.SYNOPSIS 使用核验后的发布身份生成 ZIP 和安装器共用名称, 历史 desktop 发行保留数字文件名.
.PARAMETER Release Get-PbiCurrentRelease 或原标签解析的发布身份.
.PARAMETER DotNetMode 本轮打包的部署模式.
.OUTPUTS 不含扩展名和安装器后缀的归档基础名称; 模式不受支持时抛错.
#>
function Get-PbiPackageBaseName($Release, [ValidateSet('with-dotnet', 'without-dotnet')][string]$DotNetMode) {
    $fileVersion = if ($Release.Tag.StartsWith('desktop-v', [StringComparison]::Ordinal)) { $Release.Version } else { $Release.Tag }
    return "pbi-utils-$fileVersion-win-x64-$DotNetMode"
}

<#
.SYNOPSIS 将已核验的发布身份映射到归档、安装器名称及发布分支.
.PARAMETER Release 已核验的发布身份.
.PARAMETER SourceBranch 非标签构建的可选来源分支.
.PARAMETER Tagged 表示已选定 Git 发布标签, 启用发布所需标签和分支信息.
.OUTPUTS 构建或发布上下文.
#>
function New-PbiReleaseContext($Release, [string]$SourceBranch, [switch]$Tagged) {
    $baseName = Get-PbiPackageBaseName $Release 'without-dotnet'
    if ($Tagged) { $SourceBranch = if ($Release.Prerelease) { 'v2' } else { 'main' } }
    [pscustomobject]@{
        Version = $Release.Version; ArchiveName = "$baseName.zip"; InstallerName = "$baseName-setup.exe"
        ReleaseTag = if ($Tagged) { $Release.Tag } else { '' }
        Prerelease = $Tagged -and $Release.Prerelease; SourceBranch = $SourceBranch
    }
}

<#
.SYNOPSIS 从 CHANGELOG 行中提取唯一有效的精确章节正文.
.PARAMETER Lines 本地或原标签的 CHANGELOG 行.
.PARAMETER Tag 已校验格式的精确发布标签.
.OUTPUTS 原章节正文; 章节缺失或重复、日期无效及内容为空时抛错.
#>
function Get-PbiReleaseSection([string[]]$Lines, [string]$Tag) {
    $header = '^##\s+\[' + [regex]::Escape($Tag) + '\]'
    $indices = @(for ($index = 0; $index -lt $Lines.Count; $index++) { if ($Lines[$index] -cmatch $header) { $index } })
    if ($indices.Count -gt 1) { throw 'CHANGELOG 版本章节重复' }
    if ($indices.Count -eq 0) { throw 'CHANGELOG 缺少精确发布章节' }
    $start = $indices[0]
    $marker = '^## \[' + [regex]::Escape($Tag) + '\] - (\d{4}-\d{2}-\d{2})$'
    if ($Lines[$start] -cnotmatch $marker) { throw 'CHANGELOG 版本标题格式无效' }
    try { $null = [DateTime]::ParseExact($Matches[1], 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) }
    catch [System.Management.Automation.MethodInvocationException] { throw 'CHANGELOG 发布日期无效' }
    $section = [Collections.Generic.List[string]]::new()
    for ($index = $start + 1; $index -lt $Lines.Count -and $Lines[$index] -notmatch '^##\s'; $index++) { $section.Add($Lines[$index]) }
    $notes = ($section -join "`n").Trim()
    $content = [regex]::Replace($notes, '(?s)<!--.*?(?:-->|\z)', '')
    $contentLines = @($content -split '\r?\n' | Where-Object { $_.Trim() -and $_ -notmatch '^\s*#' -and $_ -notmatch '^\s*(?:[-*+]\s*)?$' })
    if (!$contentLines.Count) { throw 'CHANGELOG 变更说明不能为空' }
    return $notes + "`n"
}

<#
.SYNOPSIS 给原正文追加实际 EXE 的 64 位十六进制摘要; 摘要非法时抛错.
.PARAMETER Notes 原发布章节正文.
.PARAMETER InstallerName 该发布身份实际分发的 EXE 名称.
.PARAMETER InstallerSha256 从该 EXE 字节计算的摘要.
.OUTPUTS 带精确安装器名称和摘要的发布说明.
#>
function Add-PbiInstallerHash([string]$Notes, [string]$InstallerName, [string]$InstallerSha256) {
    if ($InstallerSha256 -cnotmatch '\A[0-9A-Fa-f]{64}\z') { throw '安装器 SHA256 必须为精确 64 位十六进制摘要' }
    $installerHash = $InstallerSha256.ToUpperInvariant()
    return $Notes + "`n### $InstallerName SHA256`n`n" + '```text' + "`n$installerHash`n" + '```' + "`n"
}

<#
.SYNOPSIS 输出当前精确发布章节及可选的安装器摘要.
.PARAMETER RepositoryRoot 包含 version 和 CHANGELOG.md 的仓库根目录.
.PARAMETER Tag 当前精确发布标签.
.PARAMETER InstallerSha256 可选的实际 EXE 摘要.
.OUTPUTS 正文; 标签、章节或摘要无效时抛错.
#>
function Get-PbiReleaseNotes([string]$RepositoryRoot, [string]$Tag, [string]$InstallerSha256) {
    $release = Get-PbiCurrentRelease $RepositoryRoot
    $changelog = Get-PbiChangelogReleaseSpec ([IO.File]::ReadAllLines((Join-Path $RepositoryRoot 'CHANGELOG.md')))
    if ($Tag -cne $release.Tag -or $Tag -cne $changelog.Release.Tag) { throw '发布标签与 version 或 CHANGELOG 最新章节不一致' }
    if ($PSBoundParameters.ContainsKey('InstallerSha256')) {
        return Add-PbiInstallerHash $changelog.Notes (New-PbiReleaseContext $release -Tagged).InstallerName $InstallerSha256
    }
    return $changelog.Notes
}

<#
.SYNOPSIS 从不可变的原发布标签读取版本和说明, 避免运行分支升级影响已有发行版复验.
.PARAMETER RepositoryRoot 含原发布标签的 Git 仓库目录.
.PARAMETER Tag 精确发布标签.
.PARAMETER InstallerSha256 可选的实际 EXE SHA256, 不得传 ZIP 摘要.
.OUTPUTS Context 和 Notes; 标签读取或原发布规范无效时抛错.
#>
function Get-PbiTaggedReleaseSpec([string]$RepositoryRoot, [string]$Tag, [string]$InstallerSha256) {
    $release = ConvertFrom-PbiReleaseTag $Tag -Historical
    $PSNativeCommandUseErrorActionPreference = $false
    $commit = & git --no-replace-objects -C $RepositoryRoot rev-parse --verify "refs/tags/${Tag}^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw '原发布标签的目标提交读取失败' }
    $lines = (Read-PbiCommitFile $RepositoryRoot $commit 'CHANGELOG.md') -split '\r\n|\r|\n'
    $context = New-PbiReleaseContext $release -Tagged
    $notes = Get-PbiReleaseSection $lines $Tag
    if ($PSBoundParameters.ContainsKey('InstallerSha256')) { $notes = Add-PbiInstallerHash $notes $context.InstallerName $InstallerSha256 }
    [pscustomobject]@{ Context = $context; Notes = $notes }
}

<#
.SYNOPSIS 用本地 SHA256 侧文件校验当前 without-dotnet 安装器, 创建仅含 EXE 的 ZIP 并核对归档原字节.
.PARAMETER RepositoryRoot 仓库根目录, 安装器必须已由现有 package/release 成功生成.
.PARAMETER Destination ZIP 输出目录; 临时归档校验完成后才替换本版本 ZIP.
.PARAMETER ExpectedTag 可选的已核验 Git 发布标签, 必须与 version 一致.
.OUTPUTS ZIP 的绝对路径; 缺失、空安装器、错误校验和、归档写入/校验失败时抛错, 不交付半包.
#>
function New-PbiReleaseArchive([string]$RepositoryRoot, [string]$Destination, [string]$ExpectedTag) {
    $release = Get-PbiCurrentRelease $RepositoryRoot
    if ($ExpectedTag -and $ExpectedTag -cne $release.Tag) { throw '归档 Git 标签与 version 不一致' }
    $context = New-PbiReleaseContext $release -Tagged
    $installerName = $context.InstallerName
    $baseName = [IO.Path]::GetFileNameWithoutExtension($installerName)
    $packageRoot = Join-Path $RepositoryRoot 'artifacts/release/win-x64/without-dotnet'
    $exe = Join-Path $packageRoot $installerName
    $checksum = Join-Path $packageRoot "$baseName.sha256"
    $record = Get-Content -LiteralPath (Join-Path $packageRoot "$baseName.json") -Raw | ConvertFrom-Json -AsHashtable
    foreach ($path in @($exe, $checksum)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) { throw '缺少非空的当前版本安装器或 SHA256' }
    }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ($record.releaseTag -cne $release.Tag -or $record.installer -cne $installerName -or $record.sha256 -cne $hash) { throw '安装包记录的发布标签、文件名或 SHA256 与当前归档输入不一致' }
    $expectedChecksum = "$hash  $installerName"
    if ((Get-Content -LiteralPath $checksum -Raw).Trim() -cne $expectedChecksum) { throw '安装器 SHA256 或文件名不匹配' }
    $null = New-Item -ItemType Directory -Path $Destination -Force
    $destinationRoot = (Get-Item -LiteralPath $Destination).FullName
    $archivePath = Join-Path $destinationRoot $context.ArchiveName
    $temporary = Join-Path $destinationRoot ('.release-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Compress-Archive -LiteralPath $exe -DestinationPath $temporary -CompressionLevel Optimal
        $archive = [IO.Compression.ZipFile]::OpenRead($temporary)
        try {
            if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne $installerName) { throw 'ZIP 必须在根目录仅包含当前安装器 EXE' }
            $stream = $archive.GetEntry($installerName).Open()
            try { $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($actualHash -cne $hash) { throw '归档期间安装器发生变化' }
            if ($actualHash -cne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash) { throw 'ZIP 内容与当前安装器不一致' }
            # 侧文件只用于本地输入与并发校验, 不作为 ZIP 条目分发; 摘要由发布说明单独呈现.
            if ((Get-Content -LiteralPath $checksum -Raw).Trim() -cne $expectedChecksum) { throw '归档期间 SHA256 发生变化' }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $archivePath -Force
        return $archivePath
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Export-ModuleMember -Function Get-PbiCurrentRelease, Initialize-PbiReleaseVersion, Get-PbiCommitReleaseSpec, Get-PbiPackageBaseName, Get-PbiReleaseContext, Get-PbiReleaseNotes, Get-PbiTaggedReleaseSpec, Add-PbiInstallerHash, New-PbiReleaseArchive
