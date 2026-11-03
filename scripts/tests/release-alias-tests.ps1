# FilePath    : pbi-utils\scripts\tests\release-alias-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 使用隔离本地 Git 仓库验证发布别名的标签、预览、原子推送与拒绝边界.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $root '.gitalias/savetag.sh'
if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw '发布脚本尚未实现: .gitalias/savetag.sh' }
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-release-alias-' + [Guid]::NewGuid().ToString('N'))
$savedGitEnvironment = @{}
foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GIT_*' })) { $savedGitEnvironment[$variable.Name] = $variable.Value }
$ownsSandbox = $false
$passed = 0
$savedConsoleInputEncoding = [Console]::InputEncoding
$savedConsoleOutputEncoding = [Console]::OutputEncoding
$savedPipelineEncoding = $OutputEncoding

<# .SYNOPSIS 在指定隔离仓库运行 Git, 返回退出码和输出; 非零码仅由调用方按场景判定. #>
function Invoke-TestGit([string]$Repository, [string[]]$Arguments) {
    $output = @(& git -C $Repository @Arguments 2>&1)
    return @{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}

<# .SYNOPSIS 运行隔离准备命令, 参数为仓库和 Git 参数; 非零退出时终止测试. #>
function Invoke-RequiredGit([string]$Repository, [string[]]$Arguments) {
    $result = Invoke-TestGit $Repository $Arguments
    if ($result.Code -ne 0) { throw "隔离 Git 准备失败: $($result.Output)" }
    return $result.Output
}

<# .SYNOPSIS 创建独立工作仓库及 bare origin, 参数为场景名、分支、精确 CHANGELOG 标签和产品版本; 返回本地路径. #>
function New-ReleaseFixture([string]$Name, [string]$Branch = 'v2', [string]$Tag = 'desktop-v0.1.0-v2.1', [string]$Version = '0.1.0') {
    $directory = Join-Path $sandbox $Name
    $repository = Join-Path $directory 'repository'
    $remote = Join-Path $directory 'origin.git'
    $null = New-Item -ItemType Directory -Path $repository -Force
    $null = Invoke-RequiredGit $directory @('init', '--quiet', '--bare', "--template=$template", $remote)
    $null = Invoke-RequiredGit $directory @('init', '--quiet', "--initial-branch=$Branch", "--template=$template", $repository)
    foreach ($setting in @(@('user.name', 'Release Test'), @('user.email', 'release-test@example.invalid'), @('core.autocrlf', 'false'), @('alias.savetag', '!bash ./.gitalias/savetag.sh'))) {
        $null = Invoke-RequiredGit $repository @('config', '--local', $setting[0], $setting[1])
    }
    $null = New-Item -ItemType Directory -Path (Join-Path $repository '.gitalias')
    Copy-Item -LiteralPath $source -Destination (Join-Path $repository '.gitalias/savetag.sh')
    [IO.File]::WriteAllText((Join-Path $repository 'Directory.Build.props'), "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>")
    [IO.File]::WriteAllText((Join-Path $repository 'CHANGELOG.md'), "# Changelog`n`n## [$Tag] - 2026-11-03`n`n### Added`n`n- 隔离发布测试.`n")
    [IO.File]::WriteAllText((Join-Path $repository 'payload.txt'), "fixture`n")
    $null = Invoke-RequiredGit $repository @('add', '--', '.')
    $null = Invoke-RequiredGit $repository @('commit', '--quiet', '--message', 'Fixture baseline')
    $null = Invoke-RequiredGit $repository @('remote', 'add', 'origin', $remote)
    return @{ Repository = $repository; Remote = $remote; Branch = $Branch; Tag = $Tag }
}

<# .SYNOPSIS 提交隔离夹具当前变更, 参数为夹具; 准备失败时抛错. #>
function Save-Fixture([hashtable]$Fixture) {
    $null = Invoke-RequiredGit $Fixture.Repository @('add', '--', '.')
    $null = Invoke-RequiredGit $Fixture.Repository @('commit', '--quiet', '--message', 'Fixture change')
}

<# .SYNOPSIS 校验条件并计入通过数, 参数为条件和场景名; 不满足时抛错. #>
function Assert-ReleaseTest([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Host "PASS: $Name"
}

<# .SYNOPSIS 验证发布拒绝且未创建本地或远端标签, 参数为夹具与预期诊断片段; 不符合时抛错. #>
function Assert-ReleaseRejected([hashtable]$Fixture, [string]$Reason) {
    $result = Invoke-TestGit $Fixture.Repository @('savetag')
    Assert-ReleaseTest ($result.Code -ne 0 -and $result.Output.Contains($Reason)) "拒绝 $Reason"
    Assert-ReleaseTest ((Invoke-RequiredGit $Fixture.Repository @('tag', '--list')).Length -eq 0 -and (Invoke-RequiredGit $Fixture.Remote @('for-each-ref', '--format=%(refname)')).Length -eq 0) "$Reason 不修改 refs"
}

try {
    # 无窗口测试宿主可能默认使用 CP936; Bash 的 UTF-8 诊断与 C# wrapper 的 UTF-8 读流必须保持同一边界.
    $utf8 = [Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = $utf8
    [Console]::OutputEncoding = $utf8
    $OutputEncoding = $utf8
    $null = New-Item -ItemType Directory -Path $sandbox
    $ownsSandbox = $true
    $template = Join-Path $sandbox 'empty-template'
    $null = New-Item -ItemType Directory -Path $template
    # 隔离继承的仓库、索引、模板、凭据和全局配置, 所有推送仅指向本次创建的 bare origin.
    foreach ($name in $savedGitEnvironment.Keys) { Remove-Item -LiteralPath "Env:$name" }
    $env:GIT_CONFIG_GLOBAL = Join-Path $sandbox 'empty-gitconfig'
    [IO.File]::WriteAllText($env:GIT_CONFIG_GLOBAL, '')
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:GIT_TERMINAL_PROMPT = '0'

    $fixture = New-ReleaseFixture 'v2-success'
    $head = Invoke-RequiredGit $fixture.Repository @('rev-parse', 'HEAD')
    $subdirectory = Join-Path $fixture.Repository 'subdirectory'
    $null = New-Item -ItemType Directory -Path $subdirectory
    $preview = Invoke-TestGit $subdirectory @('savetag', '--dry-run')
    Assert-ReleaseTest ($preview.Code -eq 0 -and $preview.Output.Contains('DRY RUN') -and $preview.Output.Contains($fixture.Tag)) '子目录调用预览'
    Assert-ReleaseTest ((Invoke-RequiredGit $fixture.Repository @('tag', '--list')).Length -eq 0 -and (Invoke-RequiredGit $fixture.Remote @('for-each-ref', '--format=%(refname)')).Length -eq 0) '预览不创建标签或推送'
    $release = Invoke-TestGit $fixture.Repository @('savetag')
    Assert-ReleaseTest ($release.Code -eq 0) "v2 发布成功: $($release.Output)"
    Assert-ReleaseTest ((Invoke-RequiredGit $fixture.Remote @('rev-parse', 'refs/heads/v2')) -ceq $head -and (Invoke-RequiredGit $fixture.Remote @('rev-parse', "$($fixture.Tag)^{}")) -ceq $head) '分支和标签指向原有 HEAD'
    Assert-ReleaseTest ((Invoke-RequiredGit $fixture.Remote @('cat-file', '-t', "refs/tags/$($fixture.Tag)")) -ceq 'tag') '使用 annotated tag'
    Assert-ReleaseTest ((Invoke-RequiredGit $fixture.Repository @('rev-parse', 'HEAD')) -ceq $head -and (Invoke-RequiredGit $fixture.Repository @('status', '--porcelain=v1')).Length -eq 0) '不自动提交或修改工作区'
    $repeat = Invoke-TestGit $fixture.Repository @('savetag')
    Assert-ReleaseTest ($repeat.Code -ne 0 -and $repeat.Output.Contains('本地标签已存在')) '拒绝重复本地标签'

    $fixture = New-ReleaseFixture 'main-success' 'main' 'desktop-v0.1.0'
    $null = Invoke-RequiredGit $fixture.Repository @('tag', '--annotate', 'unrelated', '--message', 'Do not publish')
    $null = Invoke-RequiredGit $fixture.Repository @('config', '--local', 'push.followTags', 'true')
    $release = Invoke-TestGit $fixture.Repository @('savetag')
    Assert-ReleaseTest ($release.Code -eq 0 -and (Invoke-RequiredGit $fixture.Remote @('tag', '--list')) -ceq 'desktop-v0.1.0') 'main 稳定发布且不附带其他 tag'

    $fixture = New-ReleaseFixture 'four-part-version' 'v2' 'desktop-v0.1.0.1-v2.1' '0.1.0.1'
    $release = Invoke-TestGit $fixture.Repository @('savetag')
    Assert-ReleaseTest ($release.Code -eq 0 -and (Invoke-RequiredGit $fixture.Remote @('tag', '--list')) -ceq $fixture.Tag) "支持当前四段产品版本契约: $($release.Output)"

    foreach ($scenario in @(
        @{ Name = 'wrong-version'; Branch = 'v2'; Tag = 'desktop-v0.2.0-v2.1'; Reason = '产品版本不一致' },
        @{ Name = 'wrong-branch'; Branch = 'feature'; Tag = 'desktop-v0.1.0-v2.1'; Reason = '仅支持 main 或 v2' },
        @{ Name = 'v2-stable'; Branch = 'v2'; Tag = 'desktop-v0.1.0'; Reason = 'v2 分支仅允许' },
        @{ Name = 'main-preview'; Branch = 'main'; Tag = 'desktop-v0.1.0-v2.1'; Reason = 'main 分支仅允许' },
        @{ Name = 'zero-preview'; Branch = 'v2'; Tag = 'desktop-v0.1.0-v2.0'; Reason = '标签格式无效' },
        @{ Name = 'leading-zero-preview'; Branch = 'v2'; Tag = 'desktop-v0.1.0-v2.01'; Reason = '标签格式无效' },
        @{ Name = 'leading-zero-version'; Branch = 'v2'; Tag = 'desktop-v0.01.0-v2.1'; Reason = '标签格式无效' },
        @{ Name = 'leading-zero-revision'; Branch = 'v2'; Tag = 'desktop-v0.1.0.01-v2.1'; Reason = '标签格式无效' },
        @{ Name = 'legacy-tag'; Branch = 'main'; Tag = 'v1.0.3.1'; Reason = '标签格式无效' }
    )) {
        $fixture = New-ReleaseFixture $scenario.Name $scenario.Branch $scenario.Tag
        Assert-ReleaseRejected $fixture $scenario.Reason
    }

    foreach ($kind in @('unstaged', 'staged', 'untracked')) {
        $fixture = New-ReleaseFixture "dirty-$kind"
        $name = if ($kind -ceq 'untracked') { 'new.txt' } else { 'payload.txt' }
        [IO.File]::WriteAllText((Join-Path $fixture.Repository $name), 'dirty')
        if ($kind -ceq 'staged') { $null = Invoke-RequiredGit $fixture.Repository @('add', '--', $name) }
        Assert-ReleaseRejected $fixture '工作区必须干净'
    }

    foreach ($kind in @('empty', 'duplicate', 'invalid-date')) {
        $fixture = New-ReleaseFixture "changelog-$kind"
        $text = switch ($kind) {
            'empty' { "# Changelog`n`n## [$($fixture.Tag)] - 2026-11-03`n`n### Added`n<!-- 尚未填写 -->`n" }
            'duplicate' { "## [$($fixture.Tag)] - 2026-11-03`n- 一.`n## [$($fixture.Tag)] - 2026-11-03`n- 二.`n" }
            'invalid-date' { "## [$($fixture.Tag)] - 2026-02-30`n- 内容.`n" }
        }
        [IO.File]::WriteAllText((Join-Path $fixture.Repository 'CHANGELOG.md'), $text)
        Save-Fixture $fixture
        $reason = switch ($kind) { 'empty' { '变更说明不能为空' }; 'duplicate' { '章节重复' }; 'invalid-date' { '日期无效' } }
        Assert-ReleaseRejected $fixture $reason
    }

    $fixture = New-ReleaseFixture 'remote-tag'
    $null = Invoke-RequiredGit $fixture.Repository @('tag', '--annotate', $fixture.Tag, '--message', 'Existing remote tag')
    $null = Invoke-RequiredGit $fixture.Repository @('push', '--quiet', 'origin', "refs/tags/$($fixture.Tag)")
    $null = Invoke-RequiredGit $fixture.Repository @('tag', '--delete', $fixture.Tag)
    $result = Invoke-TestGit $fixture.Repository @('savetag')
    Assert-ReleaseTest ($result.Code -ne 0 -and $result.Output.Contains('远端标签已存在') -and (Invoke-RequiredGit $fixture.Repository @('tag', '--list')).Length -eq 0) '远端标签存在时不创建本地 tag'

    $fixture = New-ReleaseFixture 'atomic-rejection'
    # update hook 只拒绝标签, 验证一次 --atomic 推送仍不会发布已允许的分支.
    $null = New-Item -ItemType Directory -Path (Join-Path $fixture.Remote 'hooks') -Force
    [IO.File]::WriteAllText((Join-Path $fixture.Remote 'hooks/update'), "#!/bin/sh`ncase `"`$1`" in refs/tags/*) exit 1 ;; esac`nexit 0`n")
    $result = Invoke-TestGit $fixture.Repository @('savetag')
    Assert-ReleaseTest ($result.Code -ne 0 -and $result.Output.Contains('原子推送失败')) '推送被拒绝返回失败'
    Assert-ReleaseTest ((Invoke-RequiredGit $fixture.Remote @('for-each-ref', '--format=%(refname)')).Length -eq 0 -and (Invoke-RequiredGit $fixture.Repository @('tag', '--list')) -ceq $fixture.Tag) '原子失败保留本地 tag 且远端没有部分发布'

    $fixture = New-ReleaseFixture 'remote-unavailable'
    $null = Invoke-RequiredGit $fixture.Repository @('remote', 'set-url', 'origin', (Join-Path $sandbox 'missing.git'))
    Assert-ReleaseRejected $fixture '无法核验远端标签'
    Write-Host "PASS release alias: $passed assertions."
} finally {
    try {
        foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GIT_*' })) { Remove-Item -LiteralPath "Env:$($variable.Name)" }
        foreach ($name in $savedGitEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedGitEnvironment[$name], 'Process') }
        if ($ownsSandbox -and (Test-Path -LiteralPath $sandbox)) {
            $item = Get-Item -LiteralPath $sandbox -Force
            $resolved = [IO.Path]::GetFullPath($item.FullName).TrimEnd([IO.Path]::DirectorySeparatorChar)
            $expected = [IO.Path]::GetFullPath($sandbox).TrimEnd([IO.Path]::DirectorySeparatorChar)
            $links = @(Get-ChildItem -LiteralPath $sandbox -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
            if ($resolved -ine $expected -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $links.Count) { throw '拒绝清理非预期或含链接的隔离测试目录.' }
            Remove-Item -LiteralPath $sandbox -Recurse -Force
        }
    } finally {
        # 即使隔离目录清理失败, 也恢复调用前的三个编码设置, 不改变产品或后续测试的控制台策略.
        [Console]::InputEncoding = $savedConsoleInputEncoding
        [Console]::OutputEncoding = $savedConsoleOutputEncoding
        $OutputEncoding = $savedPipelineEncoding
    }
}
