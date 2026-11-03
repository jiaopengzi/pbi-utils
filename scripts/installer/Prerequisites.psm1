# FilePath    : pbi-utils\scripts\installer\Prerequisites.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : Windows PowerShell 5.1 可执行的前置检测、微软下载校验和受限安装流程.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

<# .SYNOPSIS 解析稳定的三段版本; 预览、缺失或非法值直接报错. #>
function ConvertTo-PbiStableVersion([string]$Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+$') { throw 'PREREQUISITE_VERSION_INVALID' }
    return [Version]$Value
}

<#
.SYNOPSIS 校验构建固化的前置清单, 不访问网络或系统安装状态.
.PARAMETER ManifestPath 安装器内随附的 schema 1 JSON 文件.
.OUTPUTS 已校验的清单对象; 缺字段、不支持的架构或非官方 HTTPS 地址均报错.
#>
function Read-PbiPrerequisiteManifest([string]$ManifestPath) {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.dotNetMode -notin @('with-dotnet', 'without-dotnet')) { throw 'PREREQUISITE_MANIFEST_INVALID' }
    $dotnet = $manifest.dotnet
    if ($dotnet.architecture -ne 'x64' -or @($dotnet.frameworks).Count -ne 2 -or
        @($dotnet.frameworks | Sort-Object) -join ',' -ne 'Microsoft.NETCore.App,Microsoft.WindowsDesktop.App') { throw 'PREREQUISITE_FRAMEWORKS_INVALID' }
    $minimum = ConvertTo-PbiStableVersion $dotnet.minimumVersion
    $recommended = ConvertTo-PbiStableVersion $dotnet.runtimeVersion
    $null = ConvertTo-PbiStableVersion $dotnet.sdkVersion
    if ($minimum.Major -ne $recommended.Major -or $minimum.Minor -ne $recommended.Minor -or $recommended -lt $minimum) { throw 'PREREQUISITE_RUNTIME_VERSION_INVALID' }
    $uri = [Uri]$dotnet.url
    if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or
        $uri.Host -notin @('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com', 'dotnetcli.azureedge.net') -or
        $uri.AbsolutePath -notlike '*.exe' -or $dotnet.sha512 -notmatch '^[A-Fa-f0-9]{128}$') { throw 'PREREQUISITE_DOTNET_DOWNLOAD_INVALID' }
    if ($manifest.webView2.url -cne 'https://go.microsoft.com/fwlink/p/?LinkId=2124703') { throw 'PREREQUISITE_WEBVIEW_DOWNLOAD_INVALID' }
    return $manifest
}

<#
.SYNOPSIS 按明确注册表视图读取单个值, 缺失返回 null, 访问失败直接报错.
.PARAMETER Hive LocalMachine 或 CurrentUser, 不读取产品或用户业务数据.
.PARAMETER View Registry32 或 Registry64, 避免启动脚本位数改变检测结果.
.PARAMETER SubKey 官方安装登记键; Name 是需要读取的值名.
.OUTPUTS 注册表值或 null; 所有注册表句柄在返回前释放.
#>
function Get-PbiRegistryValue([Microsoft.Win32.RegistryHive]$Hive, [Microsoft.Win32.RegistryView]$View, [string]$SubKey, [string]$Name) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
    $key = $null
    try {
        $key = $base.OpenSubKey($SubKey, $false)
        if ($null -eq $key) { return $null }
        return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    } finally {
        if ($null -ne $key) { $key.Dispose() }
        $base.Dispose()
    }
}

