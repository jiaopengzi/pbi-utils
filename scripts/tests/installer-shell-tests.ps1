# FilePath    : pbi-utils\scripts\tests\installer-shell-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 使用隔离 HKCU 注册键和真实快捷方式验证安装归属、冲突保留及回滚, 不修改用户产品入口.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../installer/Installation.psm1') -Force
$shellModule = Get-Module -All InstallationShell | Select-Object -First 1
$identifier = [Guid]::NewGuid().ToString('N')
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-installer-shell-' + $identifier)
$registryRoot = 'Software\pbi-utils-tests\' + $identifier
$null = [IO.Directory]::CreateDirectory($sandbox)

<# .SYNOPSIS 断言真实行为结果, 参数为条件和说明, 不成立时抛错. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }

<# .SYNOPSIS 为场景设置唯一注册键及按 Desktop/StartMenu 命名的快捷方式目录, 返回路径集合; 仅改变测试进程中的模块环境. #>
function New-Scenario([string]$Name) {
    $directory = Join-Path $sandbox $Name
    $paths = @{ StartMenu = (Join-Path $directory 'start/pbi-utils.lnk'); Desktop = (Join-Path $directory 'desktop/pbi-utils.lnk') }
    foreach ($path in $paths.Values) { $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) }
    $root = Join-Path $directory 'installed'
    $productKey = $registryRoot + '\' + $Name + '\Product'
    $uninstallKey = $registryRoot + '\' + $Name + '\Uninstall'
    & $shellModule {
        param($product, $uninstall, $shortcuts)
        $script:ProductKey = $product
        $script:UninstallKey = $uninstall
        $script:TestShortcutPaths = $shortcuts
        function script:Get-PbiShortcutPaths { return $script:TestShortcutPaths }
        <# .SYNOPSIS 为现有 Shell 场景提供固定隔离 SVG 字节, 不读取不存在的真实安装载荷. #>
        function script:Read-PbiInstallationExternalToolIcon([string]$Root, [string]$PayloadRoot) { return [Text.Encoding]::UTF8.GetBytes('<svg xmlns="http://www.w3.org/2000/svg"></svg>') }
    } $productKey $uninstallKey $paths
    foreach ($descriptorModule in @(Get-Module -All ExternalToolDescriptor)) {
        & $descriptorModule {
            param($path)
            $script:TestExternalTools = $path
            <# .SYNOPSIS 只把本测试中的固定登记解析到场景临时目录, 不读取或写入真实 Power BI 共享目录. #>
            function script:Get-PbiExternalToolDirectory { return $script:TestExternalTools }
        } (Join-Path $directory 'external-tools')
    }
    return @{ Directory = $directory; Root = $root; Start = $paths.StartMenu; Desktop = $paths.Desktop; ProductKey = $productKey; UninstallKey = $uninstallKey }
}

<# .SYNOPSIS 用真实 Windows Shell 创建测试快捷方式, 参数为路径和目标, COM 错误直接传播. #>
function New-Shortcut([string]$Path, [string]$Target) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        try { $shortcut.TargetPath = $Target; $shortcut.Description = 'isolated installer test'; $shortcut.Save() }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

