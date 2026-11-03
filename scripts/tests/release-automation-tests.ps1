# FilePath    : pbi-utils\scripts\tests\release-automation-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离目录验证发布标签、安装器摘要说明、单文件 ZIP 与校验和拒绝行为.

<# .SYNOPSIS 不访问远端或执行安装, 任一发布行为断言不满足即失败. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../ReleaseAutomation.psm1') -Force
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-release-' + [Guid]::NewGuid().ToString('N'))

<# .SYNOPSIS 校验实际行为, 条件不满足时抛出说明. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 执行预期失败的操作, 正常返回则阻止测试通过. #>
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert $rejected '预期拒绝的发布输入被接受'
}

try {
    $null = New-Item -ItemType Directory -Path $sandbox
    '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>' | Set-Content -LiteralPath (Join-Path $sandbox 'Directory.Build.props')
    @'
# 更新日志

## [desktop-v0.1.0-v2.1] - 2026-11-03

### Changed

- 构建验证与 ZIP 安装包.

## [desktop-v0.1.0] - 2026-11-03

### Added

- 首个正式版.
'@ | Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md')
    $validChangelog = Get-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Raw
    $branch = Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/heads/v2'
    Assert (!$branch.ReleaseTag -and $branch.SourceBranch -ceq 'v2') 'v2 push 必须只构建'
    $stable = Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/tags/desktop-v0.1.0'
    Assert (!$stable.Prerelease -and $stable.SourceBranch -ceq 'main') '正式标签必须属于 main'
    $preview = Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/tags/desktop-v0.1.0-v2.1'
    Assert ($preview.Prerelease -and $preview.SourceBranch -ceq 'v2') 'v2 标签必须为预发布'
    foreach ($ref in @('refs/tags/v0.1.0', 'refs/tags/desktop-v0.2.0', 'refs/tags/desktop-v0.1.0-v2.0', 'refs/tags/desktop-v0.1.0-rc.1', 'refs/heads/other')) {
        Assert-Rejected { Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref $ref }
    }
    $notes = Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag
    Assert ($notes.Contains('构建验证与 ZIP') -and !$notes.Contains('首个正式版')) '更新说明必须精确选中当前章节'
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag 'desktop-v0.1.0-v2.2' }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value "`n## [desktop-v0.1.0-v2.1] - 2026-11-03`n重复章节"
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag }
    Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value "## [desktop-v0.1.0-v2.1] - 2026-11-03`n`n### Changed`n"
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value '<!-- 仅有注释, 不能当作发版说明. -->'
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value '- '
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag }
    Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value $validChangelog

    $package = Join-Path $sandbox 'artifacts/release/win-x64/without-dotnet'
    $null = New-Item -ItemType Directory -Path $package -Force
    $name = 'pbi-utils-0.1.0-win-x64-without-dotnet-setup'
    $exe = Join-Path $package "$name.exe"
    [IO.File]::WriteAllBytes($exe, [byte[]](1, 4, 7, 11))
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    "$hash  $name.exe" | Set-Content -LiteralPath (Join-Path $package "$name.sha256") -Encoding ascii
    foreach ($extra in @("$name.json", '.package.lock', 'old-setup.exe')) {
        'excluded' | Set-Content -LiteralPath (Join-Path $package $extra)
    }
    $zip = New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist')
    Assert ([IO.Path]::GetFileName($zip) -ceq 'pbi-utils-0.1.0-win-x64-without-dotnet.zip') 'ZIP 名必须匹配更新下载契约'
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries.FullName | Sort-Object)
        Assert ($entries.Count -eq 1 -and $entries[0] -ceq "$name.exe") 'ZIP 根目录必须仅包含当前 EXE, 不含 SHA256 或其他文件'
        $reader = [IO.BinaryReader]::new($archive.GetEntry("$name.exe").Open())
        try { Assert (($reader.ReadBytes(4) -join ',') -ceq '1,4,7,11') 'ZIP 内容必须保持安装器原字节' } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $zip).Hash
    Assert ($hash -cne $zipHash) '夹具必须区分安装器摘要与 ZIP 摘要'
    Assert ((Get-Content -LiteralPath (Join-Path $package "$name.sha256") -Raw).Trim() -ceq "$hash  $name.exe") '本地 SHA256 侧文件仍保留且不加入 ZIP'
    $hashNotes = Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag -InstallerSha256 $hash.ToLowerInvariant()
    $expectedHashNotes = $notes + "`n### $name.exe SHA256`n`n" + '```text' + "`n$hash`n" + '```' + "`n"
    Assert ($hashNotes -ceq $expectedHashNotes) '发布说明必须以精确 EXE 名和 SHA256 为标题, text 代码块仅含大写 64 位摘要'
    Assert (!$hashNotes.Contains($zipHash) -and !$hashNotes.Contains([IO.Path]::GetFileName($zip))) '安装器摘要说明不能混入 ZIP 哈希或 ZIP 文件名'
    Assert ((Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag) -ceq $notes) '构建前省略摘要时必须保持原 CHANGELOG 章节'
    foreach ($invalid in @('', ' ', ('A' * 63), ('A' * 65), ('g' * 64), "$hash`n", " $hash")) {
        Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.ReleaseTag -InstallerSha256 $invalid }
    }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value "`n## [desktop-v0.2.0] - 2026-11-03`n- 其他产品版本."
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag 'desktop-v0.2.0' -InstallerSha256 $hash }
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag 'desktop-v0.2.0' }
    'invalid checksum' | Set-Content -LiteralPath (Join-Path $package "$name.sha256")
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -ceq $zipHash) '失败不得覆盖原 ZIP'
    "$([string]::new('0', 64))  $name.exe" | Set-Content -LiteralPath (Join-Path $package "$name.sha256")
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -ceq $zipHash) '错误摘要不得覆盖原 ZIP'
    "$hash  wrong.exe" | Set-Content -LiteralPath (Join-Path $package "$name.sha256")
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -ceq $zipHash) '错误安装器名不得覆盖原 ZIP'
    Remove-Item -LiteralPath (Join-Path $package "$name.sha256")
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -ceq $zipHash) '缺少 SHA256 不得覆盖原 ZIP'
    "$hash  $name.exe" | Set-Content -LiteralPath (Join-Path $package "$name.sha256")
    [IO.File]::WriteAllBytes($exe, [byte[]]@())
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -ceq $zipHash) '空安装器不得覆盖原 ZIP'
    Remove-Item -LiteralPath $exe
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -ceq $zipHash) '缺少安装器不得覆盖原 ZIP'
    Write-Output 'PASS release context, notes and exact ZIP archive'
} finally {
    $absolute = [IO.Path]::GetFullPath($sandbox)
    if ([IO.Path]::GetDirectoryName($absolute) -cne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-release-[0-9a-f]{32}$') { throw '测试清理目录越界' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
