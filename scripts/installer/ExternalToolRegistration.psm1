# FilePath    : pbi-utils\scripts\installer\ExternalToolRegistration.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 开发与安装共用的 Power BI 外部工具登记、最小提权、精确归属和失败回滚.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ExternalToolDescriptor.psm1')
Import-Module (Join-Path $PSScriptRoot 'ExternalToolFiles.psm1')

<# .SYNOPSIS 递归检查异常链是否为拒绝访问, 参数为错误对象, 只有真实权限错误才允许申请提权. #>
function Test-PbiExternalToolAccessDenied([Exception]$Exception) {
    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [UnauthorizedAccessException]) { return $true }
        $current = $current.InnerException
    }
    return $false
}

<# .SYNOPSIS 通过系统 Windows PowerShell 5.1 提权执行固定登记动作, 参数均为已验证类别和 base64; UAC 取消及子进程错误抛错. #>
function Invoke-PbiExternalToolElevated([string]$Kind, [string]$Expected, [string]$Desired, [hashtable]$HelperFiles) {
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-external-tool-' + [Guid]::NewGuid().ToString('N'))
    $null = Resolve-PbiExternalToolPath $temporary
    $null = [IO.Directory]::CreateDirectory($temporary)
    $names = @('ExternalTool-Worker.ps1', 'ExternalToolFiles.psm1', 'ExternalToolDescriptor.psm1')
    $leases = @{}
    $primaryFailure = $null
    try {
        foreach ($name in $names) {
            if (!$HelperFiles.ContainsKey($name)) { throw '外部工具登记维护入口缺失' }
            $path = Resolve-PbiExternalToolPath (Join-Path $temporary $name)
            $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
            $leases[$name] = $stream
            $bytes = $HelperFiles[$name]
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
            # PowerShell 读脚本时不分享已有写句柄. 用禁止删除的 guard 接续到只读租约;
            # 过渡期若有写者则租约失败, 若已写完则后续哈希失败, 均不执行不可信代码.
            $guard = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try {
                $stream.Dispose()
                $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                $leases[$name] = $stream
            } finally { $guard.Dispose() }
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $expectedHash = [Convert]::ToBase64String($algorithm.ComputeHash($bytes))
                $actualHash = [Convert]::ToBase64String($algorithm.ComputeHash($stream))
                if ($actualHash -cne $expectedHash) { throw '外部工具提权维护文件写后核验失败' }
            } finally { $algorithm.Dispose(); $stream.Position = 0 }
        }
    # 文件只共享读取, 从创建至管理员进程退出期间阻止普通进程替换或注入 helper 代码.
    foreach ($name in $names) { $null = Resolve-PbiExternalToolPath (Join-Path $temporary $name) }
    $worker = Join-Path $temporary 'ExternalTool-Worker.ps1'
    $powershell = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (![Environment]::Is64BitProcess -and [Environment]::Is64BitOperatingSystem) { $powershell = Join-Path ([Environment]::GetFolderPath('Windows')) 'Sysnative\WindowsPowerShell\v1.0\powershell.exe' }
    $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $worker + '" -Kind ' + $Kind + ' -Expected "' + $Expected + '" -Desired "' + $Desired + '"'
    if ($arguments.Length + $powershell.Length + 4 -gt 32766) { throw '外部工具登记提权参数超过 Windows 命令行限制, 请在管理员 PowerShell 中重试' }
    try { $process = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru -Wait }
    catch {
        $errorValue = New-Object InvalidOperationException('外部工具登记未完成, 管理员授权已取消或无法启动', $_.Exception)
        $current = $_.Exception
        while ($null -ne $current) {
            if ($current -is [ComponentModel.Win32Exception] -and $current.NativeErrorCode -eq 1223) { $errorValue.Data['ExitCode'] = 130 }
            $current = $current.InnerException
        }
        throw $errorValue
    }
    try { if ($process.ExitCode -ne 0) { throw ('外部工具登记的管理员操作失败, 退出码 ' + $process.ExitCode + '; 登记冲突或权限问题须解决后重试') } }
    finally { $process.Dispose() }
    if ((Get-PbiExternalToolContent $Kind) -cne $Desired) { throw '管理员登记结束后的权威回读不一致' }
    } catch { $primaryFailure = $_.Exception; throw }
    finally {
        try {
            foreach ($stream in $leases.Values) { $stream.Dispose() }
            $absolute = Resolve-PbiExternalToolPath $temporary
            if ([IO.Path]::GetDirectoryName($absolute) -ine [IO.Path]::GetTempPath().TrimEnd('\') -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-external-tool-[0-9a-f]{32}$') { throw '外部工具临时维护目录清理越界' }
            foreach ($name in $leases.Keys) { [IO.File]::Delete((Join-Path $absolute $name)) }
            [IO.Directory]::Delete($absolute, $false)
        } catch {
            $cleanup = '外部工具临时维护文件清理未完成: ' + $_.Exception.Message
            if ($null -ne $primaryFailure) { [Console]::Error.WriteLine($cleanup) }
            else { throw ('外部工具登记已完成, 但' + $cleanup) }
        }
    }
}