<# .SYNOPSIS 读取真实快捷方式目标, 参数为路径, 不修改快捷方式, 无法读取时抛错. #>
function Read-ShortcutTarget([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        try { return [string]$shortcut.TargetPath }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

<# .SYNOPSIS 从隔离注册键读取值, 参数为键路径及字段名, 缺少键返回 null, 不写注册表. #>
function Read-RegistryValue([string]$Path, [string]$Name) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if ($null -eq $key) { return $null }
    try { return $key.GetValue($Name) } finally { $key.Dispose() }
}

<# .SYNOPSIS 创建可由真实安装事务读取的最小隔离载荷, 参数为场景目录, 返回载荷路径; 文件失败直接抛错. #>
function New-Payload([string]$Directory) {
    $payload = Join-Path $Directory 'payload'
    $app = Join-Path $payload 'app'
    $null = [IO.Directory]::CreateDirectory((Join-Path $app 'web/branding'))
    [IO.File]::WriteAllText((Join-Path $app 'web/branding/logo.svg'), '<svg xmlns="http://www.w3.org/2000/svg"></svg>')
    $null = [IO.Directory]::CreateDirectory((Join-Path $payload 'maintenance'))
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
        foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) { [IO.File]::WriteAllText((Join-Path $app "$entry.$suffix"), 'isolated payload') }
    }
    [IO.File]::WriteAllText((Join-Path $app 'web/index.html'), 'isolated page')
    $null = [IO.Directory]::CreateDirectory((Join-Path $app 'licenses'))
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        [IO.File]::WriteAllText((Join-Path $app $license), "isolated license $license")
    }
    foreach ($name in @('Install-Application.ps1', 'Installation.psm1', 'InstallationPayload.psm1', 'InstallationShell.psm1', 'ExternalToolRegistration.psm1', 'ExternalToolDescriptor.psm1', 'ExternalToolFiles.psm1', 'ExternalTool-Worker.ps1')) { [IO.File]::WriteAllText((Join-Path $payload "maintenance/$name"), 'isolated maintenance') }
    [IO.File]::WriteAllText((Join-Path $payload 'uninstall.exe'), 'isolated uninstaller')
    $files = @(Get-ChildItem -LiteralPath $app -Recurse -File | ForEach-Object {
        @{ file = $_.FullName.Substring($app.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    @{ schemaVersion = 3; layout = 'shared'; runtime = 'win-x64'; scope = 'All'; configuration = 'Release'; deploymentMode = 'framework-dependent'; version = '1.0.0'; files = $files } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $app 'manifest.json') -Encoding UTF8
    return $payload
}

try {
    $quietScenario = New-Scenario 'quiet-registration'
    $quietSnapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $false $false $false '' $true } $quietScenario.Root
    Assert ($quietSnapshot.ExternalTool.NonInteractive -eq $true) '真实 Shell 快照必须保持外部工具登记的无 UAC 约束'
    # 选项经真实安装事务进入隔离 Shell, 同时验证首次安装、修复切换、回滚及缺失入口卸载.
    foreach ($desktop in @($false, $true)) {
        foreach ($startMenu in @($false, $true)) {
            $scenario = New-Scenario ('options-' + [int]$desktop + '-' + [int]$startMenu)
            $payload = New-Payload $scenario.Directory
            $adapters = & $shellModule { Get-PbiInstallationAdapters }
            $adapters.AssertIdle = { param($root) }
            $null = Invoke-PbiApplicationInstallation -Operation Install -InstallRoot $scenario.Root -PayloadRoot $payload -Adapters $adapters -CreateDesktopShortcut $desktop -CreateStartMenuShortcut $startMenu
            Assert ([IO.File]::Exists($scenario.Desktop) -eq $desktop -and [IO.File]::Exists($scenario.Start) -eq $startMenu) '首次安装必须准确应用桌面和开始菜单的四种选择组合'
            $before = @{}
            foreach ($path in @($scenario.Start, $scenario.Desktop)) { $before[$path] = if ([IO.File]::Exists($path)) { (Get-FileHash -LiteralPath $path).Hash } else { $null } }
            $applyShell = $adapters.ApplyShell
            $adapters.ApplyShell = {
                param($root, $version, $operation, $saved)
                & $applyShell $root $version $operation $saved
                throw 'injected option repair failure'
            }.GetNewClosure()
            $failure = $null
            try { $null = Invoke-PbiApplicationInstallation -Operation Install -InstallRoot $scenario.Root -PayloadRoot $payload -Adapters $adapters -CreateDesktopShortcut (!$desktop) -CreateStartMenuShortcut (!$startMenu) } catch { $failure = $_ }
            Assert ($null -ne $failure -and $failure.Exception.Message -match 'injected option repair failure') '修复选项后的故障必须触发真实事务回滚'
            foreach ($path in $before.Keys) {
                if ($null -eq $before[$path]) { Assert (![IO.File]::Exists($path)) '失败修复必须移除本轮新建入口' }
                else { Assert ((Get-FileHash -LiteralPath $path).Hash -ceq $before[$path]) '失败修复必须恢复被取消入口的原始字节' }
            }
            $adapters.ApplyShell = $applyShell
            $null = Invoke-PbiApplicationInstallation -Operation Install -InstallRoot $scenario.Root -PayloadRoot $payload -Adapters $adapters -CreateDesktopShortcut (!$desktop) -CreateStartMenuShortcut (!$startMenu)
            Assert ([IO.File]::Exists($scenario.Desktop) -eq (!$desktop) -and [IO.File]::Exists($scenario.Start) -eq (!$startMenu)) '同版修复必须创建新选择入口并移除取消选择的自有入口'
            if ($startMenu -and [IO.File]::Exists($scenario.Desktop)) { [IO.File]::Delete($scenario.Desktop) }
            $null = Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $scenario.Root -Adapters $adapters -CreateDesktopShortcut $false -CreateStartMenuShortcut $false
            Assert (![IO.File]::Exists($scenario.Desktop) -and ![IO.File]::Exists($scenario.Start)) '卸载必须忽略创建选项并容忍已缺失入口'
            Assert (![IO.Directory]::Exists($scenario.Root)) '选项组合卸载必须完成程序清理'
        }
    }

    $scenario = New-Scenario 'deselected-foreign'
    New-Shortcut $scenario.Start (Join-Path $sandbox 'foreign-start.exe')
    New-Shortcut $scenario.Desktop (Join-Path $sandbox 'foreign-desktop.exe')
    $foreignStart = (Get-FileHash -LiteralPath $scenario.Start).Hash
    $foreignDesktop = (Get-FileHash -LiteralPath $scenario.Desktop).Hash
    $payload = New-Payload $scenario.Directory
    $adapters = & $shellModule { Get-PbiInstallationAdapters }
    $adapters.AssertIdle = { param($root) }
    $null = Invoke-PbiApplicationInstallation -Operation Install -InstallRoot $scenario.Root -PayloadRoot $payload -Adapters $adapters -CreateDesktopShortcut $false -CreateStartMenuShortcut $false
    $null = Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $scenario.Root -Adapters $adapters
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignStart -and (Get-FileHash -LiteralPath $scenario.Desktop).Hash -ceq $foreignDesktop) '取消勾选及随后卸载必须保留两处外部同名入口'

    foreach ($operation in @('Install', 'Uninstall')) {
        $scenario = New-Scenario ('deselected-late-change-' + $operation)
        foreach ($path in @($scenario.Start, $scenario.Desktop)) { New-Shortcut $path (Join-Path $scenario.Root 'app/PbiUtils.Desktop.exe') }
        $desktopHash = (Get-FileHash -LiteralPath $scenario.Desktop).Hash
        $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $true $false $false } $scenario.Root
        New-Shortcut $scenario.Start (Join-Path $sandbox 'reassigned-after-option-snapshot.exe')
        $foreignHash = (Get-FileHash -LiteralPath $scenario.Start).Hash
        & $shellModule { param($root, $saved, $action) Set-PbiInstallationShell $root '1.0.0' $action $saved } $scenario.Root $snapshot $operation
        Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash -and ![IO.File]::Exists($scenario.Desktop)) '取消勾选和卸载只可移除快照未变的自有入口, 必须保留迟到改动'
        & $shellModule { param($saved) Restore-PbiInstallationShell $saved } $snapshot
        Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash -and (Get-FileHash -LiteralPath $scenario.Desktop).Hash -ceq $desktopHash) '取消勾选和卸载回滚必须恢复本轮删除并保留用户迟到改动'
    }

    foreach ($operation in @('Install', 'Uninstall')) {
        $scenario = New-Scenario ('changed-after-recheck-' + $operation)
        New-Shortcut $scenario.Desktop (Join-Path $scenario.Root 'app/PbiUtils.Desktop.exe')
        $foreignPath = Join-Path $scenario.Directory 'foreign.lnk'
        New-Shortcut $foreignPath (Join-Path $sandbox 'changed-during-shell-update.exe')
        $foreignBytes = [IO.File]::ReadAllBytes($foreignPath)
        $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $true $false $false } $scenario.Root
        $originalCheck = & $shellModule { return ${function:Test-PbiShortcutSnapshot} }
        & $shellModule {
            param($original, $target, $replacement)
            $script:OriginalShortcutCheck = $original; $script:RaceTarget = $target; $script:RaceBytes = $replacement; $script:RaceInjected = $false
            <# .SYNOPSIS 包装真实字节检查, 参数为路径和原字节; 首次匹配后注入迟到改动并返回原布尔结果, I/O 失败抛错. #>
            function script:Test-PbiShortcutSnapshot([string]$Path, $Bytes) {
                $matches = & $script:OriginalShortcutCheck $Path $Bytes
                if ($matches -and !$script:RaceInjected -and $Path -ceq $script:RaceTarget) {
                    [IO.File]::WriteAllBytes($Path, $script:RaceBytes); $script:RaceInjected = $true
                }
                return $matches
            }
        } $originalCheck $scenario.Desktop $foreignBytes
        try { & $shellModule { param($root, $saved, $action) Set-PbiInstallationShell $root '1.0.0' $action $saved } $scenario.Root $snapshot $operation }
        finally { & $shellModule { param($original) Set-Item -LiteralPath Function:script:Test-PbiShortcutSnapshot -Value $original } $originalCheck }
        Assert ([IO.File]::Exists($scenario.Desktop)) '统一复核后被改写的入口不得由取消勾选或卸载删除'
        Assert ((Get-FileHash -LiteralPath $scenario.Desktop).Hash -ceq (Get-FileHash -LiteralPath $foreignPath).Hash) '实际修改前发现迟到入口必须保留其原始字节'
        & $shellModule { param($saved) Restore-PbiInstallationShell $saved } $snapshot
        Assert ((Get-FileHash -LiteralPath $scenario.Desktop).Hash -ceq (Get-FileHash -LiteralPath $foreignPath).Hash) '实际修改前的迟到入口必须从回滚范围排除'
    }

    $scenario = New-Scenario 'foreign-start'
    New-Shortcut $scenario.Start (Join-Path $sandbox 'other-app.exe')
    $foreignHash = (Get-FileHash -LiteralPath $scenario.Start).Hash
    $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $false } $scenario.Root
    & $shellModule { param($root, $saved) Set-PbiInstallationShell $root '1.0.0' 'Install' $saved } $scenario.Root $snapshot
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '安装必须保留外部快捷方式原始字节'
    Assert ((Read-ShortcutTarget $scenario.Desktop) -ieq (Join-Path $scenario.Root 'app/PbiUtils.Desktop.exe')) '不冲突的桌面入口必须创建'
    Assert ((Read-RegistryValue $scenario.ProductKey 'InstallRoot') -ieq $scenario.Root) '快捷方式冲突不得阻止产品安装登记'
    Assert ((Read-RegistryValue $scenario.UninstallKey 'DisplayVersion') -eq '1.0.0') '快捷方式冲突不得阻止卸载登记'

    & $shellModule { param($saved) Restore-PbiInstallationShell $saved } $snapshot
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '失败恢复必须保留快照外的外部快捷方式'
    Assert (![IO.File]::Exists($scenario.Desktop)) '首次安装回滚应移除本轮创建的桌面入口'
    Assert ($null -eq (Read-RegistryValue $scenario.ProductKey 'InstallRoot')) '首次安装回滚应移除本轮安装登记'

    $scenario = New-Scenario 'owned-upgrade'
    $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $false } $scenario.Root
    & $shellModule { param($root, $saved) Set-PbiInstallationShell $root '1.0.0' 'Install' $saved } $scenario.Root $snapshot
    $ownedHash = (Get-FileHash -LiteralPath $scenario.Desktop).Hash
    $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $true } $scenario.Root
    & $shellModule { param($root, $saved) Set-PbiInstallationShell $root '1.1.0' 'Install' $saved } $scenario.Root $snapshot
    Assert ((Read-RegistryValue $scenario.ProductKey 'Version') -eq '1.1.0') '自有安装升级必须更新版本登记'
    Assert ((Read-ShortcutTarget $scenario.Start) -ieq (Join-Path $scenario.Root 'app/PbiUtils.Desktop.exe')) '升级必须保持自有开始菜单入口'
    & $shellModule { param($saved) Restore-PbiInstallationShell $saved } $snapshot
    Assert ((Read-RegistryValue $scenario.ProductKey 'Version') -eq '1.0.0' -and (Get-FileHash -LiteralPath $scenario.Desktop).Hash -ceq $ownedHash) '升级失败恢复必须还原旧版本登记和快捷方式原始字节'

    New-Shortcut $scenario.Start (Join-Path $sandbox 'user-reassigned.exe')
    $foreignHash = (Get-FileHash -LiteralPath $scenario.Start).Hash
    $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $true } $scenario.Root
    & $shellModule { param($root, $saved) Set-PbiInstallationShell $root '1.0.0' 'Uninstall' $saved } $scenario.Root $snapshot
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '卸载必须保留用户改指向的同名入口'
    Assert (![IO.File]::Exists($scenario.Desktop)) '卸载必须删除仍属于本安装的入口'
    Assert ($null -eq (Read-RegistryValue $scenario.ProductKey 'InstallRoot') -and $null -eq (Read-RegistryValue $scenario.UninstallKey 'DisplayVersion')) '快捷方式冲突不得阻止移除自有安装登记'

    $scenario = New-Scenario 'late-conflict'
    $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $false } $scenario.Root
    New-Shortcut $scenario.Start (Join-Path $sandbox 'arrived-after-snapshot.exe')
    $foreignHash = (Get-FileHash -LiteralPath $scenario.Start).Hash
    & $shellModule { param($root, $saved) Set-PbiInstallationShell $root '1.0.0' 'Install' $saved } $scenario.Root $snapshot
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '预检后出现的外部入口不得被安装覆盖'
    & $shellModule { param($saved) Restore-PbiInstallationShell $saved } $snapshot
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '预检后出现的外部入口不得被回滚删除'
    Assert (![IO.File]::Exists($scenario.Desktop)) '晚到冲突仍须允许不冲突入口创建并回滚'

    $scenario = New-Scenario 'directory-conflict'
    $null = [IO.Directory]::CreateDirectory($scenario.Start)
    $keep = Join-Path $scenario.Start 'keep.txt'
    [IO.File]::WriteAllText($keep, 'preserve directory')
    $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $false } $scenario.Root
    & $shellModule { param($root, $saved) Set-PbiInstallationShell $root '1.0.0' 'Install' $saved } $scenario.Root $snapshot
    Assert ([IO.File]::ReadAllText($keep) -ceq 'preserve directory') '快捷方式同名目录及内容必须保留'
    Assert ([IO.File]::Exists($scenario.Desktop)) '同名目录不得阻止其他入口创建'
    & $shellModule { param($saved) Restore-PbiInstallationShell $saved } $snapshot
    Assert ([IO.File]::ReadAllText($keep) -ceq 'preserve directory') '回滚不得更改同名目录及内容'

    $scenario = New-Scenario 'changed-after-apply'
    $snapshot = & $shellModule { param($root) Get-PbiInstallationShellSnapshot $root $false } $scenario.Root
    & $shellModule { param($root, $saved) Set-PbiInstallationShell $root '1.0.0' 'Install' $saved } $scenario.Root $snapshot
    # 固定冲突入口先恢复, 验证失败不阻止后续无冲突入口恢复.
    $snapshot.ShortcutChanges = [ordered]@{
        $scenario.Start = $snapshot.ShortcutChanges[$scenario.Start]
        $scenario.Desktop = $snapshot.ShortcutChanges[$scenario.Desktop]
    }
    New-Shortcut $scenario.Start (Join-Path $sandbox 'user-changed-after-apply.exe')
    $foreignHash = (Get-FileHash -LiteralPath $scenario.Start).Hash
    $failure = $null
    try { & $shellModule { param($saved) Restore-PbiInstallationShell $saved } $snapshot } catch { $failure = $_ }
    Assert ($null -ne $failure) '快捷方式在安装后被改写时, 回滚必须明确失败'
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '回滚失败必须保留用户在安装后改写的快捷方式'
    Assert (![IO.File]::Exists($scenario.Desktop)) '一个入口冲突不得阻止恢复其他未被修改的自有入口'

    # 真实文件事务串联真实 Shell 适配, 捕获未向 ApplyShell 传递快照或卸载重新枚举入口的回归.
    $scenario = New-Scenario 'transaction'
    New-Shortcut $scenario.Start (Join-Path $sandbox 'existing-app.exe')
    $foreignHash = (Get-FileHash -LiteralPath $scenario.Start).Hash
    $payload = New-Payload $scenario.Directory
    $adapters = & $shellModule { Get-PbiInstallationAdapters }
    $adapters.AssertIdle = { param($root) }
    $applyShell = $adapters.ApplyShell
    $adapters.ApplyShell = {
        param($root, $version, $operation, $saved)
        & $applyShell $root $version $operation $saved
        throw 'injected failure after real shell changes'
    }.GetNewClosure()
    $failure = $null
    try { $null = Invoke-PbiApplicationInstallation -Operation Install -InstallRoot $scenario.Root -PayloadRoot $payload -Adapters $adapters } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.Exception.Message -match 'injected failure after real shell changes') '真实 Shell 写入后失败必须传播原始事务错误'
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '真实事务失败回滚必须保留外部快捷方式'
    Assert (![IO.Directory]::Exists($scenario.Root) -and ![IO.File]::Exists($scenario.Desktop)) '真实事务失败回滚必须移除本轮程序和入口'
    Assert ($null -eq (Read-RegistryValue $scenario.ProductKey 'InstallRoot')) '真实事务失败回滚必须移除本轮注册登记'
    $adapters.ApplyShell = $applyShell
    $null = Invoke-PbiApplicationInstallation -Operation Install -InstallRoot $scenario.Root -PayloadRoot $payload -Adapters $adapters
    Assert ([IO.File]::Exists((Join-Path $scenario.Root 'app/PbiUtils.Desktop.exe'))) '快捷方式冲突不得阻止真实文件事务交付程序'
    Assert ([IO.File]::Exists((Join-Path $scenario.Root 'maintenance/installation-state.json'))) '文件事务必须记录安装身份'
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        Assert ([IO.File]::ReadAllText((Join-Path $scenario.Root "app/$license")) -ceq "isolated license $license") '真实 Shell 事务完成后仍须保留完整许可文件供安装目录访问'
    }
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash -and [IO.File]::Exists($scenario.Desktop)) '文件事务必须保留外部入口并创建自有入口'
    $null = Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $scenario.Root -Adapters $adapters
    Assert ((Get-FileHash -LiteralPath $scenario.Start).Hash -ceq $foreignHash) '真实卸载事务必须保留外部入口'
    Assert (![IO.Directory]::Exists($scenario.Root) -and ![IO.File]::Exists($scenario.Desktop)) '真实卸载事务必须清理自有程序和快捷方式'
    Write-Output 'PASS installer shell conflicts, ownership and rollback'
}
finally {
    if ($registryRoot -notmatch '^Software\\pbi-utils-tests\\[0-9a-f]{32}$') { throw '测试注册键清理越界' }
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registryRoot, $false)
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-installer-shell-[0-9a-f]{32}$') { throw '快捷方式测试清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
