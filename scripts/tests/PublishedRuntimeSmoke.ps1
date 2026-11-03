# FilePath    : pbi-utils\scripts\tests\PublishedRuntimeSmoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 为发布验收隔离子进程环境并核验自包含或系统共享运行时的真实解析来源.

<#
.SYNOPSIS 按发布模式配置子进程环境和本轮宿主跟踪, 不改调用方或机器环境.
.PARAMETER Start 待启动进程的配置.
.PARAMETER DeploymentMode 发布清单声明的自包含或依赖框架模式.
.PARAMETER TracePath 本轮唯一宿主跟踪文件, 不存在的运行时目录建于同级且不创建.
.OUTPUTS 无; 无效模式或证据文件无法清理时抛出错误.
#>
function Set-PbiSmokeRuntimeEnvironment(
    [Diagnostics.ProcessStartInfo]$Start,
    [ValidateSet('self-contained', 'framework-dependent')][string]$DeploymentMode,
    [string]$TracePath
) {
    foreach ($key in @($Start.Environment.Keys)) {
        if ($key -like 'DOTNET_ROOT*' -or $key -in @('DOTNET_ROLL_FORWARD', 'DOTNET_ROLL_FORWARD_TO_PRERELEASE', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'DOTNET_STARTUP_HOOKS')) {
            $null = $Start.Environment.Remove($key)
        }
    }
    $Start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
    $Start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    if ($DeploymentMode -ceq 'self-contained') {
        $missingRuntime = Join-Path ([IO.Path]::GetDirectoryName($TracePath)) 'nonexistent-runtime'
        if (Test-Path -LiteralPath $missingRuntime) { throw '自包含验收隔离目录必须不存在' }
        $Start.Environment['DOTNET_ROOT'] = $missingRuntime
        $Start.Environment['DOTNET_ROOT_X64'] = $missingRuntime
    }
    if (Test-Path -LiteralPath $TracePath) { Remove-Item -LiteralPath $TracePath }
    $Start.Environment['DOTNET_HOST_TRACE'] = '1'
    $Start.Environment['DOTNET_HOST_TRACEFILE'] = $TracePath
    $Start.Environment['DOTNET_HOST_TRACE_VERBOSITY'] = '4'
}

<#
.SYNOPSIS 核验真实启动选定的 CoreCLR 和框架资产, 不以候选探测路径或启动失败证明成功.
.PARAMETER TracePath 已退出探针生成的宿主跟踪文件.
.PARAMETER PublishRoot 当前清单验证通过的发布目录.
.PARAMETER DeploymentMode 当前发布模式.
.PARAMETER SharedAssets 自包含包需从同目录解析的已核验框架资产名称.
.OUTPUTS 运行时来源证据; 跟踪缺失、来源错误、依赖框架包借用私有运行时均抛出错误.
#>
function Assert-PbiSmokeRuntimeResolution(
    [string]$TracePath,
    [string]$PublishRoot,
    [ValidateSet('self-contained', 'framework-dependent')][string]$DeploymentMode,
    [string[]]$SharedAssets = @()
) {
    $trace = Get-Content -LiteralPath $TracePath -Raw
    $resolved = @([regex]::Matches($trace, "(?m)^CoreCLR path = '([^'\r\n]+)'") | ForEach-Object { [IO.Path]::GetFullPath($_.Groups[1].Value) } | Sort-Object -Unique)
    if ($resolved.Count -eq 0) { throw '宿主跟踪没有实际选定的 CoreCLR, 不能判定运行时启动成功' }
    $publishPrefix = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\') + '\'
    foreach ($path in $resolved) {
        if ($DeploymentMode -ceq 'self-contained') {
            if ($path -ine (Join-Path $PublishRoot 'coreclr.dll')) { throw '自包含入口没有从发布目录加载 CoreCLR' }
            foreach ($name in @('System.Private.CoreLib.dll') + $SharedAssets) {
                if (!$trace.Contains((Join-Path $PublishRoot $name), [StringComparison]::OrdinalIgnoreCase)) { throw "发布宿主未从共享目录解析资产: $name" }
            }
        }
        else {
            if ($path.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) -or $path -notmatch '\\shared\\Microsoft\.NETCore\.App\\[^\\]+\\coreclr\.dll$') { throw '依赖框架入口没有从系统 shared 目录加载 CoreCLR' }
            $registered = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -Name InstallLocation -ErrorAction SilentlyContinue
            $systemRoots = @((Join-Path $env:ProgramFiles 'dotnet'))
            if ($registered -and $registered.InstallLocation) { $systemRoots += $registered.InstallLocation }
            if (@($systemRoots | Where-Object { $path.StartsWith(([IO.Path]::GetFullPath($_).TrimEnd('\') + '\shared\'), [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) { throw '依赖框架入口使用了未注册的私有运行时' }
            $coreLib = Join-Path ([IO.Path]::GetDirectoryName($path)) 'System.Private.CoreLib.dll'
            if (!$trace.Contains($coreLib, [StringComparison]::OrdinalIgnoreCase)) { throw '依赖框架入口没有解析系统共享核心程序集' }
            if ($trace.Contains((Join-Path $PublishRoot 'System.Private.CoreLib.dll'), [StringComparison]::OrdinalIgnoreCase)) { throw '依赖框架入口混入了包内核心程序集' }
        }
    }
    return @{ source = $(if ($DeploymentMode -ceq 'self-contained') { 'publish-directory' } else { 'system-shared' }); coreClrPaths = $resolved; verified = $true }
}
