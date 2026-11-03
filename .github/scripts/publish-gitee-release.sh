#!/usr/bin/env bash
# FilePath    : pbi-utils\.github\scripts\publish-gitee-release.sh
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 将已验证 GitHub ZIP 同步到精确 Gitee 发行版, 校验附件后才开放正式更新.

# 输入为受信工作流环境中的 RELEASE_TAG、ARCHIVE_NAME、PRERELEASE、GITHUB_SHA、GITEE_TOKEN.
# 当前目录必须有 release-zip/<精确 ZIP> 及 release-notes.md; 失败保留远端实际状态, 不盲重放写请求.
set -euo pipefail
api='https://gitee.com/api/v5/repos/jiaopengzi/pbi-utils'
archive="release-zip/$ARCHIVE_NAME"
[[ "$RELEASE_TAG" =~ ^desktop-v[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-v2\.[1-9][0-9]*)?$ ]]
[[ "$ARCHIVE_NAME" =~ ^pbi-utils-[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?-win-x64-without-dotnet\.zip$ ]]
[[ "$PRERELEASE" == true || "$PRERELEASE" == false ]]
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]]
test -n "$GITEE_TOKEN"
test -s "$archive"
test -s release-notes.md
bytes="$(stat --format=%s "$archive")"
if (( bytes > 100000000 )); then echo '::error::ZIP exceeds the Gitee 100M attachment limit.'; exit 1; fi
expected_hash="$(sha256sum "$archive" | cut -d ' ' -f1)"
notes="$(cat release-notes.md)"
state="$(mktemp -d "$RUNNER_TEMP/pbi-utils-gitee-release.XXXXXX")"
trap 'code=$?; if (( code != 0 )); then echo "::error::Gitee synchronization failed (method=${request_method:-unavailable}, path=${request_path:-unavailable}, HTTP ${status:-unavailable}); inspect remote state before rerunning only the failed job."; fi; rm -rf -- "$state"; exit "$code"' EXIT

# 参数为固定的响应分类原因, 只输出当前方法、路径和状态, 不输出令牌或远端正文; 总是明确失败.
reject_response() {
  echo "::error::Gitee response rejected (method=$request_method, path=$request_path, HTTP $status): $1."
  exit 1
}

# 向固定 Gitee API 发起一次请求, 参数为方法、相对路径、输出文件和可选 JSON 文件.
# 只读取 HTTP 状态, 不输出令牌或响应正文; 非预期结果抛失败, 写请求不自动重试.
request() {
  local method="$1" path="$2" output="$3" payload="${4:-}"
  request_method="$method"
  request_path="$path"
  local args=(--silent --show-error --connect-timeout 15 --max-time 180 --request "$method"
    --header "Authorization: Bearer $GITEE_TOKEN" --output "$output" --write-out '%{http_code}')
  if [[ -n "$payload" ]]; then args+=(--header 'Content-Type: application/json' --data-binary "@$payload"); fi
  status="$(curl "${args[@]}" "$api$path")"
}

# 验证本发行版的唯一 ZIP 元数据, 下载实际公开附件并核对完整字节, 不接受同名不同内容.
verify_asset() {
  local release_id="$1"
  request GET "/releases/$release_id/attach_files?per_page=100&page=1" "$state/assets.json"
  [[ "$status" == 200 ]]
  jq -e --arg name "$ARCHIVE_NAME" --argjson size "$bytes" \
    'type == "array" and length == 1 and .[0].name == $name and .[0].size == $size' "$state/assets.json" > /dev/null
  local url expected_url actual_hash
  url="$(jq -er '.[0].browser_download_url' "$state/assets.json")"
  expected_url="https://gitee.com/jiaopengzi/pbi-utils/releases/download/$RELEASE_TAG/$ARCHIVE_NAME"
  [[ "$url" == "$expected_url" ]]
  # 公开下载地址不携带 API 令牌, 保留 HTTPS 和附件大小边界.
  request_method=GET
  request_path="$url"
  status="$(curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    --connect-timeout 15 --max-time 180 --max-filesize 100000000 --output "$state/verified.zip" --write-out '%{http_code}' "$url")"
  actual_hash="$(sha256sum "$state/verified.zip" | cut -d ' ' -f1)"
  [[ "$actual_hash" == "$expected_hash" ]]
  [[ "$(stat --format=%s "$state/verified.zip")" == "$bytes" ]]
}

