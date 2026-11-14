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
$savedGitEnvironment = @{}
foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GIT_*' })) {
    $savedGitEnvironment[$variable.Name] = $variable.Value
}
$ownsSandbox = $false

<# .SYNOPSIS 校验实际行为, 条件不满足时抛出说明. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 执行预期失败的操作, 正常返回则阻止测试通过. #>
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert $rejected '预期拒绝的发布输入被接受'
}

<# .SYNOPSIS 仅在临时仓库执行 Git; 原生命令失败时抛错. #>
function Invoke-FixtureGit([string[]]$ArgumentList) {
    & git -C $sandbox @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "隔离 Git 命令失败: $($ArgumentList[0])" }
}

try {
    $null = New-Item -ItemType Directory -Path $sandbox
    $ownsSandbox = $true
    # 即使调用者指定了工作区、索引或配置, Git 子进程也只操作本轮夹具.
    foreach ($name in $savedGitEnvironment.Keys) { Remove-Item -LiteralPath "Env:$name" }
    $env:GIT_CONFIG_GLOBAL = Join-Path $sandbox 'empty-gitconfig'
    [IO.File]::WriteAllText($env:GIT_CONFIG_GLOBAL, '')
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:GIT_TERMINAL_PROMPT = '0'
    @'
# 更新日志

## [v0.1.0-rc1] - 2026-11-03

### Changed

- 构建验证与 ZIP 安装包.

## [v0.1.0] - 2026-11-03

### Added

- 首个正式版.

## [v0.1.0-rc9] - 2026-11-03

- 引入 version 之前的现行格式预发布说明.

## [desktop-v0.1.0-v2.7] - 2026-11-03

### Added

- 旧格式预发布说明.
'@ | Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md')
    $validChangelog = Get-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Raw
    $versionPath = Join-Path $sandbox 'version'
    [IO.File]::WriteAllText($versionPath, "v0.1.0-rc1`n")
    $changelogPath = Join-Path $sandbox 'CHANGELOG.md'
    Move-Item -LiteralPath $changelogPath -Destination (Join-Path $sandbox 'saved-notes.md')
    $current = Get-PbiCurrentRelease -RepositoryRoot $sandbox
    Assert ($current.Tag -ceq 'v0.1.0-rc1' -and $current.Version -ceq '0.1.0' -and $current.Prerelease) '缺少 CHANGELOG 时仍从 version 读取完整构建身份'
    $branch = Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/heads/v2'
    Assert (!$branch.ReleaseTag -and $branch.SourceBranch -ceq 'v2') '分支构建只读 version'
    $pr = Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/pull/42/merge'
    Assert (!$pr.ReleaseTag -and $pr.ArchiveName -ceq $branch.ArchiveName) 'PR 构建只读 version'
    Move-Item -LiteralPath (Join-Path $sandbox 'saved-notes.md') -Destination $changelogPath
    foreach ($invalidVersion in @('', " v0.1.0-rc1", 'v0.1.0-rc1 ', "v0.1.0-rc1`n`n", "v0.1.0-rc1`nv0.1.0-rc2", "v0.1.0-rc1`r", 'v65535.1.0', 'v0.1.0-rc2147483648')) {
        [IO.File]::WriteAllText($versionPath, $invalidVersion)
        Assert-Rejected { Get-PbiCurrentRelease -RepositoryRoot $sandbox }
    }
    Remove-Item -LiteralPath $versionPath
    Assert-Rejected { Get-PbiCurrentRelease -RepositoryRoot $sandbox }
    [IO.File]::WriteAllText($versionPath, "v0.1.0-rc1`n")
    foreach ($heading in @('## [Unreleased]', '## [v00.1.0]', '## [v65535.1.0]', '## [v0.1.0-rc0]', '## [v0.1.0-rc01]', '## [v0.1.0-rc2147483648]', '## [v0.1.0.1]', '## [desktop-v0.1.0-v2.8]')) {
        "$heading - 2026-11-03`n- 内容。`n$validChangelog" | Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md')
        Assert-Rejected { Initialize-PbiReleaseVersion -RepositoryRoot $sandbox -Branch v2 }
    }
    "## [v0.2.0] - 2026-11-14`n- 内容。`n$validChangelog" | Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md')
    $stable = Initialize-PbiReleaseVersion -RepositoryRoot $sandbox -Branch main
    Assert ($stable.Version -ceq '0.2.0' -and !$stable.Prerelease) 'main 的发布准备从 CHANGELOG 生成正式版本'
    $stableBuild = Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/heads/main'
    Assert ($stableBuild.ArchiveName -ceq 'pbi-utils-v0.2.0-win-x64-without-dotnet.zip') '正式版文件名必须带精确发布标签'
    $validChangelog | Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md')
    $preview = Initialize-PbiReleaseVersion -RepositoryRoot $sandbox -Branch v2
    foreach ($ref in @('refs/tags/desktop-v0.1.0-v2.1', 'refs/tags/v0.2.0', 'refs/tags/v0.1.0', 'refs/tags/v0.1.0-rc0', 'refs/tags/v0.1.0-rc.1', 'refs/heads/other')) {
        Assert-Rejected { Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref $ref }
    }
    $notes = Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag
    Assert ($notes.Contains('构建验证与 ZIP') -and !$notes.Contains('首个正式版')) '更新说明必须精确选中当前章节'
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag 'v0.1.0-rc2' }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value "`n## [v0.1.0-rc1] - 2026-11-03`n重复章节"
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag }
    Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value "## [v0.1.0-rc1] - 2026-11-03`n`n### Changed`n"
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value '<!-- 仅有注释, 不能当作发版说明. -->'
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value '- '
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag }
    Set-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value $validChangelog

    $template = Join-Path $sandbox 'empty-template'
    $null = New-Item -ItemType Directory -Path $template
    Invoke-FixtureGit @('init', '--quiet', '--initial-branch=v2', "--template=$template")
    foreach ($setting in @(@('user.name', 'Release Test'), @('user.email', 'release-test@example.invalid'), @('core.hooksPath', $template), @('core.autocrlf', 'false'), @('commit.gpgSign', 'false'), @('tag.gpgSign', 'false'))) {
        Invoke-FixtureGit @('config', '--local', $setting[0], $setting[1])
    }
    Invoke-FixtureGit @('add', '--', 'CHANGELOG.md')
    Invoke-FixtureGit @('commit', '--quiet', '-m', 'Historical releases before version')
    Invoke-FixtureGit @('tag', '--annotate', '-m', 'Historical desktop', 'desktop-v0.1.0-v2.7')
    Invoke-FixtureGit @('tag', '--annotate', '-m', 'Historical current-format tag', 'v0.1.0')
    Invoke-FixtureGit @('tag', '--annotate', '-m', 'Historical current-format prerelease', 'v0.1.0-rc9')
    $legacy = Get-PbiTaggedReleaseSpec -RepositoryRoot $sandbox -Tag 'v0.1.0'
    Assert ($legacy.Context.ReleaseTag -ceq 'v0.1.0' -and $legacy.Notes.Contains('首个正式版')) '历史现行格式标签也可缺少 version 并读取其对应章节'
    Assert-Rejected { Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/tags/v0.1.0' }

    [IO.File]::WriteAllText($versionPath, "v0.1.0-rc2`n")
    Invoke-FixtureGit @('add', '--', 'version')
    Invoke-FixtureGit @('commit', '--quiet', '-m', 'Commit inconsistent version')
    Invoke-FixtureGit @('tag', '--annotate', '-m', 'Version and CHANGELOG mismatch', 'v0.1.0-rc2')
    Assert-Rejected { Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/tags/v0.1.0-rc2' }

    [IO.File]::WriteAllText($versionPath, "v0.1.0-rc1`n")
    Invoke-FixtureGit @('add', '--', 'version')
    Invoke-FixtureGit @('commit', '--quiet', '-m', 'Commit matching version')
    Invoke-FixtureGit @('tag', '--annotate', '-m', 'Tag and version mismatch', 'v0.1.0-rc3')
    Invoke-FixtureGit @('tag', '--annotate', '-m', 'Release candidate', 'v0.1.0-rc1')
    $head = Invoke-FixtureGit @('rev-parse', 'HEAD')
    Invoke-FixtureGit @('update-ref', 'refs/remotes/origin/v2', $head)
    Assert-Rejected { Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/tags/v0.1.0-rc2' }
    Assert-Rejected { Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/tags/v0.1.0-rc3' }
    $tagged = Get-PbiReleaseContext -RepositoryRoot $sandbox -Ref 'refs/tags/v0.1.0-rc1'
    Assert ($tagged.Prerelease -and $tagged.ReleaseTag -ceq $preview.Tag -and $tagged.SourceBranch -ceq 'v2') '匹配的已提交预发布身份可进入标签构建'
    Assert ($tagged.ArchiveName -ceq $branch.ArchiveName) '普通构建和标签构建使用相同的归档名称'

    $package = Join-Path $sandbox 'artifacts/release/win-x64/without-dotnet'
    $null = New-Item -ItemType Directory -Path $package -Force
    $name = 'pbi-utils-v0.1.0-rc1-win-x64-without-dotnet-setup'
    $exe = Join-Path $package "$name.exe"
    [IO.File]::WriteAllBytes($exe, [byte[]](1, 4, 7, 11))
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    "$hash  $name.exe" | Set-Content -LiteralPath (Join-Path $package "$name.sha256") -Encoding ascii
    @{ releaseTag = $preview.Tag; installer = "$name.exe"; sha256 = $hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package "$name.json")
    foreach ($extra in @('.package.lock', 'old-setup.exe')) {
        'excluded' | Set-Content -LiteralPath (Join-Path $package $extra)
    }
    $zip = New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist')
    Assert ([IO.Path]::GetFileName($zip) -ceq 'pbi-utils-v0.1.0-rc1-win-x64-without-dotnet.zip') '预发布 ZIP 名必须保留 v 前缀和 rc 轮次'
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries.FullName | Sort-Object)
        Assert ($entries.Count -eq 1 -and $entries[0] -ceq "$name.exe") 'ZIP 根目录必须仅包含当前 EXE, 不含 SHA256 或其他文件'
        $reader = [IO.BinaryReader]::new($archive.GetEntry("$name.exe").Open())
        try { Assert (($reader.ReadBytes(4) -join ',') -ceq '1,4,7,11') 'ZIP 内容必须保持安装器原字节' } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $zip).Hash
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') -ExpectedTag 'v0.1.0-rc2' }
    @{ releaseTag = 'v0.1.0-rc2'; installer = "$name.exe"; sha256 = $hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package "$name.json")
    Assert-Rejected { New-PbiReleaseArchive -RepositoryRoot $sandbox -Destination (Join-Path $sandbox 'dist') }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -ceq $zipHash) '版本标签不符时不得覆盖现有 ZIP'
    @{ releaseTag = $preview.Tag; installer = "$name.exe"; sha256 = $hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package "$name.json")
    Assert ($hash -cne $zipHash) '夹具必须区分安装器摘要与 ZIP 摘要'
    Assert ((Get-Content -LiteralPath (Join-Path $package "$name.sha256") -Raw).Trim() -ceq "$hash  $name.exe") '本地 SHA256 侧文件仍保留且不加入 ZIP'
    $hashNotes = Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag -InstallerSha256 $hash.ToLowerInvariant()
    $expectedHashNotes = $notes + "`n### $name.exe SHA256`n`n" + '```text' + "`n$hash`n" + '```' + "`n"
    Assert ($hashNotes -ceq $expectedHashNotes) '发布说明必须以精确 EXE 名和 SHA256 为标题, text 代码块仅含大写 64 位摘要'
    Assert (!$hashNotes.Contains($zipHash) -and !$hashNotes.Contains([IO.Path]::GetFileName($zip))) '安装器摘要说明不能混入 ZIP 哈希或 ZIP 文件名'
    # 使用隔离 Git 仓库和已有发行版夹具验证实际共享包脚本, 不访问远端.
    $bundleScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../.github/scripts/release-bundle.ps1'))
    & {
        $savedEnvironment = @{}
        foreach ($key in @('RELEASE_TAG', 'ARCHIVE_NAME', 'GITHUB_REPOSITORY')) { $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key) }
        <# .SYNOPSIS 返回封闭发行版夹具或复制测试 ZIP; 不允许其他外部请求. #>
        function gh {
            $global:LASTEXITCODE = 0
            if ($args[0] -ceq 'api') { return ($fixtureRelease | ConvertTo-Json -Depth 8) }
            if ($args[0] -ceq 'release' -and $args[1] -ceq 'download') {
                Copy-Item -LiteralPath $zip -Destination $args[[array]::IndexOf($args, '--dir') + 1]
                return
            }
            throw '未预期的 gh 请求'
        }
        Push-Location $sandbox
        try {
            $env:RELEASE_TAG = $preview.Tag
            $env:ARCHIVE_NAME = $branch.ArchiveName
            $env:GITHUB_REPOSITORY = 'fixture/repository'
            $historical = Get-PbiTaggedReleaseSpec -RepositoryRoot $sandbox -Tag 'desktop-v0.1.0-v2.7' -InstallerSha256 $hash
            Assert ($historical.Context.Prerelease -and $historical.Context.ArchiveName -ceq 'pbi-utils-0.1.0-win-x64-without-dotnet.zip' -and $historical.Notes.Contains('旧格式预发布说明')) '历史 desktop 标签保持其原始附件名'
            Assert ($historical.Context.InstallerName -ceq 'pbi-utils-0.1.0-win-x64-without-dotnet-setup.exe' -and $historical.Notes.Contains("### $($historical.Context.InstallerName) SHA256")) '历史发行说明沿用原安装器名称'
            $dist = Join-Path $sandbox 'dist'
            & $bundleScript -Mode Create -Directory $dist | Out-Null
            # 运行分支升级或修改说明后, 仍须使用原标签规范复验原包.
            'changed branch notes' | Set-Content (Join-Path $sandbox 'CHANGELOG.md')
            [IO.File]::WriteAllText($versionPath, "v0.2.0-rc1`n")
            & $bundleScript -Mode Verify -Directory $dist | Out-Null
            $notesPath = Join-Path $dist 'release-notes.md'
            $originalNotes = [IO.File]::ReadAllText($notesPath)
            [IO.File]::WriteAllText($notesPath, $originalNotes + 'tampered')
            Assert-Rejected { & $bundleScript -Mode Verify -Directory $dist }
            [IO.File]::WriteAllText($notesPath, $originalNotes)
            $fixtureRelease = @{
                tag_name = $env:RELEASE_TAG; name = $env:RELEASE_TAG; draft = $false; prerelease = $true; body = $originalNotes
                assets = @(@{ name = $env:ARCHIVE_NAME; size = (Get-Item $zip).Length; digest = 'sha256:' + $zipHash.ToLowerInvariant()
                        browser_download_url = "https://github.com/$env:GITHUB_REPOSITORY/releases/download/$env:RELEASE_TAG/$env:ARCHIVE_NAME" })
            }
            & $bundleScript -Mode Download -Directory (Join-Path $sandbox 'download') | Out-Null
            Assert ((Get-FileHash (Join-Path $sandbox "download/$env:ARCHIVE_NAME")).Hash -ceq $zipHash) '重试必须复用原 ZIP 字节'
            & $bundleScript -Mode Verify -Directory (Join-Path $sandbox 'download') -Reuse | Out-Null
            $fixtureRelease.assets[0].digest = 'sha256:' + ('0' * 64)
            Assert-Rejected { & $bundleScript -Mode Download -Directory (Join-Path $sandbox 'bad-digest') }
            $fixtureRelease.assets += $fixtureRelease.assets[0]
            Assert-Rejected { & $bundleScript -Mode Download -Directory (Join-Path $sandbox 'duplicate-asset') }
            Assert (!(Test-Path (Join-Path $sandbox 'duplicate-asset'))) '重复附件必须在下载前拒绝'

            # 现行格式的历史预发布在原提交中也可能没有 version.
            $env:RELEASE_TAG = 'v0.1.0-rc9'
            $old = Get-PbiTaggedReleaseSpec -RepositoryRoot $sandbox -Tag $env:RELEASE_TAG -InstallerSha256 $hash
            $env:ARCHIVE_NAME = $old.Context.ArchiveName
            $zip = Join-Path $sandbox $env:ARCHIVE_NAME
            $oldExe = Join-Path $sandbox $old.Context.InstallerName
            Copy-Item -LiteralPath $exe -Destination $oldExe
            Compress-Archive -LiteralPath $oldExe -DestinationPath $zip
            $fixtureRelease.tag_name = $fixtureRelease.name = $env:RELEASE_TAG
            $fixtureRelease.body = $old.Notes
            $fixtureRelease.assets = @(@{ name = $env:ARCHIVE_NAME; size = (Get-Item $zip).Length; digest = 'sha256:' + (Get-FileHash $zip).Hash.ToLowerInvariant()
                    browser_download_url = "https://github.com/$env:GITHUB_REPOSITORY/releases/download/$env:RELEASE_TAG/$env:ARCHIVE_NAME" })
            $reuseDirectory = Join-Path $sandbox 'old-release'
            & $bundleScript -Mode Download -Directory $reuseDirectory | Out-Null
            & $bundleScript -Mode Verify -Directory $reuseDirectory -Reuse | Out-Null
            Assert-Rejected { & $bundleScript -Mode Verify -Directory $reuseDirectory }
        } finally {
            $validChangelog | Set-Content (Join-Path $sandbox 'CHANGELOG.md')
            [IO.File]::WriteAllText($versionPath, "v0.1.0-rc1`n")
            Pop-Location
            foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key]) }
        }
    }
    Assert ((Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag) -ceq $notes) '构建前省略摘要时必须保持原 CHANGELOG 章节'
    foreach ($invalid in @('', ' ', ('A' * 63), ('A' * 65), ('g' * 64), "$hash`n", " $hash")) {
        Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag $preview.Tag -InstallerSha256 $invalid }
    }
    Add-Content -LiteralPath (Join-Path $sandbox 'CHANGELOG.md') -Value "`n## [v0.2.0] - 2026-11-03`n- 其他产品版本."
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag 'v0.2.0' -InstallerSha256 $hash }
    Assert-Rejected { Get-PbiReleaseNotes -RepositoryRoot $sandbox -Tag 'v0.2.0' }
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
    foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GIT_*' })) { Remove-Item -LiteralPath "Env:$($variable.Name)" }
    foreach ($name in $savedGitEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedGitEnvironment[$name], 'Process') }
    $absolute = [IO.Path]::GetFullPath($sandbox)
    if ([IO.Path]::GetDirectoryName($absolute) -cne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-release-[0-9a-f]{32}$') { throw '测试清理目录越界' }
    if ($ownsSandbox -and (Test-Path -LiteralPath $absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
