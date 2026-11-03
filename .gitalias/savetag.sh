#!/usr/bin/env bash
# FilePath    : pbi-utils\.gitalias\savetag.sh
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 从已提交的 CHANGELOG 核验发布标签, 对当前 HEAD 创建注释标签并原子推送精确分支和标签.

# 用法: git savetag [--dry-run]. 无参数时发布当前干净仓库中已提交的版本.
# 配置: git config --local alias.savetag '!bash ./.gitalias/savetag.sh'.
# 使用 Git Bash 自带工具, 仅 XML 版本读取使用仓库已有的 PowerShell 7.
# 不自动提交、修改版本或合并分支; 任何失败返回非零, 推送失败保留本地标签供人工核验.

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
    current_branch=$(read_git '当前必须位于 main 或 v2 分支' symbolic-ref --quiet --short HEAD)
    current_head=$(read_git '无法读取当前 HEAD' rev-parse --verify HEAD)
    status=$(read_git '无法核验工作区' status --porcelain=v1 --untracked-files=all)
    [[ "$current_branch" == "$1" && "$current_head" == "$2" ]] || fail '发布期间分支或 HEAD 已变化, 请重新核验.'
    [[ -z "$status" ]] || fail '工作区必须干净, 请先自行审阅并提交全部发布变更.'
}

dry_run=false
if (($# == 1)) && [[ "$1" == --dry-run ]]; then
    dry_run=true
elif (($# != 0)); then
    fail '用法: git savetag [--dry-run]. 不接受版本号或其他参数.'
fi

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository=$(cd -- "$script_directory/.." && pwd -P)
cd -- "$repository"
git_root=$(read_git '脚本必须位于本项目 Git 仓库的 .gitalias 目录' rev-parse --show-toplevel)
# 通过 cd 统一 Git 的 Windows 路径与 Git Bash 的 /盘符 路径, 不依赖调用目录.
[[ "$(cd -- "$git_root" && pwd -P)" == "$repository" ]] || fail '脚本所在目录不是 Git 仓库根目录.'
branch=$(read_git '当前必须位于 main 或 v2 分支' symbolic-ref --quiet --short HEAD)
[[ "$branch" == main || "$branch" == v2 ]] || fail '发布仅支持 main 或 v2 分支.'
head=$(read_git '无法读取当前 HEAD' rev-parse --verify HEAD)
assert_baseline "$branch" "$head"

[[ -f CHANGELOG.md ]] || fail '找不到 CHANGELOG.md.'
heading=$(awk '/^##[[:space:]]/ { sub(/\r$/, ""); print; exit }' CHANGELOG.md)
heading_pattern='^## \[([^]]+)\] - ([0-9]{4}-[0-9]{2}-[0-9]{2})$'
[[ "$heading" =~ $heading_pattern ]] || fail 'CHANGELOG 版本标题格式无效, 必须精确使用 ## [desktop-v<版本>] - YYYY-MM-DD.'
tag=${BASH_REMATCH[1]}
release_date=${BASH_REMATCH[2]}
if ! normalized_date=$(date --date="$release_date" +%F 2>/dev/null); then
    fail 'CHANGELOG 发布日期无效.'
fi
[[ "$normalized_date" == "$release_date" && "$release_date" != 0000-* ]] || fail 'CHANGELOG 发布日期无效.'
duplicate_count=$(awk -v target="$tag" '
    /^##[[:space:]]+\[/ {
        line = $0
        sub(/^##[[:space:]]+\[/, "", line)
        closing = index(line, "]")
        if (closing && substr(line, 1, closing - 1) == target) count++
    }
    END { print count + 0 }
' CHANGELOG.md)
[[ "$duplicate_count" == 1 ]] || fail 'CHANGELOG 版本章节重复.'

# 只检查最新版本正文, 排除空行、分类标题、空列表以及跨行 HTML 注释; 不拿其他版本补说明.
if ! awk '
    /^##[[:space:]]/ { if (capturing) exit; capturing = 1; next }
    capturing {
        line = $0
        visible = ""
        while (length(line)) {
            if (in_comment) {
                closing = index(line, "-->")
                if (!closing) { line = ""; break }
                line = substr(line, closing + 3)
                in_comment = 0
            } else {
                opening = index(line, "<!--")
                if (!opening) { visible = visible line; line = ""; break }
                visible = visible substr(line, 1, opening - 1)
                line = substr(line, opening + 4)
                in_comment = 1
            }
        }
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", visible)
        if (visible != "" && visible !~ /^#/ && visible !~ /^[-*+]?[[:space:]]*$/) meaningful = 1
    }
    END { exit meaningful ? 0 : 1 }
' CHANGELOG.md; then
    fail 'CHANGELOG 变更说明不能为空, 小节标题或注释不能代替变更内容.'
fi

tag_pattern='^desktop-v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))?)(-v2\.[1-9][0-9]*)?$'
[[ "$tag" =~ $tag_pattern ]] || fail '标签格式无效, 使用 desktop-v<版本> 或 desktop-v<版本>-v2.<正整数>, 产品版本为三段或四段无前导零数字.'
tag_version=${BASH_REMATCH[1]}
if [[ "$branch" == v2 && "$tag" != *-v2.* ]]; then
    fail 'v2 分支仅允许 desktop-v<版本>-v2.<正整数> 测试标签.'
elif [[ "$branch" == main && "$tag" == *-v2.* ]]; then
    fail 'main 分支仅允许 desktop-v<版本> 稳定标签.'
fi

# XML 是产品版本的权威来源, 仅此读取使用已有 pwsh 的 XML 解析器, 不执行另一个发布脚本.
if ! product_version=$(pwsh -NoProfile -NonInteractive -Command '
    $ErrorActionPreference = "Stop"
    try {
        if ($PSVersionTable.PSVersion.Major -lt 7) { exit 1 }
        $document = [xml][IO.File]::ReadAllText("Directory.Build.props")
        $versions = @($document.SelectNodes("/Project/PropertyGroup/Version"))
        if ($versions.Count -ne 1) { exit 1 }
        [Console]::Write($versions[0].InnerText)
    } catch { exit 1 }
' 2>/dev/null); then
    fail '无法通过 PowerShell 7 读取 Directory.Build.props 的唯一产品版本.'
fi
[[ "$product_version" == "$tag_version" ]] || fail 'CHANGELOG 标签与 Directory.Build.props 产品版本不一致; 脚本不会自动改版本.'

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
if [[ "$dry_run" == true ]]; then
    printf 'DRY RUN: 分支 %s, 产品版本 %s, 标签 %s.\n' "$branch" "$product_version" "$tag"
    printf '将对当前 HEAD %s 创建 annotated tag, 并原子推送该分支和精确标签到 origin. 未修改本地或远端 refs.\n' "$head"
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
    printf '发布完成: %s 和 %s 已原子推送到 origin; 未创建提交或修改产品版本.\n' "$branch" "$tag"
else
    code=$?
    fail "原子推送失败 (Git 退出码 $code). 不会自动重试; 请核验 origin 的分支和标签状态."
fi
