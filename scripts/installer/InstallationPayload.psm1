# FilePath    : pbi-utils\scripts\installer\InstallationPayload.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 以 Windows PowerShell 5.1 验证安装目录、发布清单和当前用户安装身份, 不修改产品文件.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<# .SYNOPSIS 核验本机绝对目录及其父链无链接, 参数为路径, 返回规范路径; 越界或类型错误时抛错. #>
function Resolve-PbiInstallPath([string]$Path) {
    if (!$Path -or ![IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\')) { throw '安装路径必须为本机绝对目录' }
    $absolute = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($absolute -eq [IO.Path]::GetPathRoot($absolute).TrimEnd('\')) { throw '安装路径不能是磁盘根目录' }
    $current = $absolute
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '安装路径包含链接或重解析点' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $absolute
}

<# .SYNOPSIS 安全枚举目录内文件, 参数为根目录, 返回文件对象; 链接或枚举失败直接抛错. #>
function Get-PbiInstallFiles([string]$Root) {
    $null = Resolve-PbiInstallPath $Root
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '安装目录包含链接或重解析点' }
        if ($entry.PSIsContainer) { Get-PbiInstallFiles $entry.FullName } else { $entry }
    }
}

<# .SYNOPSIS 将清单路径约束到根目录, 参数为根和正斜杠相对路径, 返回绝对路径; 非法名称或穿越抛错. #>
function Join-PbiOwnedInstallPath([string]$Root, [string]$Relative) {
    if (!$Relative -or $Relative.StartsWith('/') -or $Relative -match '[<>:"\\|?*\x00-\x1f]') { throw '安装清单路径无效' }
    foreach ($part in $Relative.Split('/')) {
        if (!$part -or $part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw '安装清单路径无效' }
    }
    $target = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$target.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装清单路径越界' }
    $null = Resolve-PbiInstallPath $target
    return $target
}

<# .SYNOPSIS 返回文件 SHA256, 参数为路径, 读取失败抛错. #>
function Get-PbiInstallHash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

<# .SYNOPSIS 读取并验证两种 .NET 部署模式的 Release All 载荷、许可声明及维护文件, 参数为解包根目录, 返回版本和精确相对文件映射; 清单、许可或文件无效抛错. #>
function Read-PbiInstallPayload([string]$PayloadRoot) {
    $root = Resolve-PbiInstallPath $PayloadRoot
    $actualFiles = @(Get-PbiInstallFiles $root)
    $app = Join-Path $root 'app'
    $manifest = Get-Content -LiteralPath (Join-Path $app 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $version = $null
    if ($manifest.schemaVersion -ne 3 -or $manifest.layout -cne 'shared' -or $manifest.runtime -cne 'win-x64' -or $manifest.scope -cne 'All' -or
        $manifest.configuration -cne 'Release' -or $manifest.deploymentMode -cnotin @('framework-dependent', 'self-contained') -or ![Version]::TryParse($manifest.version, [ref]$version)) { throw '安装载荷必须为有效 Release All framework-dependent 或 self-contained 发布清单' }
    $expected = @{}
    foreach ($item in $manifest.files) {
        $path = Join-PbiOwnedInstallPath $app $item.file
        if ($item.file -ieq 'manifest.json' -or $expected.ContainsKey($item.file) -or $item.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw '安装清单路径或哈希无效或重复' }
        if (![IO.File]::Exists($path) -or (Get-PbiInstallHash $path) -cne $item.sha256.ToUpperInvariant()) { throw "安装载荷文件缺失或哈希错误: $($item.file)" }
        $expected[$item.file] = $item.sha256.ToUpperInvariant()
    }
    $files = @{}
    foreach ($file in $actualFiles) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $null = Join-PbiOwnedInstallPath $root $relative
        if ($relative.StartsWith('app/', [StringComparison]::Ordinal)) {
            $appRelative = $relative.Substring(4)
            if ($appRelative -cne 'manifest.json' -and !$expected.ContainsKey($appRelative)) { throw '安装载荷文件集合与清单不一致' }
        } elseif (!$relative.StartsWith('maintenance/', [StringComparison]::Ordinal) -and $relative -cne 'uninstall.exe') { throw '安装载荷包含范围外文件' }
        if ($relative -ieq 'maintenance/installation-state.json') { throw '安装载荷不得携带既有安装身份' }
        $files[$relative] = Get-PbiInstallHash $file.FullName
    }
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
        foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
            if (!$files.ContainsKey("app/$entry.$suffix")) { throw '安装载荷产品入口缺失' }
        }
    }
    foreach ($required in @('app/web/index.html', 'app/web/branding/logo.svg', 'app/LICENSE', 'app/THIRD-PARTY-NOTICES.md', 'app/THIRD-PARTY-NOTICES.txt', 'app/licenses/manifest.json', 'maintenance/Install-Application.ps1', 'maintenance/Installation.psm1', 'maintenance/InstallationPayload.psm1', 'maintenance/InstallationShell.psm1', 'maintenance/ExternalToolRegistration.psm1', 'maintenance/ExternalToolDescriptor.psm1', 'maintenance/ExternalToolFiles.psm1', 'maintenance/ExternalTool-Worker.ps1', 'uninstall.exe')) {
        if (!$files.ContainsKey($required)) { throw "安装载荷必需文件缺失: $required" }
    }
    return @{ Root = $root; Version = $version.ToString(); Files = $files }
}

