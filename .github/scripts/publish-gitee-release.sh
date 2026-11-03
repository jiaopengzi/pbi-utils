#!/usr/bin/env bash
# FilePath    : pbi-utils\.github\scripts\publish-gitee-release.sh
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 分阶段准备 Gitee 发行版并验证现成 Action 上传的 ZIP, 校验完成后才开放正式更新.

# 唯一参数为 prepare 或 verify; 共同输入为 RELEASE_TAG、ARCHIVE_NAME、PRERELEASE、RELEASE_COMMIT、GITEE_TOKEN、GITHUB_OUTPUT.
# verify 还要求 PREPARED_RELEASE_ID; 当前目录必须有精确 ZIP 和 release-notes.md, 失败不重试或删除远端对象.
set -euo pipefail

# 参数为固定输入错误原因; 不回显凭据或任意输入值, 以退出码 2 在请求前明确失败.
reject_input() { echo "::error::Gitee release input rejected: $1."; exit 2; }
[[ "$#" == 1 ]] || reject_input 'exactly one prepare or verify phase is required'
phase="$1"
[[ "$phase" == prepare || "$phase" == verify ]] || reject_input 'phase must be prepare or verify'
[[ "${RELEASE_TAG:-}" =~ ^desktop-v[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-v2\.[1-9][0-9]*)?$ ]] || reject_input 'release tag is invalid'
[[ "${ARCHIVE_NAME:-}" =~ ^pbi-utils-[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?-win-x64-without-dotnet\.zip$ ]] || reject_input 'archive name is invalid'
[[ "${PRERELEASE:-}" == true || "${PRERELEASE:-}" == false ]] || reject_input 'prerelease must be true or false'
[[ "${RELEASE_COMMIT:-}" =~ ^[0-9a-f]{40}$ ]] || reject_input 'release commit must be a resolved commit SHA'
[[ -n "${GITEE_TOKEN:-}" ]] || reject_input 'Gitee token is missing'
[[ -n "${GITHUB_OUTPUT:-}" && -f "$GITHUB_OUTPUT" && -w "$GITHUB_OUTPUT" && ! -L "$GITHUB_OUTPUT" ]] || reject_input 'GitHub output must be an existing writable regular file'
if [[ "$phase" == verify ]]; then
  [[ "${PREPARED_RELEASE_ID:-}" =~ ^[1-9][0-9]*$ ]] || reject_input 'prepared release id is required for verification'
fi
api='https://gitee.com/api/v5/repos/jiaopengzi/pbi-utils'
archive="release-zip/$ARCHIVE_NAME"
[[ -s "$archive" ]] || reject_input 'verified archive file is missing or empty'
[[ -s release-notes.md ]] || reject_input 'release notes file is missing or empty'
bytes="$(stat --format=%s "$archive")"
if (( bytes > 100000000 )); then echo '::error::ZIP exceeds the Gitee 100M attachment limit.'; exit 1; fi
expected_hash="$(sha256sum "$archive" | cut -d ' ' -f1)"
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

# 参数为本次响应文件, 验证唯一对象、正整数 id、精确 tag/name、正文及预发布类型; 成功更新 release_id.
validate_release() {
  local release_file="$1"
  jq -e -s 'length == 1 and (.[0] | type == "object")' "$release_file" > /dev/null 2>&1 \
    || reject_response 'release metadata is not exactly one JSON object'
  # 仅忽略命令行工具可能追加的末尾 LF, 不折叠 CR、空格或正文内部差异; 最终 PATCH 保留原对象正文.
  jq -e --arg tag "$RELEASE_TAG" --rawfile notes release-notes.md \
    '(.id | type == "number") and .id > 0 and .tag_name == $tag and .name == $tag and (.body | type == "string") and ((.body | sub("\n+$"; "")) == ($notes | sub("\n+$"; ""))) and (.prerelease | type == "boolean")' \
    "$release_file" > /dev/null 2>&1 || reject_response 'release identity, notes or prerelease type does not match the checked release'
  release_id="$(jq -er '.id' "$release_file")"
  [[ "$release_id" =~ ^[1-9][0-9]*$ ]] || reject_response 'release id is not a positive integer'
}

# 参数为已验证的 release id, 严格读取唯一 JSON 数组, 仅允许零或一个附件; 成功更新 asset_count.
read_assets() {
  local id="$1"
  request GET "/releases/$id/attach_files?per_page=100&page=1" "$state/assets.json"
  [[ "$status" == 200 ]] || reject_response 'attachment lookup HTTP status is not accepted'
  jq -e -s 'length == 1 and (.[0] | type == "array")' "$state/assets.json" > /dev/null 2>&1 \
    || reject_response 'attachments are not exactly one JSON array'
  jq -e 'length <= 1' "$state/assets.json" > /dev/null 2>&1 || reject_response 'release has ambiguous attachments'
  asset_count="$(jq -r 'length' "$state/assets.json")"
}

# 验证本发行版的唯一 ZIP 元数据, 下载实际公开附件并核对完整字节, 不接受同名不同内容.
verify_asset() {
  local id="$1"
  read_assets "$id"
  [[ "$asset_count" == 1 ]] || reject_response 'release does not have the required unique ZIP attachment'
  jq -e --arg name "$ARCHIVE_NAME" --argjson size "$bytes" \
    '.[0].name == $name and .[0].size == $size' "$state/assets.json" > /dev/null 2>&1 \
    || reject_response 'attachment name or size does not match the verified archive'
  local url expected_url actual_hash
  url="$(jq -er '.[0].browser_download_url' "$state/assets.json")"
  expected_url="https://gitee.com/jiaopengzi/pbi-utils/releases/download/$RELEASE_TAG/$ARCHIVE_NAME"
  [[ "$url" == "$expected_url" ]] || reject_response 'attachment URL is not the exact same-source release URL'
  # 公开下载地址不携带 API 令牌, 保留 HTTPS 和附件大小边界.
  request_method=GET
  request_path="$url"
  status="$(curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    --connect-timeout 15 --max-time 180 --max-filesize 100000000 --output "$state/verified.zip" --write-out '%{http_code}' "$url")"
  actual_hash="$(sha256sum "$state/verified.zip" | cut -d ' ' -f1)"
  [[ "$actual_hash" == "$expected_hash" ]] || reject_response 'public attachment hash does not match the verified archive'
  [[ "$(stat --format=%s "$state/verified.zip")" == "$bytes" ]] || reject_response 'public attachment length does not match the verified archive'
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
  [[ "$phase" == prepare ]] || reject_response 'release is absent during verification'
  # 先以预发布创建, 避免正式更新检查在附件上传前发现空发行版.
  jq -n --arg tag "$RELEASE_TAG" --arg commit "$RELEASE_COMMIT" --rawfile body release-notes.md \
    '{tag_name:$tag,name:$tag,body:$body,target_commitish:$commit,prerelease:true}' > "$state/create.json"
  request POST '/releases' "$state/release.json" "$state/create.json"
  [[ "$status" == 200 || "$status" == 201 ]] || reject_response 'release creation HTTP status is not accepted'
fi
validate_release "$state/release.json"
if [[ "$phase" == prepare ]]; then
  read_assets "$release_id"
  upload_required=true
  if [[ "$asset_count" == 1 ]]; then verify_asset "$release_id"; upload_required=false; fi
  # 固定字段和值不允许注入 GITHUB_OUTPUT; 仅在元数据及已有资产检查完成后写入本步输出.
  printf 'release_id=%s\nupload_required=%s\n' "$release_id" "$upload_required" >> "$GITHUB_OUTPUT"
  echo "Prepared Gitee release: release_id=$release_id, upload_required=$upload_required"
  exit 0
fi
[[ "$release_id" == "$PREPARED_RELEASE_ID" ]] || reject_response 'release id changed after preparation'
verify_asset "$release_id"

# Gitee PATCH 要求 tag/name/body; 取已核验对象的原始值, 不因本地 notes 的末尾 LF 差异改写远端正文.
jq --argjson prerelease "$PRERELEASE" \
  '{tag_name:.tag_name,name:.name,body:.body,prerelease:$prerelease}' "$state/release.json" > "$state/final.json"
request PATCH "/releases/$release_id" "$state/final-response.json" "$state/final.json"
[[ "$status" == 200 ]] || reject_response 'release finalization HTTP status is not accepted'
request GET "/releases/tags/$RELEASE_TAG" "$state/final-release.json"
[[ "$status" == 200 ]] || reject_response 'final release read-back HTTP status is not accepted'
validate_release "$state/final-release.json"
[[ "$release_id" == "$PREPARED_RELEASE_ID" ]] || reject_response 'release id changed during finalization'
jq -e --argjson prerelease "$PRERELEASE" '.prerelease == $prerelease' "$state/final-release.json" > /dev/null 2>&1 \
  || reject_response 'final release prerelease flag does not match the requested value'
echo "Verified Gitee release: https://gitee.com/jiaopengzi/pbi-utils/releases/tag/$RELEASE_TAG"
