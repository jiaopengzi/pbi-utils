# FilePath    : pbi-utils\.gitalias\savetag.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 从已提交的 CHANGELOG 核验发布标签, 对当前 HEAD 创建注释标签并原子推送精确分支和标签.

<#
.SYNOPSIS
发布当前干净仓库中已提交的版本, 不自动提交、修改版本或合并分支.
.DESCRIPTION
无参数时创建 annotated tag 并通过 origin 原子推送当前分支和该标签.
--dry-run 仅执行本地及远端只读核验并显示计划, 不创建标签或推送.
要求 PowerShell 7 和 Git; 通过 git config --local alias.savetag '!pwsh -NoProfile -File ./.gitalias/savetag.ps1' 配置.
任何校验、标签创建或推送失败均返回非零; 推送失败时保留已创建的本地标签供人工核验.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = Split-Path $PSScriptRoot -Parent

<# .SYNOPSIS 在脚本所属仓库调用 Git, 参数为精确参数数组; 返回退出码及捕获输出, 不直接输出可能包含凭据的传输诊断. #>
function Invoke-ReleaseGit([string[]]$Arguments) {
    $output = @(& git -C $repository @Arguments 2>&1)
    return @{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}

<# .SYNOPSIS 执行必须成功的只读 Git 命令, 参数为 Git 参数和安全错误说明; 返回输出, 失败时抛错. #>
function Read-ReleaseGit([string[]]$Arguments, [string]$Failure) {
    $result = Invoke-ReleaseGit $Arguments
    if ($result.Code -ne 0) { throw "$Failure (Git 退出码 $($result.Code))." }
    return $result.Output
}

<# .SYNOPSIS 复核当前分支、HEAD 和干净状态, 参数为初始分支与提交; 不一致时拒绝继续, 不修改仓库. #>
function Assert-ReleaseBaseline([string]$Branch, [string]$Head) {
    $currentBranch = Read-ReleaseGit @('symbolic-ref', '--quiet', '--short', 'HEAD') '当前必须位于 main 或 v2 分支'
    $currentHead = Read-ReleaseGit @('rev-parse', '--verify', 'HEAD') '无法读取当前 HEAD'
    $status = Read-ReleaseGit @('status', '--porcelain=v1', '--untracked-files=all') '无法核验工作区'
    if ($currentBranch -cne $Branch -or $currentHead -cne $Head) { throw '发布期间分支或 HEAD 已变化, 请重新核验.' }
    if ($status.Length -gt 0) { throw '工作区必须干净, 请先自行审阅并提交全部发布变更.' }
}

try {
    $dryRun = $args.Count -eq 1 -and $args[0] -ceq '--dry-run'
    if ($args.Count -gt 0 -and !$dryRun) { throw '用法: git savetag [--dry-run]. 不接受版本号或其他参数.' }
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw '发布脚本需要 PowerShell 7.' }
    $gitRoot = Read-ReleaseGit @('rev-parse', '--show-toplevel') '脚本必须位于本项目 Git 仓库的 .gitalias 目录'
    if ([IO.Path]::GetFullPath($gitRoot).TrimEnd('\', '/') -ine [IO.Path]::GetFullPath($repository).TrimEnd('\', '/')) { throw '脚本所在目录不是 Git 仓库根目录.' }
    $branch = Read-ReleaseGit @('symbolic-ref', '--quiet', '--short', 'HEAD') '当前必须位于 main 或 v2 分支'
    if ($branch -cnotin @('main', 'v2')) { throw '发布仅支持 main 或 v2 分支.' }
    $head = Read-ReleaseGit @('rev-parse', '--verify', 'HEAD') '无法读取当前 HEAD'
    Assert-ReleaseBaseline $branch $head

    $changelog = [IO.File]::ReadAllText((Join-Path $repository 'CHANGELOG.md'))
    $headings = [regex]::Matches($changelog, '(?m)^##\s+.*\r?$')
    if ($headings.Count -eq 0) { throw 'CHANGELOG 缺少精确版本标题: ## [desktop-v<版本>] - YYYY-MM-DD.' }
    $heading = [regex]::Match($headings[0].Value.TrimEnd("`r"), '^## \[(?<tag>[^\]]+)\] - (?<date>\d{4}-\d{2}-\d{2})$')
    if (!$heading.Success) { throw 'CHANGELOG 版本标题格式无效, 必须精确使用 ## [desktop-v<版本>] - YYYY-MM-DD.' }
    $tag = $heading.Groups['tag'].Value
    $releaseDate = [DateTime]::MinValue
    if (![DateTime]::TryParseExact($heading.Groups['date'].Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$releaseDate)) { throw 'CHANGELOG 发布日期无效.' }
    $duplicates = @($headings | Where-Object { $_.Value -match ('^##\s+\[' + [regex]::Escape($tag) + '\](?:\s|$)') })
    if ($duplicates.Count -ne 1) { throw 'CHANGELOG 版本章节重复.' }
    $sectionStart = $headings[0].Index + $headings[0].Length
    $sectionEnd = if ($headings.Count -gt 1) { $headings[1].Index } else { $changelog.Length }
    $notes = $changelog.Substring($sectionStart, $sectionEnd - $sectionStart)
    $notes = [regex]::Replace($notes, '(?s)<!--.*?-->', '')
    $content = @($notes -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 -and $_ -notmatch '^\s*#' -and $_ -notmatch '^\s*(?:[-*+]\s*)?$' })
    if ($content.Count -eq 0) { throw 'CHANGELOG 变更说明不能为空, 小节标题或注释不能代替变更内容.' }

    $match = [regex]::Match($tag, '^desktop-v(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?)(?<preview>-v2\.[1-9][0-9]*)?$')
    if (!$match.Success) { throw '标签格式无效, 使用 desktop-v<版本> 或 desktop-v<版本>-v2.<正整数>, 产品版本为三段或四段无前导零数字.' }
    $isPreview = $match.Groups['preview'].Success
    if ($branch -ceq 'v2' -and !$isPreview) { throw 'v2 分支仅允许 desktop-v<版本>-v2.<正整数> 测试标签.' }
    if ($branch -ceq 'main' -and $isPreview) { throw 'main 分支仅允许 desktop-v<版本> 稳定标签.' }
    [xml]$properties = [IO.File]::ReadAllText((Join-Path $repository 'Directory.Build.props'))
    $versions = @($properties.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versions.Count -ne 1 -or $versions[0].InnerText -cne $match.Groups['version'].Value) { throw 'CHANGELOG 标签与 Directory.Build.props 产品版本不一致; 脚本不会自动改版本.' }

    $existing = Invoke-ReleaseGit @('show-ref', '--verify', '--quiet', "refs/tags/$tag")
    if ($existing.Code -eq 0) { throw "本地标签已存在: $tag. 不会覆盖或重用已有标签." }
    if ($existing.Code -ne 1) { throw "无法核验本地标签 (Git 退出码 $($existing.Code))." }
    # ls-remote 与 push 必须指向同一唯一目标, 避免 pushurl 或多个 URL 使预检与实际发布错位.
    $fetchUrl = Read-ReleaseGit @('remote', 'get-url', 'origin') '无法读取 origin'
    $pushUrls = (Read-ReleaseGit @('remote', 'get-url', '--push', '--all', 'origin') '无法读取 origin 推送目标') -split '\r?\n'
    if ($pushUrls.Count -ne 1 -or $pushUrls[0] -cne $fetchUrl) { throw 'origin 必须使用同一唯一读取与推送目标, 请先核验仓库本地 Git 配置.' }
    $remoteTag = Invoke-ReleaseGit @('ls-remote', '--exit-code', '--refs', 'origin', "refs/tags/$tag")
    if ($remoteTag.Code -eq 0) { throw "远端标签已存在: $tag. 不会覆盖或重用已有标签." }
    if ($remoteTag.Code -ne 2) { throw "无法核验远端标签 (Git 退出码 $($remoteTag.Code)), 发布已停止." }
    Assert-ReleaseBaseline $branch $head
    if ($dryRun) {
        Write-Host "DRY RUN: 分支 $branch, 产品版本 $($versions[0].InnerText), 标签 $tag."
        Write-Host "将对当前 HEAD $head 创建 annotated tag, 并原子推送该分支和精确标签到 origin. 未修改本地或远端 refs."
        exit 0
    }

    $created = Invoke-ReleaseGit @('tag', '--annotate', '--message', "Release: $tag", '--', $tag, $head)
    if ($created.Code -ne 0) { throw "创建本地注释标签失败 (Git 退出码 $($created.Code)); 未执行推送." }
    try {
        Assert-ReleaseBaseline $branch $head
        # 显式关闭 followTags, 不受用户该偏好影响而顺带推送其他本地标签; 不 force, 不修改 upstream.
        $push = Invoke-ReleaseGit @('push', '--atomic', '--no-follow-tags', '--recurse-submodules=no', 'origin', "${head}:refs/heads/$branch", "refs/tags/${tag}:refs/tags/$tag")
        if ($push.Code -ne 0) { throw "原子推送失败 (Git 退出码 $($push.Code)). 不会自动重试; 请核验 origin 的分支和标签状态." }
    } catch {
        throw "$($_.Exception.Message) 本地标签 $tag 已保留, 未删除或覆盖任何标签."
    }
    Write-Host "发布完成: $branch 和 $tag 已原子推送到 origin; 未创建提交或修改产品版本."
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
