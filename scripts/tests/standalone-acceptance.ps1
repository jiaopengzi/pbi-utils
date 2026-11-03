# FilePath    : pbi-utils\scripts\tests\standalone-acceptance.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在独立 Windows 测试机验证发布清单与 CLI, 记录环境及离线证据.

<#
.SYNOPSIS
使用 Windows 自带 PowerShell 5.1 或 PowerShell 7 验证完整发布目录, 不需要开发工具.
.DESCRIPTION
只读取发布文件与机器条件, 在脚本旁创建独立 evidence 目录. 不安装工具、不改系统或网络.
CleanMachineConfirmed 和 OfflineConfirmed 是操作者确认的前置条件, 不会配置机器.
离线探测仅辅助记录两个外网端点不可达, 不证明所有外网均被阻断; 网络隔离由操作者确认.
.PARAMETER PublishRoot
解压后的发布目录, 以清单判断 with-dotnet 或 without-dotnet, 默认在本脚本旁.
.PARAMETER CleanMachineConfirmed
操作者已确认机器未安装 Node 和 .NET SDK; 同时检查 PATH 与常见安装位置.
.PARAMETER OfflineConfirmed
操作者已确认测试机外网不可达, 可以保留局域网管理通道.
#>
[CmdletBinding()]
param(
    [string]$PublishRoot = (Join-Path $PSScriptRoot 'win-x64'),
    [switch]$CleanMachineConfirmed,
    [switch]$OfflineConfirmed
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PublishRoot)
$output = Join-Path $PSScriptRoot ('evidence-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $output
$utf8 = New-Object System.Text.UTF8Encoding($false)
$report = [ordered]@{ schemaVersion = 1; status = 'UNRUN'; timestamp = [DateTime]::UtcNow.ToString('o'); commands = @(); desktopInteraction = 'UNRUN' }
$exitCode = 0

<# .SYNOPSIS 以最多两秒 TCP 连接检查一个公开 HTTPS 端点, 失败仅表示该端点未连通. #>
function Test-ExternalEndpoint([string]$HostName) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($HostName, 443)
        if (!$task.Wait(2000)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
}

<#
.SYNOPSIS 拒绝发布根及其父级的链接、重解析点或网络路径.
.PARAMETER Path 待检查的本机绝对或相对路径.
.OUTPUTS 无返回值; 发现不安全路径或读取失败时抛出错误.
#>
function Assert-StandalonePublishPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    if ($current.StartsWith('\\')) { throw '发布只允许本机目录' }
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '发布路径包含链接或重解析点' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

<#
.SYNOPSIS 逐级校验后枚举发布文件, 不递归跟随链接.
.PARAMETER Root 待枚举的本机目录.
.OUTPUTS 安全目录中的 FileInfo; 目录缺失、不安全或枚举失败时抛出错误.
#>
function Get-StandalonePublishFiles([string]$Root) {
    Assert-StandalonePublishPath $Root
    if (!(Test-Path -LiteralPath $Root -PathType Container)) { throw '发布根目录不存在' }
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop) {
        Assert-StandalonePublishPath $entry.FullName
        if ($entry.PSIsContainer) { Get-StandalonePublishFiles $entry.FullName } else { $entry }
    }
}

