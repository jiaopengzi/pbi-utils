#!/usr/bin/env bash
# FilePath    : pbi-utils\.gitalias\savetag.sh
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 从 CHANGELOG 准备 version, 核验已提交的发布资料后对当前 HEAD 打标签并原子推送.

# 用法: git savetag [--prepare|--dry-run]. 准备时只生成 version, 无参数时发布当前干净仓库中已提交的版本.
# 配置: git config --local alias.savetag '!bash ./.gitalias/savetag.sh'.
# 使用 Git Bash 执行 Git 操作, 通过 PowerShell 7 复用仓库发布规范.
# 不自动暂存、提交或合并分支; 任何失败返回非零, 推送失败保留本地标签供人工核验.

set -euo pipefail
tag_created=false
tag=''

# 输出安全错误并以非零结束; 参数为不含 URL、凭据或原始 Git 传输诊断的说明.
fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

# 失败时报告已创建的本地标签, 不删除或重用标签; EXIT 状态由原失败保留.
report_retained_tag() {
    local code=$?
    if ((code != 0)) && [[ "$tag_created" == true ]]; then
        printf '本地标签 %s 已保留, 未删除或覆盖任何标签.\n' "$tag" >&2
    fi
}
trap report_retained_tag EXIT

# 执行必须成功的只读 Git 命令; 首参为安全错误说明, 其余为 Git 参数, 成功返回原输出.
read_git() {
    local failure=$1 output code
    shift
    if output=$(git "$@" 2>&1); then
        printf '%s' "$output"
    else
        code=$?
        fail "$failure (Git 退出码 $code)."
    fi
}

# 复核初始分支、HEAD 与干净状态; 参数为原分支和提交, 变化时拒绝继续且不修改仓库.
assert_baseline() {
    local current_branch current_head status
    current_branch=$(read_git '当前必须位于 main 或 vN 分支' symbolic-ref --quiet --short HEAD)
    current_head=$(read_git '无法读取当前 HEAD' rev-parse --verify HEAD)
    status=$(read_git '无法核验工作区' status --porcelain=v1 --untracked-files=all)
    [[ "$current_branch" == "$1" && "$current_head" == "$2" ]] || fail '发布期间分支或 HEAD 已变化, 请重新核验.'
    [[ -z "$status" ]] || fail '工作区必须干净, 请先自行审阅并提交全部发布变更.'
}

# 复用发布规范, 首参为分支, 可选次参为待发布提交.
# 输出准备或提交核验取得的完整版本; 失败给出诊断并返回非零.
invoke_release_spec() {
    PBI_SAVETAG_BRANCH="$1" PBI_SAVETAG_COMMIT="${2:-}" pwsh -NoProfile -NonInteractive -Command '
        $ErrorActionPreference = "Stop"
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        try {
            if ($PSVersionTable.PSVersion.Major -lt 7) { throw "读取发布版本需要 PowerShell 7." }
            Import-Module (Join-Path $PWD "scripts/ReleaseAutomation.psm1") -Force
            if ($env:PBI_SAVETAG_COMMIT) {
                $context = (Get-PbiCommitReleaseSpec -RepositoryRoot $PWD -Commit $env:PBI_SAVETAG_COMMIT -Branch $env:PBI_SAVETAG_BRANCH).Context
                [Console]::Write($context.ReleaseTag)
            } else {
                $release = Initialize-PbiReleaseVersion -RepositoryRoot $PWD -Branch $env:PBI_SAVETAG_BRANCH
                [Console]::Write($release.Tag)
            }
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
    '
}

action=${1:-}
if (($# > 1)) || { (($# == 1)) && [[ "$action" != --prepare && "$action" != --dry-run ]]; }; then
    fail '用法: git savetag [--prepare|--dry-run]. 不接受版本号或其他参数.'
fi

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository=$(cd -- "$script_directory/.." && pwd -P)
cd -- "$repository"
git_root=$(read_git '脚本必须位于本项目 Git 仓库的 .gitalias 目录' rev-parse --show-toplevel)
# 通过 cd 统一 Git 的 Windows 路径与 Git Bash 的 /盘符 路径, 不依赖调用目录.
[[ "$(cd -- "$git_root" && pwd -P)" == "$repository" ]] || fail '脚本所在目录不是 Git 仓库根目录.'
branch=$(read_git '当前必须位于 main 或 vN 分支' symbolic-ref --quiet --short HEAD)
[[ "$branch" == main || "$branch" =~ ^v[1-9][0-9]*$ ]] || fail '发布仅支持 main 或 vN 分支 (N 为正整数).'
if [[ "$action" == --prepare ]]; then
    tag=$(invoke_release_spec "$branch") || exit 1
    printf '已准备 version %s. 请审阅并提交 CHANGELOG.md 和 version 后执行 git savetag.\n' "$tag"
    exit 0
fi

head=$(read_git '无法读取当前 HEAD' rev-parse --verify HEAD)
assert_baseline "$branch" "$head"

tag=$(invoke_release_spec "$branch" "$head") || exit 1

tag_ref="refs/tags/$tag"
if git show-ref --verify --quiet "$tag_ref" >/dev/null 2>&1; then
    fail "本地标签已存在: $tag. 不会覆盖或重用已有标签."
else
    code=$?
    ((code == 1)) || fail "无法核验本地标签 (Git 退出码 $code)."
fi
# 预检与推送使用同一唯一目标, 不输出可能带凭据的 URL 或 Git 原始传输诊断.
fetch_url=$(read_git '无法读取 origin' remote get-url origin)
push_urls=$(read_git '无法读取 origin 推送目标' remote get-url --push --all origin)
[[ "$push_urls" == "$fetch_url" && "$push_urls" != *$'\n'* ]] || fail 'origin 必须使用同一唯一读取与推送目标, 请先核验仓库本地 Git 配置.'
if git ls-remote --exit-code --refs origin "$tag_ref" >/dev/null 2>&1; then
    fail "远端标签已存在: $tag. 不会覆盖或重用已有标签."
else
    code=$?
    ((code == 2)) || fail "无法核验远端标签 (Git 退出码 $code), 发布已停止."
fi
assert_baseline "$branch" "$head"
if [[ "$action" == --dry-run ]]; then
    printf 'DRY RUN: 分支 %s, 版本 %s.\n' "$branch" "$tag"
    printf '将为当前提交 %s 创建注释标签, 并将其与当前分支一起原子推送到 origin. 本次仅预览.\n' "$head"
    exit 0
fi

if git tag --annotate --message "Release: $tag" -- "$tag" "$head" >/dev/null 2>&1; then
    tag_created=true
else
    code=$?
    fail "创建本地注释标签失败 (Git 退出码 $code); 未执行推送."
fi
assert_baseline "$branch" "$head"
# 显式关闭 followTags, 不顺带推送其他本地标签; 不 force, 不修改 upstream 或自动重试.
if git push --atomic --no-follow-tags --recurse-submodules=no origin "$head:refs/heads/$branch" "$tag_ref:$tag_ref" >/dev/null 2>&1; then
    printf '发布完成: 分支 %s 和版本 %s 已原子推送到 origin.\n' "$branch" "$tag"
else
    code=$?
    fail "原子推送失败 (Git 退出码 $code). 不会自动重试; 请核验 origin 的分支和标签状态."
fi