<# .SYNOPSIS 获取 x64 host 使用的登记位置, 无登记才采用系统 x64 Program Files 默认位置; 不使用 PATH 或 DOTNET_ROOT. #>
function Get-PbiDotNetInstallationRoot {
    if (![Environment]::Is64BitOperatingSystem) { return $null }
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
        $location = Get-PbiRegistryValue LocalMachine $view 'SOFTWARE\dotnet\Setup\InstalledVersions\x64' 'InstallLocation'
        if (![string]::IsNullOrWhiteSpace([string]$location)) { return [IO.Path]::GetFullPath([string]$location) }
    }
    $programFiles = Get-PbiRegistryValue LocalMachine Registry64 'SOFTWARE\Microsoft\Windows\CurrentVersion' 'ProgramFilesDir'
    if ([string]::IsNullOrWhiteSpace([string]$programFiles)) { return $null }
    return Join-Path ([string]$programFiles) 'dotnet'
}

<# .SYNOPSIS 读取 PE 机器类型确认真实 x64 可执行文件; 非 PE、截断或其他架构返回 false, I/O 失败报错. #>
function Test-PbiX64Executable([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { return $false }
        $stream.Position = $offset
        return ($reader.ReadUInt32() -eq 0x4550 -and $reader.ReadUInt16() -eq 0x8664)
    } finally { $reader.Dispose(); $stream.Dispose() }
}

<#
.SYNOPSIS 核对实际 host 列出的两个共享框架, 只接受同安装根、稳定同系列且不低于最低版本.
.PARAMETER Lines 已核验 x64 host 的 --list-runtimes 标准输出行.
.PARAMETER MinimumVersion 产品 runtimeconfig 的最低版本; 不要求开发 SDK 或推荐补丁完全一致.
.PARAMETER InstallationRoot 当前 host 的权威登记根, 不合并其他安装副本的框架.
.OUTPUTS 两个共享框架均满足返回 true, 其他情况返回 false.
#>
function Test-PbiDotNetRuntimeInventory([string[]]$Lines, [string]$MinimumVersion, [string]$InstallationRoot) {
    $minimum = ConvertTo-PbiStableVersion $MinimumVersion
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $found = $false
        $expectedPath = [IO.Path]::GetFullPath((Join-Path $InstallationRoot ('shared\' + $framework))).TrimEnd('\')
        foreach ($line in $Lines) {
            if ($line -match '^(Microsoft\.NETCore\.App|Microsoft\.WindowsDesktop\.App) (\d+\.\d+\.\d+) \[(.+)\]$' -and $Matches[1] -eq $framework) {
                $version = [Version]$Matches[2]
                $frameworkPath = [IO.Path]::GetFullPath($Matches[3]).TrimEnd('\')
                if ($frameworkPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase) -and
                    $version.Major -eq $minimum.Major -and $version.Minor -eq $minimum.Minor -and $version -ge $minimum) { $found = $true }
            }
        }
        if (!$found) { return $false }
    }
    return $true
}

<# .SYNOPSIS 在 15 秒内运行登记的 x64 host 并核实共享框架; 缺 host 返回 false, 执行异常/超时报错而不冒充缺失. #>
function Test-PbiInstalledDotNet([string]$MinimumVersion) {
    $root = Get-PbiDotNetInstallationRoot
    if ([string]::IsNullOrWhiteSpace($root)) { return $false }
    $hostPath = Join-Path $root 'dotnet.exe'
    if (!(Test-Path -LiteralPath $hostPath -PathType Leaf)) { return $false }
    if (!(Test-PbiX64Executable $hostPath)) { return $false }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $hostPath; $start.Arguments = '--list-runtimes'
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ([string]$name -like 'DOTNET_*' -or [string]$name -like 'COREHOST_*') { $start.EnvironmentVariables.Remove([string]$name) }
    }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw 'PREREQUISITE_DOTNET_PROBE_START_FAILED' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit(); throw 'PREREQUISITE_DOTNET_PROBE_TIMEOUT' }
        if ($process.ExitCode -ne 0) { throw 'PREREQUISITE_DOTNET_PROBE_FAILED' }
        $lines = $stdout.GetAwaiter().GetResult() -split '\r?\n'
        $null = $stderr.GetAwaiter().GetResult()
        return Test-PbiDotNetRuntimeInventory $lines $MinimumVersion $root
    } finally { $process.Dispose() }
}

