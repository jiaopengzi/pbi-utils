# FilePath    : pbi-utils\.github\scripts\test-gitee-access.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 区分 Gitee token 身份、发行版写权限和无文件上传接口响应, 不输出凭据或账号正文.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { throw 'GITEE_TOKEN 未配置' }
if ($env:RELEASE_TAG -cnotmatch '^desktop-v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?-v2\.[1-9][0-9]*$') { throw '诊断仅允许明确的 v2 预发布' }
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(30)
$api = 'https://gitee.com/api/v5'

<#
.SYNOPSIS 对固定 Gitee 路径请求一次, 返回仅供本脚本处理的状态和正文, 日志只输出分类和耗时.
.PARAMETER Label 非秘密诊断名称.
.PARAMETER Method HTTP 方法.
.PARAMETER Path 固定 API 相对路径.
.PARAMETER Token 当前或故意无效的控制令牌, 不写入日志.
.PARAMETER FormToken 是否使用与上游上传 Action 相同的 multipart token 字段, 不添加文件.
.PARAMETER Json 可选的原样发行版元数据; 用于保持内容不变的权限核验.
.OUTPUTS Status 和 Body; 网络超时返回 Status=0, 不输出异常正文或响应原文.
#>
function Invoke-AccessProbe([string]$Label, [string]$Method, [string]$Path, [string]$Token, [bool]$FormToken = $false, [string]$Json = '') {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$api$Path")
    $response = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($FormToken) {
            $request.Content = [Net.Http.MultipartFormDataContent]::new()
            $request.Content.Add([Net.Http.StringContent]::new($Token), 'access_token')
        } else { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token) }
        if ($Json) { $request.Content = [Net.Http.StringContent]::new($Json, [Text.Encoding]::UTF8, 'application/json') }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $category = if ($status -eq 401) { 'unauthorized' } elseif ($status -eq 403) { 'forbidden-or-scope' } elseif ($status -in 400, 422) { 'invalid-input' } elseif ($status -ge 200 -and $status -lt 300) { 'success' } else { 'other-http-error' }
        Write-Host "$Label status=$status category=$category elapsedMs=$($timer.ElapsedMilliseconds) responseBytes=$([Text.Encoding]::UTF8.GetByteCount($body))"
        return [pscustomobject]@{ Status = $status; Body = $body }
    } catch {
        Write-Host "$Label status=0 category=network-or-timeout elapsedMs=$($timer.ElapsedMilliseconds)"
        return [pscustomobject]@{ Status = 0; Body = '' }
    } finally {
        if ($response) { $response.Dispose() }
        $request.Dispose()
    }
}

try {
    $null = Invoke-AccessProbe 'invalid-token-control-user' GET '/user' 'invalid-diagnostic-token'
    $identity = Invoke-AccessProbe 'configured-token-user' GET '/user' $env:GITEE_TOKEN
    $releaseResult = Invoke-AccessProbe 'configured-token-release-read' GET "/repos/jiaopengzi/pbi-utils/releases/tags/$env:RELEASE_TAG" $env:GITEE_TOKEN
    if ($releaseResult.Status -ne 200) { throw '发行版读取失败, 停止权限写核验' }
    $release = $releaseResult.Body | ConvertFrom-Json
    if ($null -eq $release -or $release.id -le 0 -or $release.tag_name -cne $env:RELEASE_TAG -or $release.name -cne $env:RELEASE_TAG -or !$release.prerelease) { throw '现有 v2 发行版身份不符' }
    $payload = @{ tag_name = $release.tag_name; name = $release.name; body = $release.body; prerelease = $release.prerelease } | ConvertTo-Json
    $write = Invoke-AccessProbe 'configured-token-release-noop-write' PATCH "/repos/jiaopengzi/pbi-utils/releases/$($release.id)" $env:GITEE_TOKEN -Json $payload
    $attachmentPath = "/repos/jiaopengzi/pbi-utils/releases/$($release.id)/attach_files"
    $null = Invoke-AccessProbe 'invalid-token-control-upload-no-file' POST $attachmentPath 'invalid-diagnostic-token' -FormToken $true
    $upload = Invoke-AccessProbe 'configured-token-upload-no-file' POST $attachmentPath $env:GITEE_TOKEN -FormToken $true
    Write-Host "tokenIdentityConfirmed=$($identity.Status -eq 200) releaseWriteConfirmed=$($write.Status -eq 200) uploadEndpointReturned=$($upload.Status -ne 0)"
    if ($write.Status -ne 200) { throw '当前 token 的发行版写权限核验失败' }
    if ($upload.Status -notin 400, 422) { throw '无文件上传应返回参数错误, 当前响应异常; 不据此宣布附件上传成功' }
} finally { $client.Dispose() }