<# .SYNOPSIS 读取当前用户且同目录的安装状态, 参数为目录、用户身份和可选同版修复版本; 返回现存自有文件真实哈希, 陌生身份或非修复修改抛错. #>
function Read-PbiInstallationState([string]$InstallRoot, [string]$UserId, [string]$RepairVersion) {
    $statePath = Join-Path $InstallRoot 'maintenance/installation-state.json'
    if (![IO.File]::Exists($statePath)) {
        if ([IO.Directory]::Exists($InstallRoot) -and @(Get-ChildItem -LiteralPath $InstallRoot -Force).Count) { throw '目标为非空陌生目录, 缺少本产品安装身份' }
        return $null
    }
    $null = @(Get-PbiInstallFiles $InstallRoot)
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $version = $null
    if ($state.schemaVersion -ne 1 -or $state.productId -cne 'pbi-utils-desktop' -or $state.userId -cne $UserId -or
        ![string]::Equals($state.installRoot, $InstallRoot, [StringComparison]::OrdinalIgnoreCase) -or ![Version]::TryParse($state.version, [ref]$version)) { throw '当前目录的产品安装身份无效' }
    $files = @{}; $declared = @{}
    $repair = $RepairVersion -and [Version]$RepairVersion -eq $version
    foreach ($item in $state.files) {
        $path = Join-PbiOwnedInstallPath $InstallRoot $item.file
        if ((!$item.file.StartsWith('app/', [StringComparison]::Ordinal) -and !$item.file.StartsWith('maintenance/', [StringComparison]::Ordinal) -and $item.file -cne 'uninstall.exe') -or
            $item.file -ieq 'maintenance/installation-state.json' -or $declared.ContainsKey($item.file) -or $item.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw '安装身份文件清单无效' }
        $declared[$item.file] = $true
        if ([IO.Directory]::Exists($path)) { throw '已安装文件路径被目录替代, 无法安全修复' }
        $actualHash = if ([IO.File]::Exists($path)) { Get-PbiInstallHash $path } else { $null }
        if (!$repair -and $actualHash -cne $item.sha256.ToUpperInvariant()) { throw "已安装文件已改变或缺失, 请使用同版本安装包修复: $($item.file)" }
        if ($actualHash) { $files[$item.file] = $actualHash }
    }
    foreach ($required in @('app/pbi-utils.exe', 'app/PbiUtils.Host.exe', 'app/PbiUtils.Desktop.exe', 'app/manifest.json', 'maintenance/Install-Application.ps1', 'maintenance/Installation.psm1', 'uninstall.exe')) {
        if (!$declared.ContainsKey($required)) { throw '安装身份必需文件缺失' }
    }
    $files['maintenance/installation-state.json'] = Get-PbiInstallHash $statePath
    return @{ Version = $version.ToString(); Files = $files }
}

Export-ModuleMember -Function Resolve-PbiInstallPath, Get-PbiInstallFiles, Join-PbiOwnedInstallPath, Get-PbiInstallHash, Read-PbiInstallPayload, Read-PbiInstallationState