<# .SYNOPSIS 按微软规定检查 Evergreen WebView2 的 HKLM/HKCU pv; Edge 浏览器和零版本不构成前提满足. #>
function Test-PbiInstalledWebView2 {
    $key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        $value = [string](Get-PbiRegistryValue $hive Registry32 $key 'pv')
        $version = $null
        if ([Version]::TryParse($value, [ref]$version) -and $version -gt [Version]'0.0.0.0') { return $true }
    }
    return $false
}

<#
.SYNOPSIS 仅通过 HTTPS 下载到本次临时文件, 最多 5 次跳转、单次读等待 30 秒、总时限 10 分钟.
.PARAMETER Url 已由 manifest 校验的微软入口; 每次重定向仍须 HTTPS.
.PARAMETER Destination 本轮拥有的临时文件路径; 下载或取消失败直接传播, 不执行部分文件.
#>
function Receive-PbiInstaller([string]$Url, [string]$Destination, [string]$CancelPath = '') {
    $previousProtocol = [Net.ServicePointManager]::SecurityProtocol
    $response = $null; $inputStream = $null; $outputStream = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        [Net.ServicePointManager]::SecurityProtocol = $previousProtocol -bor [Net.SecurityProtocolType]::Tls12
        $uri = [Uri]$Url
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if ($CancelPath -and (Test-Path -LiteralPath $CancelPath)) { throw 'PREREQUISITE_CANCELLED' }
            if ($uri.Scheme -ne 'https' -or $uri.UserInfo) { throw 'PREREQUISITE_DOWNLOAD_HTTPS_REQUIRED' }
            $request = [Net.HttpWebRequest]::Create($uri)
            $request.AllowAutoRedirect = $false; $request.Timeout = 30000; $request.ReadWriteTimeout = 30000
            $response = $request.GetResponse()
            if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
                $location = $response.Headers['Location']
                if ([string]::IsNullOrWhiteSpace($location)) { throw 'PREREQUISITE_DOWNLOAD_REDIRECT_INVALID' }
                $uri = New-Object Uri($uri, $location)
                $response.Dispose(); $response = $null
                continue
            }
            if ([int]$response.StatusCode -ne 200) { throw 'PREREQUISITE_DOWNLOAD_HTTP_FAILED' }
            break
        }
        if ($null -eq $response) { throw 'PREREQUISITE_DOWNLOAD_REDIRECT_LIMIT' }
        $inputStream = $response.GetResponseStream()
        $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] 65536
        while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($CancelPath -and (Test-Path -LiteralPath $CancelPath)) { throw 'PREREQUISITE_CANCELLED' }
            if ($watch.Elapsed.TotalMinutes -ge 10) { throw 'PREREQUISITE_DOWNLOAD_TIMEOUT' }
            $outputStream.Write($buffer, 0, $read)
        }
        if ($outputStream.Length -eq 0) { throw 'PREREQUISITE_DOWNLOAD_EMPTY' }
    } finally {
        if ($null -ne $outputStream) { $outputStream.Dispose() }
        if ($null -ne $inputStream) { $inputStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        [Net.ServicePointManager]::SecurityProtocol = $previousProtocol
    }
}

