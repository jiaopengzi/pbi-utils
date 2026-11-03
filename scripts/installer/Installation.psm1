# FilePath    : pbi-utils\scripts\installer\Installation.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : Windows PowerShell 5.1 用户级安装文件事务、同目录互斥、入口租约及失败恢复, 不处理用户业务数据.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'InstallationPayload.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'InstallationShell.psm1') -Force

<# .SYNOPSIS 返回同用户同安装目录的互斥锁名称, 参数为规范目录和用户标识, 不创建锁. #>
function Get-PbiInstallationMutexName([string]$Root, [string]$UserId) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($UserId + '|' + $Root.ToUpperInvariant()))).Replace('-', '') }
    finally { $algorithm.Dispose() }
    return 'Local\pbi-utils-install-' + $hash
}

<# .SYNOPSIS 判断是否为必须最后释放的产品入口, 参数为清单相对路径, 返回布尔值. #>
function Test-PbiInstallEntry([string]$Path) { return $Path -iin @('app/pbi-utils.exe', 'app/PbiUtils.Host.exe', 'app/PbiUtils.Desktop.exe') }

<# .SYNOPSIS 通过独占租约计算内容哈希并复位流位置, 参数为文件流, 返回 SHA256 字符串. #>
function Get-PbiLeaseHash([IO.FileStream]$Stream) {
    $Stream.Position = 0
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $Stream.Position = 0 }
}

<# .SYNOPSIS 通过持有的独占流原位替换内容, 参数为目标流和来源文件, 保持启动阻止租约; I/O 失败抛错. #>
function Write-PbiLeasedFile([IO.FileStream]$Stream, [string]$Source) {
    $inputStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $Stream.Position = 0; $inputStream.CopyTo($Stream); $Stream.SetLength($inputStream.Length); $Stream.Flush($true); $Stream.Position = 0 }
    finally { $inputStream.Dispose() }
}

