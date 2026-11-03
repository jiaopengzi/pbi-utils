# FilePath    : pbi-utils\scripts\installer\InstallationShell.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 安装器当前用户身份、进程核验、HKCU 注册及快捷方式适配, 支持事务快照恢复.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ExternalToolRegistration.psm1') -Force
$script:ProductKey = 'Software\pbi-utils\Desktop'
$script:UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\pbi-utils-desktop'

<# .SYNOPSIS 核验三个入口没有在目标目录运行, 参数为安装根; 无法核验或仍运行时拒绝, 不结束进程. #>
function Assert-PbiInstallationIdle([string]$Root) {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='PbiUtils.Desktop.exe' OR Name='PbiUtils.Host.exe' OR Name='pbi-utils.exe'" -ErrorAction Stop)
    foreach ($process in $processes) {
        if (!$process.ExecutablePath -or ![IO.Path]::IsPathRooted($process.ExecutablePath)) { throw '无法核验 pbi-utils 进程的真实路径, 请关闭本软件及 MCP 客户端后重试' }
        if ([IO.Path]::GetFullPath($process.ExecutablePath).StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'pbi-utils 仍在运行, 请暂停 MCP 接入并等待操作完成, 关闭桌面、CLI 和后台后重试'
        }
    }
}

<# .SYNOPSIS 按 Desktop 和 StartMenu 名称返回本安装器管理的用户快捷方式路径, 不依赖枚举顺序且无副作用. #>
function Get-PbiShortcutPaths {
    return @{
        StartMenu = (Join-Path ([Environment]::GetFolderPath('Programs')) 'pbi-utils\pbi-utils.lnk')
        Desktop = (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'pbi-utils.lnk')
    }
}

<# .SYNOPSIS 读取专属 HKCU 键的值及类型, 参数为键路径, 返回空值或快照, 子键存在时拒绝. #>
function Read-PbiInstallRegistry([string]$Path) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if ($null -eq $key) { return $null }
    try {
        if ($key.SubKeyCount -ne 0) { throw '安装注册项包含未知子键, 未作更改' }
        $values = @()
        foreach ($name in $key.GetValueNames()) { $values += @{ Name = $name; Value = $key.GetValue($name, $null, 'DoNotExpandEnvironmentNames'); Kind = $key.GetValueKind($name) } }
        return @{ Values = $values }
    } finally { $key.Dispose() }
}

<#
.SYNOPSIS 获取当前用户登记及两处快捷方式的归属快照, 同名外部入口保留并跳过.
.PARAMETER Root 已核验的安装根目录.
.PARAMETER Installed 是否已具有本安装的有效状态, 新安装不接管同名入口.
.PARAMETER CreateDesktopShortcut 安装时是否创建桌面入口, 默认 true; false 只移除已拥有入口.
.PARAMETER CreateStartMenuShortcut 安装时是否创建开始菜单入口, 默认 true; 卸载不使用这两项创建选择.
.PARAMETER PayloadRoot 安装时的已验证载荷根, 卸载时为空并读取当前安装的品牌图标.
.PARAMETER RegistrationNonInteractive true 时固定登记权限不足直接失败, 不弹出 UAC.
.OUTPUTS 固定修改范围、创建选择及原始字节快照; 注册身份冲突或读取失败时抛错.
#>
function Get-PbiInstallationShellSnapshot([string]$Root, [bool]$Installed, [bool]$CreateDesktopShortcut = $true, [bool]$CreateStartMenuShortcut = $true, [string]$PayloadRoot = '', [bool]$RegistrationNonInteractive = $false) {
    $registry = @{}
    foreach ($path in @($script:ProductKey, $script:UninstallKey)) {
        $saved = Read-PbiInstallRegistry $path
        if ($null -ne $saved) {
            $field = if ($path -ceq $script:ProductKey) { 'InstallRoot' } else { 'InstallLocation' }
            $owner = @($saved.Values | Where-Object Name -CEQ $field)
            if (!$Installed -or $owner.Count -ne 1 -or ![string]::Equals($owner[0].Value, $Root, [StringComparison]::OrdinalIgnoreCase)) { throw '安装注册身份与当前目录不一致, 未覆盖其他安装' }
        }
        $registry[$path] = $saved
    }
    $shortcuts = @{}; $selections = @{}
    $paths = Get-PbiShortcutPaths
    $choices = @{ Desktop = $CreateDesktopShortcut; StartMenu = $CreateStartMenuShortcut }
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($name in $paths.Keys) {
            $path = $paths[$name]
            if ([IO.Directory]::Exists($path)) {
                Write-Host ('已保留同名目录, 跳过快捷方式: ' + $path)
                continue
            }
            if ([IO.File]::Exists($path)) {
                if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '快捷方式路径包含链接' }
                $shortcut = $shell.CreateShortcut($path)
                try {
                    if (!$Installed -or ![string]::Equals($shortcut.TargetPath, (Join-Path $Root 'app\PbiUtils.Desktop.exe'), [StringComparison]::OrdinalIgnoreCase)) {
                        Write-Host ('已保留不属于本安装的同名快捷方式, 跳过此入口: ' + $path)
                        continue
                    }
                } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
                $shortcuts[$path] = [IO.File]::ReadAllBytes($path)
            } else { $shortcuts[$path] = $null }
            $selections[$path] = $choices[$name]
        }
    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $iconBytes = Read-PbiInstallationExternalToolIcon $Root $PayloadRoot
    $externalTool = Get-PbiExternalToolSnapshot Installed (Join-Path $Root 'app\PbiUtils.Desktop.exe') $iconBytes
    $externalTool.NonInteractive = $RegistrationNonInteractive
    return @{ Registry = $registry; Shortcuts = $shortcuts; ShortcutSelections = $selections; ShortcutChanges = @{}; ExternalTool = $externalTool }
}