<#
.SYNOPSIS 校验下载文件的 SHA512 和微软 Authenticode; WebView2 浮动引导程序仅有官方签名可核验.
.PARAMETER Path 本轮下载的 EXE; Sha512 为 .NET 官方元数据哈希, WebView2 传空字符串.
.PARAMETER Kind dotnet 要求官方 SHA512 并允许微软的 .NET 产品证书; webview2 只接受微软公司证书.
.OUTPUTS 无; 哈希缺失/不匹配、签名不可信或发布者不符合指定组件时抛出固定校验错误.
.NOTES 签名必须为 Valid 且组织为 Microsoft Corporation; CN 可表示产品, 不能假定始终等于组织名.
#>
function Assert-PbiTrustedInstaller([string]$Path, [string]$Sha512, [ValidateSet('dotnet', 'webview2')][string]$Kind) {
    if ($Kind -eq 'dotnet' -and $Sha512 -notmatch '^[A-Fa-f0-9]{128}$') { throw 'PREREQUISITE_SHA512_MISMATCH' }
    if ($Sha512 -and (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash -ine $Sha512) { throw 'PREREQUISITE_SHA512_MISMATCH' }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $commonNamePattern = if ($Kind -eq 'dotnet') { '(^|,\s*)CN=(Microsoft Corporation|\.NET)(,|$)' } else { '(^|,\s*)CN=Microsoft Corporation(,|$)' }
    if ([string]$signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)' -or
        $signature.SignerCertificate.Subject -notmatch $commonNamePattern) { throw 'PREREQUISITE_MICROSOFT_SIGNATURE_INVALID' }
}

<#
.SYNOPSIS 锁定并校验微软 EXE 后调用其官方安装参数, 等待真实退出码; 不提权 PowerShell 或产品进程.
.PARAMETER Path 下载文件; Sha512 为 .NET 元数据哈希; Kind 选择 dotnet 或 webview2.
.OUTPUTS 官方安装器退出码. UAC 取消和启动失败直接报错, 不自动重启或强杀安装器.
#>
function Invoke-PbiOfficialInstaller([string]$Path, [string]$Sha512, [ValidateSet('dotnet', 'webview2')][string]$Kind, [string]$CancelPath = '') {
    $fileLock = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-PbiTrustedInstaller $Path $Sha512 $Kind
        if ($CancelPath -and (Test-Path -LiteralPath $CancelPath)) { throw 'PREREQUISITE_CANCELLED' }
        $arguments = if ($Kind -eq 'dotnet') { '/install /passive /norestart' } else { '/silent /install' }
        $parameters = @{ FilePath = $Path; ArgumentList = $arguments; PassThru = $true; Wait = $true; WindowStyle = 'Hidden' }
        if ($Kind -eq 'dotnet') {
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            try {
                $principal = New-Object Security.Principal.WindowsPrincipal($identity)
                if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { $parameters.Verb = 'RunAs' }
            } finally { $identity.Dispose() }
        }
        $process = Start-Process @parameters
        try { return $process.ExitCode } finally { $process.Dispose() }
    } finally { $fileLock.Dispose() }
}