<# .SYNOPSIS 尝试固定文件事务, 参数为类别、前后字节和非交互标志; 非交互权限不足明确失败, 交互仅对此动作请求管理员授权. #>
function Invoke-PbiExternalToolChange([string]$Kind, [string]$Expected, [string]$Desired, [bool]$NonInteractive, [hashtable]$HelperFiles) {
    try { Invoke-PbiExternalToolFileChange $Kind $Expected $Desired }
    catch {
        if (!(Test-PbiExternalToolAccessDenied $_.Exception)) { throw }
        if ($NonInteractive) {
            $errorValue = New-Object InvalidOperationException('无权写入 Power BI 外部工具共享目录; 非交互模式不会弹出 UAC, 请在管理员 PowerShell 中重新运行相同命令')
            $errorValue.Data['ExitCode'] = 3
            throw $errorValue
        }
        Invoke-PbiExternalToolElevated $Kind $Expected $Desired $HelperFiles
    }
}

<#
.SYNOPSIS 捕获登记原始字节及预期产品身份, 不写文件或申请权限.
.PARAMETER Kind 开发版或安装版, 使用各自固定名称.
.PARAMETER ExecutablePath 此次发布或安装的真实桌面入口, 不读取被安装事务锁定的 EXE.
.PARAMETER IconBytes 当前载荷的品牌 SVG, 用于判定现有描述是否仍属于本产品.
.OUTPUTS 可变事务快照; 未知登记标记为非自有, 供安装拒绝或卸载保留.
#>
function Get-PbiExternalToolSnapshot([ValidateSet('Development', 'Installed')][string]$Kind, [string]$ExecutablePath, [byte[]]$IconBytes) {
    $template = New-PbiExternalToolDescriptor $Kind $ExecutablePath '0.0.0' (ConvertTo-PbiExternalToolIcon $IconBytes)
    $content = Get-PbiExternalToolContent $Kind
    $owned = $content -ceq 'absent'
    if (!$owned) {
        try { $owned = Test-PbiExternalToolOwner $Kind (Read-PbiExternalToolDescriptor $Kind $content) ([PSCustomObject]$template) }
        catch { $owned = $false }
    }
    # 卸载稍后会独占维护文件; 在获得租约前捕获固定 helper 内容, 提权时只从这些已加载来源建立临时副本.
    $helpers = @{}
    foreach ($name in @('ExternalTool-Worker.ps1', 'ExternalToolFiles.psm1', 'ExternalToolDescriptor.psm1')) {
        $source = Resolve-PbiExternalToolPath (Join-Path $PSScriptRoot $name)
        $helpers[$name] = [IO.File]::ReadAllBytes($source)
    }
    return @{ Kind = $Kind; Path = (Get-PbiExternalToolRegistrationPath $Kind); Template = $template; Before = $content; Owned = $owned; Changed = $false; Applied = $null; NonInteractive = $false; HelperFiles = $helpers }
}