<#
.SYNOPSIS 校验当前共享布局的元数据、发布模式、必需文件、精确文件集合与 SHA256.
.PARAMETER Root 解压后的发布根目录.
.OUTPUTS 当前 schemaVersion=3 的发布清单; 模式、布局、路径、集合或哈希无效时抛出错误.
.NOTES 独立测试机不依赖仓库模块或 .NET 新增 API, 保持 Windows PowerShell 5.1 可运行.
#>
function Assert-StandalonePublishedArtifacts([string]$Root) {
    $absolute = [IO.Path]::GetFullPath($Root)
    $actualFiles = @(Get-StandalonePublishFiles $absolute)
    $manifest = Get-Content -LiteralPath (Join-Path $absolute 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 3 -or $manifest.layout -cne 'shared' -or $manifest.runtime -cne 'win-x64' -or $manifest.scope -cnotin @('Core', 'All') -or $manifest.configuration -cnotin @('Debug', 'Release') -or !$manifest.version -or $manifest.deploymentMode -cnotin @('self-contained', 'framework-dependent')) { throw '发布清单版本、布局、目标、范围、配置或模式无效' }
    if ($manifest.configuration -ceq 'Debug' -and $manifest.deploymentMode -cne 'self-contained') { throw 'Debug 发布必须为自包含模式' }
    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $manifest.files) {
        $relative = $item.file
        if ($relative -isnot [string] -or !$relative -or $relative.Contains('\') -or $relative.Contains(':') -or $relative.StartsWith('/') -or $relative.Split('/') -contains '..' -or $relative.Split('/') -contains '.' -or $relative.Split('/') -contains '' -or $relative -ieq 'manifest.json') { throw '发布清单路径无效' }
        if ($relative.StartsWith('cli/', [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('host/', [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('desktop/', [StringComparison]::OrdinalIgnoreCase)) { throw '发布清单包含已移除的独立目录入口' }
        if ($manifest.scope -ceq 'Core' -and ($relative.StartsWith('web/', [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('PbiUtils.Desktop.', [StringComparison]::OrdinalIgnoreCase))) { throw '发布清单包含范围外文件' }
        if ($item.sha256 -isnot [string] -or $item.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $expected.ContainsKey($relative)) { throw '发布清单哈希无效或路径重复' }
        $expected.Add($relative, $item.sha256)
    }
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $prefix = $absolute.TrimEnd('\') + '\'
    foreach ($file in $actualFiles) {
        if (!$file.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '发布文件路径越界' }
        $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        if ($relative -ine 'manifest.json' -and !$actual.Add($relative)) { throw '发布文件路径重复' }
    }
    if (!$actual.SetEquals($expected.Keys)) { throw '发布文件集合与清单不一致 (额外或缺失文件)' }
    $required = @('pbi-utils.exe', 'pbi-utils.dll', 'pbi-utils.deps.json', 'pbi-utils.runtimeconfig.json', 'PbiUtils.Host.exe', 'PbiUtils.Host.dll', 'PbiUtils.Host.deps.json', 'PbiUtils.Host.runtimeconfig.json')
    $runtimeFiles = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')
    if ($manifest.deploymentMode -ceq 'self-contained') { $required += $runtimeFiles }
    elseif (@($runtimeFiles | Where-Object { $expected.ContainsKey($_) }).Count -gt 0) { throw 'without-dotnet 包含私有 .NET 运行时' }
    if ($manifest.scope -ceq 'All') { $required += @('PbiUtils.Desktop.exe', 'PbiUtils.Desktop.dll', 'PbiUtils.Desktop.deps.json', 'PbiUtils.Desktop.runtimeconfig.json', 'web/index.html') }
    foreach ($file in $required) { if (!$expected.ContainsKey($file)) { throw "发布必需文件缺失: $file" } }
    $entries = @('pbi-utils', 'PbiUtils.Host')
    if ($manifest.scope -ceq 'All') { $entries += 'PbiUtils.Desktop' }
    foreach ($entry in $entries) {
        $config = Get-Content -LiteralPath (Join-Path $absolute ($entry + '.runtimeconfig.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
        $properties = @($config.runtimeOptions.PSObject.Properties.Name)
        if ($manifest.deploymentMode -ceq 'self-contained') {
            if (!$config.runtimeOptions.includedFrameworks -or $properties -contains 'framework' -or $properties -contains 'frameworks') { throw 'with-dotnet 入口未声明自包含运行时' }
        }
        elseif ($properties -contains 'includedFrameworks' -or (!$config.runtimeOptions.framework -and !$config.runtimeOptions.frameworks)) { throw 'without-dotnet 入口未声明系统运行时依赖' }
    }
    foreach ($relative in $expected.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $absolute $relative) -Algorithm SHA256).Hash -ine $expected[$relative]) { throw "发布清单哈希不匹配: $relative" }
    }
    return $manifest
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '此验收脚本需要 Windows' }
    $manifest = Assert-StandalonePublishedArtifacts $root
    $nodeOnPath = $null -ne (Get-Command node.exe -ErrorAction SilentlyContinue)
    $dotnetOnPath = $null -ne (Get-Command dotnet.exe -ErrorAction SilentlyContinue)
    $knownSdkPresent = $false
    $knownNodePresent = $false
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA, $env:USERPROFILE)) {
        if (!$base) { continue }
        foreach ($relative in @('dotnet/sdk', '.dotnet/sdk', 'Microsoft/dotnet/sdk')) {
            $sdk = Join-Path $base $relative
            if ((Test-Path -LiteralPath $sdk) -and @(Get-ChildItem -LiteralPath $sdk -Directory).Count -gt 0) { $knownSdkPresent = $true }
        }
        if (Test-Path -LiteralPath (Join-Path $base 'nodejs/node.exe')) { $knownNodePresent = $true }
    }
    $runtime = 'missing'
    foreach ($registry in @('HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')) {
        $value = Get-ItemProperty -LiteralPath $registry -Name pv -ErrorAction SilentlyContinue
        if ($value -and $value.pv) { $runtime = $value.pv }
    }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $report.environment = [ordered]@{
        os = [Environment]::OSVersion.VersionString
        processArchitecture = $(if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' })
        powershell = $PSVersionTable.PSVersion.ToString()
        elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        evergreenRuntime = $runtime
        nodeOnPath = $nodeOnPath
        dotnetOnPath = $dotnetOnPath
        sdkInKnownLocations = $knownSdkPresent
        nodeInKnownLocations = $knownNodePresent
        cleanMachineConfirmedByOperator = [bool]$CleanMachineConfirmed
        offlineConfirmedByOperator = [bool]$OfflineConfirmed
    }
    if ($CleanMachineConfirmed -and ($nodeOnPath -or $knownSdkPresent -or $knownNodePresent)) { throw '干净机器声明与检测到的开发工具冲突' }
    if ($OfflineConfirmed) {
        $report.network = @('www.microsoft.com', 'nodejs.org') | ForEach-Object {
            @{ host = $_; port = 443; reachable = (Test-ExternalEndpoint $_) }
        }
        if (@($report.network | Where-Object { $_.reachable }).Count) { throw '测试机仍可连接外网, 不能记为离线验收' }
    }
    $cases = @(
        @{ name = 'business-host'; arguments = '--invalid-smoke-argument'; command = 'business-host startup'; format = 'host'; executable = 'PbiUtils.Host.exe'; expectedExitCode = 2 }
        @{ name = 'doctor'; arguments = 'doctor --json'; command = 'doctor'; format = 'json' }
        @{ name = 'templates'; arguments = 'new templates --json'; command = 'new templates'; format = 'json' }
        @{ name = 'help'; arguments = '--help --version --json'; command = '--help'; format = 'help' }
        @{ name = 'version'; arguments = '--version --json'; command = '--version'; format = 'version' }
    )
    foreach ($case in $cases) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $executable = if ($case.ContainsKey('executable')) { $case.executable } else { 'pbi-utils.exe' }
        $expectedExitCode = if ($case.ContainsKey('expectedExitCode')) { $case.expectedExitCode } else { 0 }
        $start.FileName = Join-Path $root $executable
        # 参数为此脚本内的固定产品命令, 不拼接用户输入或执行 shell.
        $start.Arguments = $case.arguments
        $start.WorkingDirectory = $root
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardOutputEncoding = $utf8
        $start.StandardErrorEncoding = $utf8
        foreach ($key in @($start.EnvironmentVariables.Keys)) {
            if ($key -like 'DOTNET_ROOT*' -or $key -in @('DOTNET_ROLL_FORWARD', 'DOTNET_ROLL_FORWARD_TO_PRERELEASE', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'DOTNET_STARTUP_HOOKS')) { $start.EnvironmentVariables.Remove($key) }
        }
        $start.EnvironmentVariables['PATH'] = Join-Path $env:SystemRoot 'System32'
        if ($manifest.deploymentMode -ceq 'self-contained') {
            $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $output 'nonexistent-runtime'
            $start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $output 'nonexistent-runtime'
        }
        $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $tracePath = Join-Path $output ($case.name + '.host-trace.txt')
        $start.EnvironmentVariables['DOTNET_HOST_TRACE'] = '1'
        $start.EnvironmentVariables['DOTNET_HOST_TRACEFILE'] = $tracePath
        $start.EnvironmentVariables['DOTNET_HOST_TRACE_VERBOSITY'] = '4'
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'CLI 验收超时' }
            $text = $stdout.GetAwaiter().GetResult()
            $errors = $stderr.GetAwaiter().GetResult()
            $extension = if ($case.format -eq 'json') { 'json' } else { 'txt' }
            [IO.File]::WriteAllText((Join-Path $output ($case.name + '.' + $extension)), $text, $utf8)
            [IO.File]::WriteAllText((Join-Path $output ($case.name + '.stderr.txt')), $errors, $utf8)
            if ($process.ExitCode -ne $expectedExitCode) { throw "发布入口启动或执行失败: $($case.name), 退出码=$($process.ExitCode); 请查看 stderr 与 host-trace 证据" }
            $trace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
            $coreClr = [regex]::Match($trace, "(?m)^CoreCLR path = '([^'\r\n]+)'")
            if (!$coreClr.Success) { throw '宿主跟踪缺少实际选定的 CoreCLR' }
            $coreClrPath = [IO.Path]::GetFullPath($coreClr.Groups[1].Value)
            if ($manifest.deploymentMode -ceq 'self-contained') {
                if ($coreClrPath -ine (Join-Path $root 'coreclr.dll')) { throw 'with-dotnet 未从发布目录加载运行时' }
            }
            else {
                $registered = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -Name InstallLocation -ErrorAction SilentlyContinue
                $systemRoots = @((Join-Path $env:ProgramFiles 'dotnet'))
                if ($registered -and $registered.InstallLocation) { $systemRoots += $registered.InstallLocation }
                if ($coreClrPath.StartsWith(($root.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
                    $coreClrPath -notmatch '\\shared\\Microsoft\.NETCore\.App\\[^\\]+\\coreclr\.dll$' -or
                    @($systemRoots | Where-Object { $coreClrPath.StartsWith(([IO.Path]::GetFullPath($_).TrimEnd('\') + '\shared\'), [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) { throw 'without-dotnet 未从系统 shared 目录加载运行时' }
            }
            if ($case.format -eq 'host') {
                if ($text -or $errors) { throw '后台启动参数拒绝不应输出业务数据' }
            }
            elseif ($case.format -eq 'json') {
                $data = $text | ConvertFrom-Json
                if ($data.schemaVersion -ne 1 -or !$data.ok -or $data.command -ne $case.command) { throw 'CLI JSON 不符合契约' }
                if ($case.name -eq 'doctor' -and ($data.data.version -ne $manifest.version -or !$data.data.environment.runtime)) { throw '诊断缺少产品版本或运行时信息' }
                if ($case.name -eq 'templates') {
                    $templateIds = @($data.data.templates | ForEach-Object { $_.id })
                    foreach ($id in @('builtin:blank')) {
                        if ($templateIds -notcontains $id) { throw '模板目录缺少内置模板' }
                    }
                }
            }
            elseif ($case.format -eq 'help') {
                if (!$text.Contains('pbi-utils doctor') -or !$text.Contains('pbi-utils new templates') -or $text.Contains('pbi-utils hello') -or $text.Contains('pbi-utils agent hello')) { throw '帮助不符合当前命令契约' }
            }
            elseif ($text.Trim() -cne $manifest.version) { throw '产品版本与发布清单不一致' }
            $report.commands += @{ command = $case.command; status = 'PASS'; exitCode = $process.ExitCode; coreClrPath = $coreClrPath }
        }
        finally { $process.Dispose() }
    }
    $report.version = $manifest.version
    $report.publishLayout = $manifest.layout
    $report.publishScope = $manifest.scope
    $report.deploymentMode = $manifest.deploymentMode
    $report.package = if ($manifest.deploymentMode -ceq 'self-contained') { 'with-dotnet' } else { 'without-dotnet' }
    $report.exactFileSetVerified = $true
    $report.manifestFilesVerified = $manifest.files.Count
    $report.manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'manifest.json') -Algorithm SHA256).Hash
    $report.status = 'PASS'
}
catch {
    $report.status = 'FAIL'
    $report.error = $_.Exception.Message
    $exitCode = 4
}
finally {
    [IO.File]::WriteAllText((Join-Path $output 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
}
Write-Output "$($report.status): $output"
Write-Output '此脚本只验证 CLI 和环境. 桌面交互、取消与关闭仍需实际操作并保存证据.'
exit $exitCode