<#
.SYNOPSIS 检测安装前提, 仅补齐缺失组件并执行安装后复核; CheckOnly 不下载或安装.
.PARAMETER ManifestPath 安装包内构建固化的 schema 1 JSON.
.PARAMETER CheckOnly 只读检测, 满足返回 0, 缺失返回 3.
.OUTPUTS 0 满足, 3010 已满足但安装器要求重启, 或官方安装器失败码; 异常由入口映射为非零.
.NOTES 诊断仅输出固定组件/状态, 不输出用户路径、网络异常原文或敏感环境数据.
#>
function Invoke-PbiInstallerPrerequisites {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ManifestPath, [switch]$CheckOnly)
    $manifest = Read-PbiPrerequisiteManifest $ManifestPath
    $dotnetReady = $manifest.dotNetMode -eq 'with-dotnet' -or (Test-PbiInstalledDotNet $manifest.dotnet.minimumVersion)
    $webviewReady = Test-PbiInstalledWebView2
    Write-Host ('PREREQUISITE dotnet=' + $dotnetReady + ' webview2=' + $webviewReady)
    if ($dotnetReady -and $webviewReady) { return 0 }
    if ($CheckOnly) { return 3 }
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('pbi-prerequisites-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $temporaryRoot
    $reboot = $false; $failure = $null; $exitCode = 0
    try {
        foreach ($kind in @('dotnet', 'webview2')) {
            if (($kind -eq 'dotnet' -and $dotnetReady) -or ($kind -eq 'webview2' -and $webviewReady)) { continue }
            $url = if ($kind -eq 'dotnet') { $manifest.dotnet.url } else { $manifest.webView2.url }
            $hash = if ($kind -eq 'dotnet') { $manifest.dotnet.sha512 } else { '' }
            $file = Join-Path $temporaryRoot ($kind + '.exe')
            Write-Host "PREREQUISITE $kind downloading"
            Receive-PbiInstaller $url $file
            Write-Host "PREREQUISITE $kind installing"
            $code = Invoke-PbiOfficialInstaller $file $hash $kind
            if ($code -notin @(0, 3010)) { $exitCode = $code; break }
            if ($code -eq 3010) { $reboot = $true }
            $ready = if ($kind -eq 'dotnet') { Test-PbiInstalledDotNet $manifest.dotnet.minimumVersion } else { Test-PbiInstalledWebView2 }
            if (!$ready) { throw 'PREREQUISITE_POSTCHECK_FAILED' }
            Write-Host "PREREQUISITE $kind verified"
        }
    } catch { $failure = $_ }
    finally {
        try {
            $resolved = [IO.Path]::GetFullPath($temporaryRoot)
            $expected = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
            if (!$resolved.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'PREREQUISITE_CLEANUP_PATH_INVALID' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        } catch {
            Write-Host 'PREREQUISITE cleanup failed'
            if ($null -eq $failure -and $exitCode -eq 0) { $failure = $_ }
        }
    }
    if ($null -ne $failure) { throw $failure }
    if ($exitCode -ne 0) { return $exitCode }
    if ($reboot) { return 3010 }
    return 0
}


<#
.SYNOPSIS 将固定字段状态原子替换到本轮私有目录, 供 NSIS 定时读取.
.PARAMETER StatePath 固定状态文件; State 是仅包含受限 ASCII 字段的状态表.
.OUTPUTS 无; 写入失败抛错, 不把旧状态冒充新状态.
#>
function Write-PbiPrerequisiteWorkerState([string]$StatePath, [hashtable]$State) {
    $temporary = $StatePath + '.new'
    $lines = @('[state]') + @('phase', 'dotnet', 'webview2', 'error', 'reboot' | ForEach-Object { "$_=$($State[$_])" })
    [IO.File]::WriteAllLines($temporary, [string[]]$lines, [Text.Encoding]::ASCII)
    if ([IO.File]::Exists($StatePath)) { $backup = $StatePath + '.previous'; [IO.File]::Replace($temporary, $StatePath, $backup); [IO.File]::Delete($backup) }
    else { [IO.File]::Move($temporary, $StatePath) }
}

<#
.SYNOPSIS 后台逐项检测并按用户选择安装缺失组件, 发布原子状态并等待官方安装器退出.
.PARAMETER ManifestPath 已固化清单; StatePath、CancelPath 和 WorkRoot 属于本轮私有目录.
.PARAMETER InstallMissing 为 true 时允许下载及启动官方安装器; false 永不下载.
.OUTPUTS 通过固定状态文件报告 ready/missing/failed/cancelled 和重启标记.
.NOTES 取消标记在下载及启动前生效; 官方安装开始后等待退出和复检, 不终止官方进程.
#>
function Invoke-PbiPrerequisiteWorker([string]$ManifestPath, [string]$StatePath, [string]$CancelPath, [string]$WorkRoot, [bool]$InstallMissing) {
    $state = @{ phase = 'checking'; dotnet = 'checking'; webview2 = 'checking'; error = 'none'; reboot = '0' }
    Write-PbiPrerequisiteWorkerState $StatePath $state
    try {
        $manifest = Read-PbiPrerequisiteManifest $ManifestPath
        foreach ($kind in @('dotnet', 'webview2')) {
            try {
                $ready = if ($kind -eq 'dotnet') { $manifest.dotNetMode -eq 'with-dotnet' -or (Test-PbiInstalledDotNet $manifest.dotnet.minimumVersion) } else { Test-PbiInstalledWebView2 }
                $state[$kind] = if ($ready) { if ($kind -eq 'dotnet' -and $manifest.dotNetMode -eq 'with-dotnet') { 'bundled' } else { 'ready' } } else { 'missing' }
            } catch { $state[$kind] = 'failed' }
            Write-PbiPrerequisiteWorkerState $StatePath $state
        }
        if ($state.dotnet -eq 'failed' -or $state.webview2 -eq 'failed') { throw 'PREREQUISITE_PROBE_FAILED' }
        if ($InstallMissing) {
            foreach ($kind in @('dotnet', 'webview2')) {
                if ($state[$kind] -ne 'missing') { continue }
                if (Test-Path -LiteralPath $CancelPath) { throw 'PREREQUISITE_CANCELLED' }
                $url = if ($kind -eq 'dotnet') { $manifest.dotnet.url } else { $manifest.webView2.url }
                $hash = if ($kind -eq 'dotnet') { $manifest.dotnet.sha512 } else { '' }
                $file = Join-Path $WorkRoot ($kind + '.exe')
                $state.phase = 'downloading'; Write-PbiPrerequisiteWorkerState $StatePath $state
                Receive-PbiInstaller $url $file $CancelPath
                if (Test-Path -LiteralPath $CancelPath) { throw 'PREREQUISITE_CANCELLED' }
                $state.phase = 'installing'; Write-PbiPrerequisiteWorkerState $StatePath $state
                $code = Invoke-PbiOfficialInstaller $file $hash $kind $CancelPath
                if ($code -notin @(0, 3010)) { throw 'PREREQUISITE_INSTALL_FAILED' }
                if ($code -eq 3010) { $state.reboot = '1' }
                $state.phase = 'checking'; Write-PbiPrerequisiteWorkerState $StatePath $state
                $ready = if ($kind -eq 'dotnet') { Test-PbiInstalledDotNet $manifest.dotnet.minimumVersion } else { Test-PbiInstalledWebView2 }
                if (!$ready) { throw 'PREREQUISITE_POSTCHECK_FAILED' }
                $state[$kind] = 'ready'; Write-PbiPrerequisiteWorkerState $StatePath $state
                if (Test-Path -LiteralPath $CancelPath) { throw 'PREREQUISITE_CANCELLED' }
            }
        }
        $state.phase = if ($state.dotnet -in @('ready', 'bundled') -and $state.webview2 -eq 'ready') { 'ready' } else { 'missing' }
    } catch {
        $errorCode = [string]$_.Exception.Message
        $failedPhase = $state.phase
        if ($state.dotnet -eq 'checking') { $state.dotnet = 'failed' }
        if ($state.webview2 -eq 'checking') { $state.webview2 = 'failed' }
        $state.phase = if ($errorCode -eq 'PREREQUISITE_CANCELLED') { 'cancelled' } else { 'failed' }
        $state.error = switch -Regex ($errorCode) {
            '^PREREQUISITE_CANCELLED$' { 'cancelled'; break }
            '^PREREQUISITE_(SHA512_MISMATCH|MICROSOFT_SIGNATURE_INVALID)$' { 'signature'; break }
            '^PREREQUISITE_POSTCHECK_FAILED$' { 'postcheck'; break }
            '^PREREQUISITE_PROBE_FAILED$' { 'probe'; break }
            '^PREREQUISITE_INSTALL_FAILED$' { 'install'; break }
            default { if ($failedPhase -eq 'downloading') { 'network' } elseif ($failedPhase -eq 'installing') { 'install' } else { 'probe' } }
        }
    }
    Write-PbiPrerequisiteWorkerState $StatePath $state
}

Export-ModuleMember -Function Invoke-PbiInstallerPrerequisites, Invoke-PbiPrerequisiteWorker
