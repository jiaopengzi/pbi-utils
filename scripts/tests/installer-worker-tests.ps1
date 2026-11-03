# FilePath    : pbi-utils\scripts\tests\installer-worker-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 使用隔离替身验证后台检测、下载取消和官方安装排空.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$module = Import-Module (Join-Path $root 'scripts/installer/Prerequisites.psm1') -Force -PassThru
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-worker-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sandbox
try {
    & $module {
        $script:Mode = 'check'; $script:ProbeCount = 0; $script:InstallCount = 0; $script:DownloadCount = 0
        function script:Read-PbiPrerequisiteManifest { [pscustomobject]@{ dotNetMode='without-dotnet'; dotnet=[pscustomobject]@{minimumVersion='10.0.0';url='https://example.invalid/dotnet';sha512=''}; webView2=[pscustomobject]@{url='https://example.invalid/webview'} } }
        function script:Test-PbiInstalledDotNet { param($version) $script:ProbeCount++; if ($script:Mode -eq 'probe-error') { throw 'probe' }; if ($script:Mode -eq 'postcheck-error') { return $false }; return ($script:InstallCount -gt 0) }
        function script:Test-PbiInstalledWebView2 { return $true }
        function script:Receive-PbiInstaller { param($url,$destination,$cancel) $script:DownloadCount++; if ($script:Mode -eq 'network-error') { throw 'network' }; [IO.File]::WriteAllText($destination,'fake'); if ($script:Mode -eq 'cancel-download') { [IO.File]::WriteAllText($cancel,'1') } }
        function script:Invoke-PbiOfficialInstaller { param($path,$hash,$kind,$cancel) if ($script:Mode -eq 'signature-error') { throw 'PREREQUISITE_SHA512_MISMATCH' }; $script:InstallCount++; if ($script:Mode -eq 'cancel-install') { [IO.File]::WriteAllText($script:CancelPath,'1') }; return 3010 }
    }
    $state = Join-Path $sandbox 'state.ini'; $cancel = Join-Path $sandbox 'cancel'
    & $module { param($s,$c,$w) Invoke-PbiPrerequisiteWorker 'unused' $s $c $w $false } $state $cancel $sandbox
    $text = [IO.File]::ReadAllText($state)
    if ($text -notmatch 'phase=missing' -or $text -notmatch 'dotnet=missing' -or $text -notmatch 'webview2=ready') { throw "检查状态错误: $text" }
    & $module { $script:Mode='cancel-download' }
    & $module { param($s,$c,$w) Invoke-PbiPrerequisiteWorker 'unused' $s $c $w $true } $state $cancel $sandbox
    $text = [IO.File]::ReadAllText($state)
    if ($text -notmatch 'phase=cancelled' -or (& $module { $script:InstallCount }) -ne 0) { throw '下载取消后不得启动官方安装器' }
    Remove-Item -LiteralPath $cancel
    & $module { param($c) $script:Mode='cancel-install'; $script:CancelPath=$c } $cancel
    & $module { param($s,$c,$w) Invoke-PbiPrerequisiteWorker 'unused' $s $c $w $true } $state $cancel $sandbox
    $text = [IO.File]::ReadAllText($state)
    if ($text -notmatch 'phase=cancelled' -or $text -notmatch 'dotnet=ready' -or $text -notmatch 'reboot=1' -or (& $module { $script:ProbeCount }) -lt 4) { throw "官方安装启动后必须等待并复检: $text" }
    Remove-Item -LiteralPath $cancel
    foreach ($case in @(@('probe-error','probe'), @('network-error','network'), @('signature-error','signature'), @('postcheck-error','postcheck'))) {
        & $module { param($mode) $script:Mode=$mode; $script:InstallCount=0 } $case[0]
        & $module { param($s,$c,$w) Invoke-PbiPrerequisiteWorker 'unused' $s $c $w $true } $state $cancel $sandbox
        $text = [IO.File]::ReadAllText($state)
        if ($text -notmatch 'phase=failed' -or $text -notmatch "error=$($case[1])") { throw "错误分类失败: $($case[0]) $text" }
    }
    Write-Host 'PASS installer prerequisite worker'
} finally { Remove-Item -LiteralPath $sandbox -Recurse -Force }