<# .SYNOPSIS 应用本轮登记或卸载, 参数为快照、版本、动作及非交互标志; 不接管未知登记, 相同内容只读成功, 失败由调用方执行恢复. #>
function Set-PbiExternalToolRegistration($Snapshot, [string]$Version, [ValidateSet('Install', 'Uninstall')][string]$Operation, [bool]$NonInteractive = $false) {
    if (!$Snapshot.Owned) {
        if ($Operation -ceq 'Uninstall') { Write-Host '已保留不属于本安装的同名 Power BI 外部工具登记'; return }
        throw '同名 Power BI 外部工具登记不属于本安装或开发仓库, 已保留; 请先处理登记冲突'
    }
    $desired = 'absent'
    if ($Operation -ceq 'Install') {
        $descriptor = New-PbiExternalToolDescriptor $Snapshot.Kind $Snapshot.Template.path $Version $Snapshot.Template.iconData
        $desired = ConvertTo-PbiExternalToolContent $descriptor
    }
    if ((Get-PbiExternalToolContent $Snapshot.Kind) -cne $Snapshot.Before) { throw 'Power BI 外部工具登记在预检后发生变化, 已保留' }
    if ($desired -ceq $Snapshot.Before) { return }
    $Snapshot.NonInteractive = $NonInteractive
    $Snapshot.Applied = $desired
    $Snapshot.Changed = $true
    Invoke-PbiExternalToolChange $Snapshot.Kind $Snapshot.Before $desired $NonInteractive $Snapshot.HelperFiles
}

<# .SYNOPSIS 恢复本轮实际改动的登记, 参数为快照; 已恢复无需写, 仅精确匹配本轮内容才回滚, 并发改动保留并报错. #>
function Restore-PbiExternalToolRegistration($Snapshot) {
    if (!$Snapshot.Changed) { return }
    $current = Get-PbiExternalToolContent $Snapshot.Kind
    if ($current -ceq $Snapshot.Before) { return }
    if ($current -cne $Snapshot.Applied) { throw 'Power BI 外部工具登记在操作后发生变化, 未覆盖用户更改' }
    Invoke-PbiExternalToolChange $Snapshot.Kind $Snapshot.Applied $Snapshot.Before $Snapshot.NonInteractive $Snapshot.HelperFiles
}

<#
.SYNOPSIS 为已验证的开发发布包登记独立 Power BI 外部工具, 不更改安装版入口.
.PARAMETER PublishRoot 标准 artifacts/build 目录中的本轮发布根, 必须包含桌面 EXE 和品牌图标.
.PARAMETER Version 本轮发布清单版本.
.PARAMETER NonInteractive 权限不足时直接失败并提示管理员重跑, 不弹 UAC.
.OUTPUTS 登记 Path、ExecutablePath 和 Version; 写入失败恢复本轮内容, 不覆盖其他仓库或用户更改.
#>
function Register-PbiDevelopmentExternalTool {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$PublishRoot, [Parameter(Mandatory = $true)][string]$Version, [switch]$NonInteractive)
    $root = Resolve-PbiExternalToolPath $PublishRoot
    $executable = Resolve-PbiExternalToolPath (Join-Path $root 'PbiUtils.Desktop.exe')
    $icon = Resolve-PbiExternalToolPath (Join-Path $root 'web\branding\logo.svg')
    if (![IO.File]::Exists($executable) -or ![IO.File]::Exists($icon)) { throw '开发外部工具登记需要完整桌面发布产物和品牌图标' }
    $snapshot = Get-PbiExternalToolSnapshot Development $executable ([IO.File]::ReadAllBytes($icon))
    try { Set-PbiExternalToolRegistration $snapshot $Version Install ([bool]$NonInteractive) }
    catch {
        $primary = $_.Exception.Message
        try { Restore-PbiExternalToolRegistration $snapshot }
        catch { throw ($primary + '; 开发外部工具登记恢复未完成: ' + $_.Exception.Message) }
        throw
    }
    return @{ Path = $snapshot.Path; ExecutablePath = $executable; Version = $Version }
}

Export-ModuleMember -Function Register-PbiDevelopmentExternalTool, Get-PbiExternalToolSnapshot, Set-PbiExternalToolRegistration, Restore-PbiExternalToolRegistration