<# .SYNOPSIS 仅删除自有文件曾使用且现为空的目录, 参数为根目录及自有相对路径, 未知内容一律保留. #>
function Remove-PbiEmptyInstallationDirectories([string]$Root, [string[]]$Files) {
    $directories = @{}
    foreach ($relative in $Files) {
        $parent = [IO.Path]::GetDirectoryName((Join-PbiOwnedInstallPath $Root $relative))
        while ($parent -and $parent.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { $directories[$parent] = $true; $parent = [IO.Path]::GetDirectoryName($parent) }
    }
    $directories[$Root] = $true
    foreach ($directory in @($directories.Keys | Sort-Object Length -Descending)) {
        if ([IO.Directory]::Exists($directory)) {
            $null = Resolve-PbiInstallPath $directory
            if (@(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop).Count -eq 0) { [IO.Directory]::Delete($directory, $false) }
        }
    }
}

<#
.SYNOPSIS 安装、同版修复、升级或卸载当前用户产品, 普通失败恢复本轮程序和系统适配.
.PARAMETER Operation Install 或 Uninstall.
.PARAMETER InstallRoot 只属于本产品且路径稳定的安装根目录.
.PARAMETER PayloadRoot Install 时由 NSIS 解包的载荷根, Uninstall 不使用.
.PARAMETER CreateDesktopShortcut Install 时创建桌面入口, 默认 true; false 只移除本安装拥有的入口, Uninstall 忽略此选择.
.PARAMETER CreateStartMenuShortcut Install 时创建开始菜单入口, 默认 true; Uninstall 始终核验并清理两处自有入口.
.PARAMETER RegistrationNonInteractive true 时外部工具登记不得申请 UAC, 权限不足返回前置错误.
.PARAMETER Adapters 测试可注入身份、进程和系统适配; ApplyShell 接收捕获快照限定修改范围, 正式 CLI 不暴露此参数.
.OUTPUTS 动作和版本; 并发、占用、身份错误、降级或事务失败时抛错. 不承诺断电原子性或用户数据回滚.
#>
function Invoke-PbiApplicationInstallation {
    [CmdletBinding()]
    param([ValidateSet('Install', 'Uninstall')][string]$Operation, [string]$InstallRoot, [string]$PayloadRoot, [hashtable]$Adapters,
        [bool]$CreateDesktopShortcut = $true, [bool]$CreateStartMenuShortcut = $true, [bool]$RegistrationNonInteractive = $false)
    if ($null -eq $Adapters) { $Adapters = Get-PbiInstallationAdapters }
    foreach ($field in @('UserId', 'AssertIdle', 'CaptureShell', 'ApplyShell', 'RestoreShell', 'DeleteOwnedFile')) { if (!$Adapters.ContainsKey($field)) { throw '安装系统适配缺失' } }
    $root = Resolve-PbiInstallPath $InstallRoot
    foreach ($special in @('UserProfile', 'DesktopDirectory', 'MyDocuments', 'LocalApplicationData', 'ApplicationData', 'ProgramFiles', 'ProgramFilesX86', 'Windows', 'System')) {
        $protected = [Environment]::GetFolderPath($special)
        if ($protected -and [string]::Equals($root, $protected.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw '安装路径不能使用用户或系统根目录' }
    }
    foreach ($dataFolder in @('.pbi-utils', '.pbi-utils-runtime')) {
        $dataRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) $dataFolder
        if ($root -ieq $dataRoot -or $root.StartsWith($dataRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装路径不能位于产品用户数据或协调目录' }
    }
    $mutex = New-Object Threading.Mutex($false, (Get-PbiInstallationMutexName $root $Adapters.UserId))
    $acquired = $false; $leases = @{}; $backups = @{}; $newFiles = @{}; $deleted = @{}
    $backupRoot = $null; $keepBackup = $false; $mutationStarted = $false; $shellStarted = $false; $shellSnapshot = $null; $firstFailure = $null
    $owned = @{}; $desired = @{}; $allFiles = @{}
    try {
        try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (!$acquired) { throw '同目录的另一安装或卸载正在执行, 请稍后重试' }
        $payload = $null
        if ($Operation -ceq 'Install') { $payload = Read-PbiInstallPayload $PayloadRoot }
        $repairVersion = if ($null -ne $payload) { $payload.Version } else { $null }
        $state = Read-PbiInstallationState $root $Adapters.UserId $repairVersion
        if ($null -ne $state) { $owned = $state.Files }
        if ($Operation -ceq 'Uninstall' -and $null -eq $state) { throw '缺少当前产品安装身份, 拒绝卸载陌生目录' }
        if ($Operation -ceq 'Install') {
            if ($payload.Root.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($payload.Root + '\', [StringComparison]::OrdinalIgnoreCase) -or $root -ieq $payload.Root) { throw '安装载荷与目标路径不能重叠' }
            if ($null -ne $state -and [Version]$payload.Version -lt [Version]$state.Version) { throw '拒绝降级安装, 当前版本高于安装包版本' }
            $desired = $payload.Files
        }
        foreach ($relative in @($owned.Keys) + @($desired.Keys) + @('maintenance/installation-state.json')) {
            $path = Join-PbiOwnedInstallPath $root $relative
            if (!$owned.ContainsKey($relative) -and ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path))) { throw "新版本路径与未知文件冲突, 未覆盖: $relative" }
            $allFiles[$relative] = $path
        }
        & $Adapters.AssertIdle $root
        $sourceRoot = if ($null -ne $payload) { $payload.Root } else { '' }
        $shellSnapshot = & $Adapters.CaptureShell $root ($null -ne $state) $CreateDesktopShortcut $CreateStartMenuShortcut $sourceRoot $RegistrationNonInteractive
        # 入口先锁, 其他自有文件随后锁. 即使进程在首次检查后启动, 写租约也会明确失败.
        foreach ($relative in @($owned.Keys | Sort-Object @{ Expression = { if (Test-PbiInstallEntry $_) { 0 } else { 1 } } }, { $_ })) {
            $path = $allFiles[$relative]
            try { $leases[$relative] = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Delete) }
            catch { throw "安装文件被占用或无法锁定, 请关闭本软件后重试: $relative" }
            if ((Get-PbiLeaseHash $leases[$relative]) -cne $owned[$relative]) { throw "安装文件在预检后发生改变: $relative" }
        }
        & $Adapters.AssertIdle $root
        $backupRoot = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-install-transaction-' + [Guid]::NewGuid().ToString('N'))
        $null = [IO.Directory]::CreateDirectory($backupRoot)
        foreach ($relative in $owned.Keys) {
            $backup = Join-PbiOwnedInstallPath $backupRoot $relative
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))
            $output = [IO.File]::Open($backup, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $leases[$relative].Position = 0; $leases[$relative].CopyTo($output); $output.Flush($true); $leases[$relative].Position = 0 }
            finally { $output.Dispose() }
            $backups[$relative] = $backup
        }
        $mutationStarted = $true
        if ($Operation -ceq 'Install') {
            $stateRelative = 'maintenance/installation-state.json'
            foreach ($relative in @(@($desired.Keys) + @($stateRelative) | Sort-Object @{ Expression = { if (Test-PbiInstallEntry $_) { 0 } else { 1 } } }, { $_ })) {
                if (!$leases.ContainsKey($relative)) {
                    $path = $allFiles[$relative]
                    $null = Resolve-PbiInstallPath $path
                    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
                    $leases[$relative] = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Delete)
                    $newFiles[$relative] = $true
                }
            }
            foreach ($relative in $desired.Keys) {
                Write-PbiLeasedFile $leases[$relative] (Join-PbiOwnedInstallPath $payload.Root $relative)
                if ((Get-PbiLeaseHash $leases[$relative]) -cne $desired[$relative]) { throw "安装写入后哈希不一致: $relative" }
            }
            $newState = @{ schemaVersion = 1; productId = 'pbi-utils-desktop'; userId = $Adapters.UserId; installRoot = $root; version = $payload.Version; files = @($desired.Keys | Sort-Object | ForEach-Object { @{ file = $_; sha256 = $desired[$_] } }) }
            $stateSource = Join-Path $backupRoot 'new-state.json'
            [IO.File]::WriteAllText($stateSource, ($newState | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($true)))
            Write-PbiLeasedFile $leases[$stateRelative] $stateSource
            $version = $payload.Version
        } else { $version = $state.Version }
        $shellStarted = $true
        & $Adapters.ApplyShell $root $version $Operation $shellSnapshot
        # 仅删除有本次可靠备份的过期自有文件. 已删除文件的租约也保留到事务结束.
        foreach ($relative in $owned.Keys) {
            if ($Operation -ceq 'Install' -and ($desired.ContainsKey($relative) -or $relative -ceq 'maintenance/installation-state.json')) { continue }
            $null = Resolve-PbiInstallPath $allFiles[$relative]
            & $Adapters.DeleteOwnedFile $allFiles[$relative]
            $deleted[$relative] = $true
        }
        return @{ operation = $Operation; version = $version; installRoot = $root }
    } catch {
        $primaryException = $_.Exception
        $primary = $_.Exception.Message
        $firstFailure = $primary
        $recoveryErrors = New-Object 'Collections.Generic.List[string]'
        if ($mutationStarted) {
            foreach ($relative in $owned.Keys) {
                try {
                    if ($deleted.ContainsKey($relative)) {
                        $leases[$relative].Dispose(); $leases.Remove($relative)
                        $leases[$relative] = [IO.File]::Open($allFiles[$relative], [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Delete)
                    }
                    Write-PbiLeasedFile $leases[$relative] $backups[$relative]
                    if ((Get-PbiLeaseHash $leases[$relative]) -cne $owned[$relative]) { throw '恢复内容核验失败' }
                } catch { $recoveryErrors.Add("恢复文件失败 $relative : $($_.Exception.Message)") }
            }
            foreach ($relative in $newFiles.Keys) {
                try { [IO.File]::Delete($allFiles[$relative]) } catch { $recoveryErrors.Add("移除本轮文件失败 $relative : $($_.Exception.Message)") }
            }
            if ($shellStarted) { try { & $Adapters.RestoreShell $shellSnapshot } catch { $recoveryErrors.Add("恢复安装登记失败: $($_.Exception.Message)") } }
        }
        if ($recoveryErrors.Count) { $keepBackup = $true; throw ($primary + '; 恢复未完成, 备份保留于 ' + $backupRoot + '; ' + ($recoveryErrors -join '; ')) }
        throw $primaryException
    } finally {
        $cleanupErrors = New-Object 'Collections.Generic.List[string]'
        # 非入口先释放, 三个入口最后释放, 避免半更新的产品被启动.
        foreach ($relative in @($leases.Keys | Sort-Object @{ Expression = { if (Test-PbiInstallEntry $_) { 1 } else { 0 } } }, { $_ })) {
            try { $leases[$relative].Dispose() } catch { $cleanupErrors.Add("释放文件租约失败 $relative : $($_.Exception.Message)") }
        }
        try {
            if ($mutationStarted) { Remove-PbiEmptyInstallationDirectories $root @($allFiles.Keys) }
            if ($backupRoot -and !$keepBackup) {
                $absoluteBackup = Resolve-PbiInstallPath $backupRoot
                if ([IO.Path]::GetDirectoryName($absoluteBackup) -ine [IO.Path]::GetTempPath().TrimEnd('\') -or [IO.Path]::GetFileName($absoluteBackup) -notmatch '^pbi-utils-install-transaction-[0-9a-f]{32}$') { throw '安装临时备份清理路径越界' }
                $null = @(Get-PbiInstallFiles $absoluteBackup)
                Remove-Item -LiteralPath $absoluteBackup -Recurse -Force -ErrorAction Stop
            }
        } catch {
            $cleanupErrors.Add('安装临时材料清理未完成: ' + $_.Exception.Message)
        } finally {
            try { if ($acquired) { $mutex.ReleaseMutex() } } catch { $cleanupErrors.Add('释放安装互斥锁失败: ' + $_.Exception.Message) }
            $mutex.Dispose()
        }
        if ($cleanupErrors.Count) {
            $cleanupError = $cleanupErrors -join '; '
            if ($null -ne $firstFailure) { [Console]::Error.WriteLine($cleanupError) }
            else { throw ('程序安装事务已完成, 但' + $cleanupError) }
        }
    }
}

Export-ModuleMember -Function Invoke-PbiApplicationInstallation, Get-PbiInstallationMutexName