request GET "/releases/tags/$RELEASE_TAG" "$state/release.json"
release_absent=false
if [[ "$status" == 404 ]]; then
  release_absent=true
elif [[ "$status" == 200 ]]; then
  # Gitee 对缺少的发行版也会返回 HTTP 200 + literal null; 空正文、错误 JSON 或多个值不等于缺失.
  jq -e -s 'length == 1' "$state/release.json" > /dev/null 2>&1 \
    || reject_response 'release lookup did not return exactly one valid JSON value'
  if jq -e '. == null' "$state/release.json" > /dev/null 2>&1; then release_absent=true; fi
else
  reject_response 'release lookup HTTP status is not accepted'
fi
if [[ "$release_absent" == true ]]; then
  # 先以预发布创建, 避免正式更新检查在附件上传前发现空发行版.
  jq -n --arg tag "$RELEASE_TAG" --arg commit "$GITHUB_SHA" --rawfile body release-notes.md \
    '{tag_name:$tag,name:$tag,body:$body,target_commitish:$commit,prerelease:true}' > "$state/create.json"
  request POST '/releases' "$state/release.json" "$state/create.json"
  [[ "$status" == 200 || "$status" == 201 ]] || reject_response 'release creation HTTP status is not accepted'
fi
jq -e -s 'length == 1 and (.[0] | type == "object")' "$state/release.json" > /dev/null 2>&1 \
  || reject_response 'release metadata is not exactly one JSON object'
jq -e --arg tag "$RELEASE_TAG" \
  '(.id | type == "number") and .id > 0 and .tag_name == $tag and .name == $tag and (.body | type == "string")' \
  "$state/release.json" > /dev/null 2>&1 || reject_response 'release identity or notes type does not match the checked release'
release_id="$(jq -er '.id' "$state/release.json")"
[[ "$release_id" =~ ^[1-9][0-9]*$ ]] || reject_response 'release id is not a positive integer'
[[ "$(jq -r '.body' "$state/release.json")" == "$notes" ]] || reject_response 'release notes do not match the checked release'

request GET "/releases/$release_id/attach_files?per_page=100&page=1" "$state/assets.json"
[[ "$status" == 200 ]]
jq -e 'type == "array" and length <= 1' "$state/assets.json" > /dev/null
if [[ "$(jq 'length' "$state/assets.json")" == 0 ]]; then
  # 官方 API 的 multipart 参数为 file; 不附加未声明的 name, 不删除已有发行版或附件.
  request_method=POST
  request_path="/releases/$release_id/attach_files"
  status="$(curl --silent --show-error --connect-timeout 15 --max-time 300 \
    --header "Authorization: Bearer $GITEE_TOKEN" --request POST \
    --form "file=@$archive;filename=$ARCHIVE_NAME;type=application/zip" \
    --output "$state/upload.json" --write-out '%{http_code}' "$api/releases/$release_id/attach_files")"
  [[ "$status" == 200 || "$status" == 201 ]]
fi
verify_asset "$release_id"

jq -n --arg tag "$RELEASE_TAG" --rawfile body release-notes.md --argjson prerelease "$PRERELEASE" \
  '{tag_name:$tag,name:$tag,body:$body,prerelease:$prerelease}' > "$state/final.json"
request PATCH "/releases/$release_id" "$state/final-response.json" "$state/final.json"
[[ "$status" == 200 ]]
request GET "/releases/tags/$RELEASE_TAG" "$state/final-release.json"
[[ "$status" == 200 ]]
jq -e --arg tag "$RELEASE_TAG" --argjson prerelease "$PRERELEASE" \
  '.tag_name == $tag and .name == $tag and .prerelease == $prerelease' "$state/final-release.json" > /dev/null
echo "Verified Gitee release: https://gitee.com/jiaopengzi/pbi-utils/releases/tag/$RELEASE_TAG"