<# .SYNOPSIS 在文件租约前读取当前载荷的 SVG, 参数为安装根及可选载荷根, 返回图标字节; 缺失或读取错误直接抛错. #>
function Read-PbiInstallationExternalToolIcon([string]$Root, [string]$PayloadRoot) {
    $source = if ($PayloadRoot) { $PayloadRoot } else { $Root }
    return [IO.File]::ReadAllBytes((Join-Path $source 'app\web\branding\logo.svg'))
}

<# .SYNOPSIS 核验快捷方式与预期字节相同, 参数为路径和字节或空值; 空值仅匹配不存在的路径, 目录及重解析点不匹配, 读取失败抛错. #>
function Test-PbiShortcutSnapshot([string]$Path, $Bytes) {
    if ([IO.Directory]::Exists($Path)) { return $false }
    if ([IO.File]::Exists($Path)) {
        if ($null -eq $Bytes -or ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        return [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -ceq [Convert]::ToBase64String($Bytes)
    }
    return $null -eq $Bytes
}

<# .SYNOPSIS 按路径及可变快照复核入口字节, 未变返回 true; 已变返回 false 并保留用户入口、排除回滚范围, 读取失败抛错. #>
function Confirm-PbiInstallationShortcut([string]$Path, $Snapshot) {
    if (Test-PbiShortcutSnapshot $Path $Snapshot.Shortcuts[$Path]) { return $true }
    Write-Host ('快捷方式在检查后发生变化, 已保留并跳过: ' + $Path)
    $Snapshot.Shortcuts.Remove($Path)
    return $false
}

<# .SYNOPSIS 按快照选择创建或移除已拥有入口, 卸载始终检查两处; 参数为安装根、版本、动作和可变快照, 迟到冲突保留并排除恢复范围, I/O 失败抛错. #>
function Set-PbiInstallationShell([string]$Root, [string]$Version, [string]$Operation, $Snapshot) {
    # 安装和卸载只处理预检确认的入口, 不重新扩大范围; 晚到的用户更改也不纳入回滚.
    foreach ($path in @($Snapshot.Shortcuts.Keys)) { $null = Confirm-PbiInstallationShortcut $path $Snapshot }
    if ($Operation -ceq 'Uninstall') {
        foreach ($path in @($script:ProductKey, $script:UninstallKey)) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($path, $false) }
        foreach ($path in @($Snapshot.Shortcuts.Keys)) {
            if (!(Confirm-PbiInstallationShortcut $path $Snapshot)) { continue }
            if ($null -ne $Snapshot.Shortcuts[$path]) {
                $Snapshot.ShortcutChanges[$path] = $Snapshot.Shortcuts[$path]
                [IO.File]::Delete($path)
                $Snapshot.ShortcutChanges[$path] = $null
            }
        }
        Set-PbiExternalToolRegistration $Snapshot.ExternalTool $Version Uninstall $Snapshot.ExternalTool.NonInteractive
        return
    }
    $keys = @{
        $script:ProductKey = @{ InstallRoot = $Root; Version = $Version }
        $script:UninstallKey = @{ DisplayName = 'pbi-utils'; DisplayVersion = $Version; InstallLocation = $Root; DisplayIcon = (Join-Path $Root 'app\PbiUtils.Desktop.exe'); Publisher = 'jiaopengzi'; UninstallString = ('"' + (Join-Path $Root 'uninstall.exe') + '"'); URLInfoAbout = 'https://jiaopengzi.com' }
    }
    foreach ($path in $keys.Keys) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($path)
        try {
            foreach ($field in $keys[$path].Keys) {
                $key.SetValue($field, $keys[$path][$field], [Microsoft.Win32.RegistryValueKind]::String)
                if ($key.GetValue($field) -cne $keys[$path][$field]) { throw '安装登记写入后核验失败' }
            }
        }
        finally { $key.Dispose() }
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($path in @($Snapshot.Shortcuts.Keys)) {
            # 注册写入及 COM 初始化期间入口仍可能改变, 每项动作前再次核验, 不使用先前通过结果替代.
            if (!(Confirm-PbiInstallationShortcut $path $Snapshot)) { continue }
            if (!$Snapshot.ShortcutSelections[$path]) {
                if ($null -ne $Snapshot.Shortcuts[$path]) {
                    $Snapshot.ShortcutChanges[$path] = $Snapshot.Shortcuts[$path]
                    [IO.File]::Delete($path)
                    $Snapshot.ShortcutChanges[$path] = $null
                }
                continue
            }
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            $shortcut = $shell.CreateShortcut($path)
            try {
                $shortcut.TargetPath = Join-Path $Root 'app\PbiUtils.Desktop.exe'
                $shortcut.WorkingDirectory = Join-Path $Root 'app'
                $shortcut.Description = 'pbi-utils'
                $Snapshot.ShortcutChanges[$path] = $Snapshot.Shortcuts[$path]
                $shortcut.Save()
                $Snapshot.ShortcutChanges[$path] = [IO.File]::ReadAllBytes($path)
            } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        }
    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    # 外部工具登记需要机器共享目录权限, 放在用户 Shell 更新最后; 失败仍进入同一安装事务回滚.
    Set-PbiExternalToolRegistration $Snapshot.ExternalTool $Version Install $Snapshot.ExternalTool.NonInteractive
}

<# .SYNOPSIS 逐项恢复本轮登记及实际改动的快捷方式, 参数为快照; 再次变化的入口保留, 其余项继续恢复, 最后聚合错误抛出. #>
function Restore-PbiInstallationShell($Snapshot) {
    $failures = New-Object 'Collections.Generic.List[string]'
    try { Restore-PbiExternalToolRegistration $Snapshot.ExternalTool }
    catch { $failures.Add('恢复 Power BI 外部工具登记失败: ' + $_.Exception.Message) }
    foreach ($path in $Snapshot.Registry.Keys) {
        try {
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($path, $false)
            $saved = $Snapshot.Registry[$path]
            if ($null -ne $saved) {
                $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($path)
                try { foreach ($item in $saved.Values) { $key.SetValue($item.Name, $item.Value, $item.Kind) } }
                finally { $key.Dispose() }
            }
        } catch { $failures.Add('恢复安装登记失败: ' + $_.Exception.Message) }
    }
    foreach ($path in $Snapshot.ShortcutChanges.Keys) {
        try {
            if (!(Test-PbiShortcutSnapshot $path $Snapshot.ShortcutChanges[$path])) { throw '快捷方式在安装后发生变化, 未覆盖用户更改' }
            if ($null -eq $Snapshot.Shortcuts[$path]) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
            else { [IO.File]::WriteAllBytes($path, $Snapshot.Shortcuts[$path]) }
        } catch { $failures.Add('恢复快捷方式失败: ' + $_.Exception.Message) }
    }
    if ($failures.Count) { throw ($failures -join '; ') }
}

<# .SYNOPSIS 返回真实系统适配, 捕获入口接收安装根、已有状态及两个布尔创建选择; 测试可注入隔离适配, CLI 不公开适配选择. #>
function Get-PbiInstallationAdapters {
    return @{
        UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        AssertIdle = { param($root) Assert-PbiInstallationIdle $root }
        CaptureShell = { param($root, $installed, $desktop, $startMenu, $payloadRoot, $nonInteractive) Get-PbiInstallationShellSnapshot $root $installed $desktop $startMenu $payloadRoot $nonInteractive }
        ApplyShell = { param($root, $version, $operation, $snapshot) Set-PbiInstallationShell $root $version $operation $snapshot }
        RestoreShell = { param($snapshot) Restore-PbiInstallationShell $snapshot }
        DeleteOwnedFile = { param($path) [IO.File]::Delete($path) }
    }
}

Export-ModuleMember -Function Get-PbiInstallationAdapters
