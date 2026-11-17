# FilePath    : pbi-utils\scripts\tests\installer-application-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离目录和内存系统适配中验证安装升级、降级拒绝、租约与失败回滚, 不修改真实注册表或快捷方式.

<# .SYNOPSIS 默认验证完整安装行为; 测试套件可分别验证原生入口参数和安装事务. #>
param([ValidateSet('All', 'EntryOptions', 'Transactions')][string]$Scenario = 'All')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../installer/Installation.psm1') -Force
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-installer-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sandbox
$environment = @{ Shell = 'original'; Fail = $false; Writes = 0; Idle = $true; Leased = $false; DeleteCount = 0; DeleteFailAfter = 0; RegistrationNonInteractive = $false
    ExpectedDeletes = 0; NewPayloadFile = ''; EntryRoot = ''; EntryChecks = 0; CorruptSource = ''; DataRemovalCount = 0; ExpectedDataRemovalDeletes = 0; DataRemovalReady = $false; DataRemovalFail = $false }
$adapters = @{
    UserId = 'isolated-test-user'
    AssertIdle = { param($root) if (!$environment.Idle) { throw '请关闭正在运行的 pbi-utils' } }.GetNewClosure()
    CaptureShell = { param($root, $installed, $desktop, $startMenu, $payloadRoot, $nonInteractive) $environment.RegistrationNonInteractive = $nonInteractive; return $environment.Shell }.GetNewClosure()
    ApplyShell = { param($root, $version, $operation)
        if ($environment.ExpectedDeletes -gt 0 -and $environment.DeleteCount -ne $environment.ExpectedDeletes) { throw '旧版全部自有文件必须先移除再登记新版' }
        $environment.Writes++
        if ($operation -eq 'Install') {
            $stream = $null
            try { $stream = [IO.File]::OpenRead((Join-Path $root 'app/PbiUtils.Desktop.exe')) }
            catch [IO.IOException] { $environment.Leased = $true }
            finally { if ($null -ne $stream) { $stream.Dispose() } }
        }
        $environment.Shell = $operation + '/' + $version
        if ($environment.Fail) { throw 'injected shell failure' }
    }.GetNewClosure()
    RestoreShell = { param($snapshot) $environment.Shell = $snapshot }.GetNewClosure()
    DeleteOwnedFile = { param($path)
        $environment.DeleteCount++
        if ($environment.NewPayloadFile -and [IO.File]::Exists($environment.NewPayloadFile)) { throw '旧文件尚未清理完成时不得写入新版载荷' }
        if ($environment.EntryRoot) {
            foreach ($entryName in @('pbi-utils.exe', 'PbiUtils.Host.exe', 'PbiUtils.Desktop.exe')) {
                $entryStream = $null
                try {
                    $entryStream = [IO.File]::OpenRead((Join-Path $environment.EntryRoot $entryName))
                    throw '清理旧文件期间产品入口不得可读或启动'
                } catch [IO.IOException] { $environment.EntryChecks++ }
                finally { if ($null -ne $entryStream) { $entryStream.Dispose() } }
            }
        }
        if ($environment.DeleteFailAfter -gt 0 -and $environment.DeleteCount -eq $environment.DeleteFailAfter) { throw 'injected second deletion failure' }
        [IO.File]::Delete($path)
        if ($environment.CorruptSource -and $environment.DeleteCount -eq $environment.ExpectedDeletes) { [IO.File]::WriteAllText($environment.CorruptSource, 'injected payload corruption after old removal') }
    }.GetNewClosure()
    RemoveUserData = { param($executable)
        $environment.DataRemovalCount++
        $environment.DataRemovalReady = $environment.DeleteCount -eq $environment.ExpectedDataRemovalDeletes -and $environment.Shell -like 'Uninstall/*' -and
            [IO.File]::ReadAllText($executable) -ceq '1.0.0 pbi-utils exe'
        if ($environment.DataRemovalFail) { throw 'injected user data cleanup failure' }
    }.GetNewClosure()
}

<# .SYNOPSIS 断言行为结果, 参数为条件和说明, 不成立时抛错. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 用系统 PowerShell 5.1 执行真实维护入口副本, 参数为入口路径及固定测试选项, 返回退出码和输出; 仅使用隔离业务替身, 超时抛错. #>
function Invoke-EntryOptions([string]$Entry, [string]$Options) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $Entry + '" -Operation Install -InstallRoot "' + $sandbox + '" ' + $Options
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.EnvironmentVariables.Remove('PSModulePath')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync(); $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(20000)) { throw '隔离维护入口未及时结束' }
        return @{ ExitCode = $process.ExitCode; Output = $output.GetAwaiter().GetResult(); Error = $errors.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
<# .SYNOPSIS 创建带完整 hash 清单的隔离产品载荷, 参数为名称、数字版本、额外文件、部署模式、可选缺失许可路径及发布标签, 返回根目录, I/O 失败抛错. #>
function New-Payload([string]$Name, [string]$Version, [string]$Extra = 'old.dll', [string]$DeploymentMode = 'framework-dependent', [string]$OmitLicense = '', [string]$ReleaseTag = "v$Version") {
    $directory = Join-Path $sandbox $Name
    $null = New-Item -ItemType Directory -Path (Join-Path $directory 'app/web/branding'), (Join-Path $directory 'maintenance') -Force
    [IO.File]::WriteAllText((Join-Path $directory 'app/web/branding/logo.svg'), '<svg xmlns="http://www.w3.org/2000/svg"></svg>')
    foreach ($name in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
        foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) { [IO.File]::WriteAllText((Join-Path $directory "app/$name.$suffix"), "$Version $name $suffix") }
    }
    [IO.File]::WriteAllText((Join-Path $directory 'app/web/index.html'), $Version)
    $null = [IO.Directory]::CreateDirectory((Join-Path $directory 'app/licenses'))
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        if ($license -cne $OmitLicense) { [IO.File]::WriteAllText((Join-Path $directory "app/$license"), "$Version license fixture $license") }
    }
    $extraPath = Join-Path $directory "app/$Extra"
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($extraPath))
    [IO.File]::WriteAllText($extraPath, $Version)
    foreach ($name in @('Install-Application.ps1', 'Installation.psm1', 'InstallationPayload.psm1', 'InstallationShell.psm1', 'ExternalToolRegistration.psm1', 'ExternalToolDescriptor.psm1', 'ExternalToolFiles.psm1', 'ExternalTool-Worker.ps1')) { [IO.File]::WriteAllText((Join-Path $directory "maintenance/$name"), $Version) }
    [IO.File]::WriteAllText((Join-Path $directory 'uninstall.exe'), $Version)
    $files = @(Get-ChildItem -LiteralPath (Join-Path $directory 'app') -Recurse -File | ForEach-Object {
        @{ file = $_.FullName.Substring((Join-Path $directory 'app').Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    @{ schemaVersion = 3; layout = 'shared'; runtime = 'win-x64'; scope = 'All'; configuration = 'Release'; deploymentMode = $DeploymentMode; version = $Version; releaseTag = $ReleaseTag; files = $files } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'app/manifest.json') -Encoding UTF8
    return $directory
}
<# .SYNOPSIS 返回测试目录的相对路径及哈希快照, 参数为根目录, 枚举失败抛错. #>
function Snapshot([string]$Directory) {
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($Directory.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|')
}
<# .SYNOPSIS 确认动作拒绝并保留首因, 参数为操作和错误模式; 未抛错或原因不符时失败. #>
function Reject([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.Exception.Message -match $Pattern) "未按预期拒绝: $Pattern; $failure"
}

<# .SYNOPSIS 对系统 Windows PowerShell 原生 -File 调用验证安装参数的默认值、合法组合及拒绝行为; 断言失败时抛错. #>
function Test-EntryOptions {
    # 保留真实入口的 -File 参数绑定和转发, 只替换业务模块, 不访问任何产品注册表或快捷方式.
    $entryDirectory = Join-Path $sandbox 'entry-options'
    $null = [IO.Directory]::CreateDirectory($entryDirectory)
    $entry = Join-Path $entryDirectory 'Install-Application.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../installer/Install-Application.ps1') -Destination $entry
    @'
# FilePath    : isolated/Installation.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 隔离维护入口测试替身, 只返回真实调用参数, 不执行安装.
<# .SYNOPSIS 返回入口传递的布尔选择, 参数与生产安装事务一致, 不修改系统或文件. #>
function Invoke-PbiApplicationInstallation {
    param($Operation, $InstallRoot, $PayloadRoot, [bool]$CreateDesktopShortcut = $true, [bool]$CreateStartMenuShortcut = $true, [bool]$RegistrationNonInteractive = $false, [bool]$RemoveUserData = $false)
    return @{ desktop = $CreateDesktopShortcut; startMenu = $CreateStartMenuShortcut; registrationNonInteractive = $RegistrationNonInteractive; removeUserData = $RemoveUserData }
}
Export-ModuleMember -Function Invoke-PbiApplicationInstallation
'@ | Set-Content -LiteralPath (Join-Path $entryDirectory 'Installation.psm1') -Encoding UTF8
    $entryResult = Invoke-EntryOptions $entry ''
    Assert ($entryResult.ExitCode -eq 0 -and !$entryResult.Error) 'Windows PowerShell 5.1 维护入口的默认参数必须成功'
    $choices = $entryResult.Output | ConvertFrom-Json
    Assert ($choices.desktop -eq $true -and $choices.startMenu -eq $true) '维护入口省略选项时必须默认选择两处入口'
    Assert ($choices.removeUserData -eq $false) '维护入口省略数据清理选项时必须默认保留数据'
    foreach ($desktop in @(0, 1)) {
        foreach ($startMenu in @(0, 1)) {
            $entryResult = Invoke-EntryOptions $entry ('-CreateDesktopShortcut ' + $desktop + ' -CreateStartMenuShortcut ' + $startMenu)
            Assert ($entryResult.ExitCode -eq 0 -and !$entryResult.Error) 'Windows PowerShell 5.1 原生入口必须接受四种 0/1 选项组合'
            $choices = $entryResult.Output | ConvertFrom-Json
            Assert ($choices.desktop -is [bool] -and $choices.startMenu -is [bool] -and $choices.desktop -eq [bool]$desktop -and $choices.startMenu -eq [bool]$startMenu) '维护入口必须将两个独立选项准确转换为布尔参数'
        }
    }
    foreach ($nonInteractive in @(0, 1)) {
        $entryResult = Invoke-EntryOptions $entry ('-RegistrationNonInteractive ' + $nonInteractive)
        Assert ($entryResult.ExitCode -eq 0 -and !$entryResult.Error) '登记静默选项必须跨 Windows PowerShell 原生参数边界传递'
        $choices = $entryResult.Output | ConvertFrom-Json
        Assert ($choices.registrationNonInteractive -is [bool] -and $choices.registrationNonInteractive -eq [bool]$nonInteractive) '登记静默标志必须保持真实布尔值'
    }
    foreach ($optionValue in @(0, 1)) {
        $entryResult = Invoke-EntryOptions $entry ('-RemoveUserData ' + $optionValue)
        Assert ($entryResult.ExitCode -eq 0 -and !$entryResult.Error) '数据清理选项必须跨 Windows PowerShell 原生参数边界传递'
        $choices = $entryResult.Output | ConvertFrom-Json
        Assert ($choices.removeUserData -is [bool] -and $choices.removeUserData -eq [bool]$optionValue) '数据清理选项必须保持真实布尔值'
    }
    foreach ($option in @('CreateDesktopShortcut', 'CreateStartMenuShortcut', 'RegistrationNonInteractive', 'RemoveUserData')) {
        $entryResult = Invoke-EntryOptions $entry ('-' + $option + ' 2')
        Assert ($entryResult.ExitCode -ne 0 -and !$entryResult.Output -and $entryResult.Error) '非法创建选项必须在执行业务模块前被拒绝'
    }
    Write-Output 'PASS installer Windows PowerShell entry options'
}

try {
    # 原生入口总由同一个系统解释器执行, 无需随两个安装事务宿主重复.
    if ($Scenario -ne 'Transactions') { Test-EntryOptions }
    if ($Scenario -eq 'EntryOptions') { return }
    $bundled = New-Payload 'payload-bundled' '1.0.0' -DeploymentMode 'self-contained'
    $bundledRoot = Join-Path $sandbox 'installed-bundled'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $bundled -InstallRoot $bundledRoot -Adapters $adapters -RegistrationNonInteractive $true
    Assert ($environment.RegistrationNonInteractive -eq $true) '静默安装必须将无 UAC 约束传入权威 Shell 快照'
    Assert ((Get-Content -LiteralPath (Join-Path $bundledRoot 'app/manifest.json') -Raw | ConvertFrom-Json).deploymentMode -ceq 'self-contained') '包含 .NET 的安装包必须能够安装实际载荷'
    $null = Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $bundledRoot -Adapters $adapters -RegistrationNonInteractive $true
    Assert ($environment.RegistrationNonInteractive -eq $true) '静默卸载必须将无 UAC 约束传入权威 Shell 快照'
    Assert ($environment.DataRemovalCount -eq 0) '静默卸载不能调用用户数据清理'
    Assert (!(Test-Path -LiteralPath $bundledRoot)) '包含 .NET 的安装包必须按自有清单完整卸载'

    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $bundled -InstallRoot $bundledRoot -Adapters $adapters -RemoveUserData $true } '只能在交互式卸载'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $bundled -InstallRoot $bundledRoot -Adapters $adapters
    Reject { Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $bundledRoot -Adapters $adapters -RemoveUserData $true -RegistrationNonInteractive $true } '只能在交互式卸载'
    $bundledState = Get-Content -LiteralPath (Join-Path $bundledRoot 'maintenance/installation-state.json') -Raw | ConvertFrom-Json
    Assert ($bundledState.version -ceq 'v1.0.0') '正式安装身份必须完整记录 v 前缀'
    $before = Snapshot $bundledRoot
    $shellBefore = $environment.Shell
    $environment.ExpectedDataRemovalDeletes = @($bundledState.files).Count + 1
    $environment.DeleteCount = 0
    $environment.DeleteFailAfter = 2
    Reject { Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $bundledRoot -Adapters $adapters -RemoveUserData $true } 'injected second deletion failure'
    Assert ($environment.DataRemovalCount -eq 0) '程序文件清理失败时不得调用不可逆的数据删除'
    $environment.DeleteFailAfter = 0
    $environment.DeleteCount = 0
    $environment.DataRemovalFail = $true
    Reject { Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $bundledRoot -Adapters $adapters -RemoveUserData $true } 'injected user data cleanup failure'
    Assert ((Snapshot $bundledRoot) -ceq $before -and $environment.Shell -ceq $shellBefore) '数据清理失败必须完整恢复安装文件和系统适配'
    $environment.DataRemovalFail = $false
    $environment.DeleteCount = 0
    $null = Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $bundledRoot -Adapters $adapters -RemoveUserData $true
    Assert ($environment.DataRemovalCount -eq 2 -and $environment.DataRemovalReady -and !(Test-Path -LiteralPath $bundledRoot)) '明确同意后只能在程序文件和入口完成清理时调用已核验的备份入口'
    $invalidMode = New-Payload 'payload-invalid-mode' '1.0.0' -DeploymentMode 'unknown'
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $invalidMode -InstallRoot $bundledRoot -Adapters $adapters } '发布清单'
    Assert (!(Test-Path -LiteralPath $bundledRoot)) '未知部署模式必须在修改文件前拒绝'
    $badRelease = New-Payload 'payload-invalid-release' '1.0.0'
    $badReleaseManifest = Join-Path $badRelease 'app/manifest.json'
    $writesBefore = $environment.Writes
    foreach ($tag in @('v1.0.1-rc3', 'v1.0.0-rc0', 'v1.0.0-rc2147483648', $null)) {
        $badManifest = Get-Content -LiteralPath $badReleaseManifest -Raw | ConvertFrom-Json
        if ($null -eq $tag) { $badManifest.PSObject.Properties.Remove('releaseTag') } else { $badManifest.releaseTag = $tag }
        $badManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $badReleaseManifest -Encoding UTF8
        Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $badRelease -InstallRoot $bundledRoot -Adapters $adapters } '发布标签无效'
        Assert (!(Test-Path -LiteralPath $bundledRoot) -and $environment.Writes -eq $writesBefore) '非法或缺失的发布标签须在安装文件和系统入口写入前拒绝'
    }
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        $missing = New-Payload ('payload-missing-' + $license.Replace('/', '-')) '1.0.0' -OmitLicense $license
        $writesBefore = $environment.Writes
        Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $missing -InstallRoot $bundledRoot -Adapters $adapters } ('必需文件缺失: app/' + [Regex]::Escape($license))
        Assert (!(Test-Path -LiteralPath $bundledRoot) -and $environment.Writes -eq $writesBefore) '许可未列入有效文件清单时须在安装文件和系统入口写入前拒绝'
    }
    $first = New-Payload 'payload-one' '1.0.0'
    $next = New-Payload 'payload-two' '1.1.0' 'new.dll' -ReleaseTag 'v1.1.0-rc3'
    $destination = Join-Path $sandbox 'installed'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $first -InstallRoot $destination -Adapters $adapters
    Assert $environment.Leased '注册与快捷方式更新前, 产品入口必须始终被禁止打开'
    $statePath = Join-Path $destination 'maintenance/installation-state.json'
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert ($state.productId -ceq 'pbi-utils-desktop' -and $state.version -ceq 'v1.0.0') '必须记录当前安装身份及完整正式版标签'
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        Assert ([IO.File]::ReadAllText((Join-Path $destination "app/$license")) -ceq "1.0.0 license fixture $license") '安装后许可原文必须可从应用目录读取'
        Assert (@($state.files | Where-Object { $_.file -ceq "app/$license" }).Count -eq 1) '安装身份必须记录许可文件以覆盖修复、回滚和卸载'
    }
    [IO.File]::WriteAllText((Join-Path $destination 'personal.txt'), 'preserve unknown user file')
    # 模拟已经分发的安装身份, 后续升级应读取原数字版本并写入新包的完整标签.
    $state.version = '1.0.0'
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding UTF8
    $environment.DeleteCount = 0; $environment.ExpectedDeletes = @($state.files).Count + 1
    $environment.NewPayloadFile = Join-Path $destination 'app/new.dll'
    $environment.EntryRoot = Join-Path $destination 'app'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $next -InstallRoot $destination -Adapters $adapters
    Assert ($environment.DeleteCount -eq $environment.ExpectedDeletes) '升级必须在写新版前移除所有旧自有文件, 包括同名文件和安装状态'
    Assert ($environment.EntryChecks -eq 3 * $environment.ExpectedDeletes) '整个旧文件移除阶段的三个产品入口必须锁定或不存在'
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert ($state.version -ceq 'v1.1.0-rc3') '升级后安装身份必须精确记录预发布标签及轮次'
    $environment.ExpectedDeletes = 0; $environment.NewPayloadFile = ''; $environment.EntryRoot = ''
    [IO.File]::WriteAllText((Join-Path $destination 'app/PbiUtils.Host.dll'), 'damaged own product file')
    [IO.File]::Delete((Join-Path $destination 'app/new.dll'))
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $next -InstallRoot $destination -Adapters $adapters
    Assert ((Get-Content -LiteralPath (Join-Path $destination 'app/PbiUtils.Host.dll') -Raw) -eq '1.1.0 PbiUtils.Host dll' -and (Test-Path -LiteralPath (Join-Path $destination 'app/new.dll'))) '同版修复必须恢复已损坏或缺失的自有程序文件'
    Assert ((Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).version -ceq 'v1.1.0-rc3') '同版修复后必须保留完整预发布标签'
    Assert (!(Test-Path -LiteralPath (Join-Path $destination 'app/old.dll')) -and (Test-Path -LiteralPath (Join-Path $destination 'app/new.dll'))) '升级只删除旧版自有过期文件'
    Assert ((Get-Content -LiteralPath (Join-Path $destination 'personal.txt') -Raw) -eq 'preserve unknown user file') '升级应保留未知文件'
    $before = Snapshot $destination
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $first -InstallRoot $destination -Adapters $adapters } '降级'
    Assert ((Snapshot $destination) -ceq $before) '降级拒绝不得修改文件'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $next -InstallRoot $destination -Adapters $adapters

    $third = New-Payload 'payload-three' '1.2.0' 'third.dll'
    $before = Snapshot $destination
    $shellBefore = $environment.Shell
    $writesBefore = $environment.Writes
    $typeConflict = New-Payload 'payload-file-directory-conflict' '1.2.0' 'new.dll/child.dll'
    $deletesBefore = $environment.DeleteCount
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $typeConflict -InstallRoot $destination -Adapters $adapters } '目录与现有文件冲突'
    Assert ((Snapshot $destination) -ceq $before -and $environment.DeleteCount -eq $deletesBefore) '旧文件变目录的类型冲突必须在移除旧包之前拒绝'
    $environment.DeleteCount = 0; $environment.DeleteFailAfter = 2
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $third -InstallRoot $destination -Adapters $adapters } 'injected second deletion failure'
    $environment.DeleteFailAfter = 0
    Assert ((Snapshot $destination) -ceq $before -and $environment.Writes -eq $writesBefore) '升级部分移除旧文件后失败必须完整恢复旧包, 不得写入系统登记'

    $currentState = Get-Content -LiteralPath (Join-Path $destination 'maintenance/installation-state.json') -Raw | ConvertFrom-Json
    $environment.DeleteCount = 0; $environment.ExpectedDeletes = @($currentState.files).Count + 1
    $environment.CorruptSource = Join-Path $third 'app/third.dll'
    $sourceBytes = [IO.File]::ReadAllBytes($environment.CorruptSource)
    try { Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $third -InstallRoot $destination -Adapters $adapters } '安装写入后哈希不一致' }
    finally {
        [IO.File]::WriteAllBytes($environment.CorruptSource, $sourceBytes)
        $environment.CorruptSource = ''; $environment.ExpectedDeletes = 0
    }
    Assert ((Snapshot $destination) -ceq $before -and $environment.Writes -eq $writesBefore -and $environment.Shell -ceq $shellBefore) '旧包全清后新版写入失败必须恢复旧版及状态并移除本轮新增文件'

    $environment.Fail = $true
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $third -InstallRoot $destination -Adapters $adapters } 'injected shell failure'
    $environment.Fail = $false
    Assert ((Snapshot $destination) -ceq $before -and $environment.Shell -ceq $shellBefore) '失败必须恢复程序、维护状态与系统适配'

    $lease = [IO.File]::Open((Join-Path $destination 'app/PbiUtils.Host.dll'), 'Open', 'Read', 'Read')
    try { Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $third -InstallRoot $destination -Adapters $adapters } '占用|关闭|锁定' }
    finally { $lease.Dispose() }
    Assert ((Snapshot $destination) -ceq $before) '文件占用拒绝必须保留原产物'
    $environment.Idle = $false
    Reject { Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $destination -Adapters $adapters } '关闭'
    $environment.Idle = $true

    $unknown = Join-Path $sandbox 'unknown'
    $null = New-Item -ItemType Directory -Path $unknown
    [IO.File]::WriteAllText((Join-Path $unknown 'keep.txt'), 'unknown')
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $first -InstallRoot $unknown -Adapters $adapters } '陌生|身份'
    Assert ((Get-Content -LiteralPath (Join-Path $unknown 'keep.txt') -Raw) -eq 'unknown') '不能清空陌生目录'

    $bad = New-Payload 'payload-traversal' '2.0.0'
    $manifestPath = Join-Path $bad 'app/manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.files[0].file = '../outside.dll'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $bad -InstallRoot $destination -Adapters $adapters } '路径'

    [IO.File]::WriteAllText((Join-Path $destination 'app/third.dll'), 'unknown file must survive')
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $third -InstallRoot $destination -Adapters $adapters } '未知文件冲突'
    [IO.File]::Delete((Join-Path $destination 'app/third.dll'))
    $stateBytes = [IO.File]::ReadAllBytes($statePath)
    $foreign = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $foreign.userId = 'another-user'
    $foreign | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding UTF8
    try { Reject { Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $destination -Adapters $adapters } '安装身份无效' }
    finally { [IO.File]::WriteAllBytes($statePath, $stateBytes) }

    $linked = New-Payload 'payload-link' '2.0.0'
    $link = Join-Path $linked 'maintenance/link'
    $null = New-Item -ItemType Junction -Path $link -Target $unknown
    try { Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $linked -InstallRoot $destination -Adapters $adapters } '链接|重解析' }
    finally { [IO.Directory]::Delete($link) }

    $danglingTarget = Join-Path $sandbox 'dangling-target'
    $danglingRoot = Join-Path $sandbox 'dangling-root'
    $null = [IO.Directory]::CreateDirectory($danglingTarget)
    $null = New-Item -ItemType Junction -Path $danglingRoot -Target $danglingTarget
    [IO.Directory]::Delete($danglingTarget)
    try { Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $first -InstallRoot $danglingRoot -Adapters $adapters } '链接|重解析' }
    finally { [IO.Directory]::Delete($danglingRoot) }

    $mutexName = Get-PbiInstallationMutexName $destination $adapters.UserId
    $ready = Join-Path $sandbox 'mutex-ready'
    $release = Join-Path $sandbox 'mutex-release'
    $childCode = '$m=New-Object Threading.Mutex($false,''{0}''); $null=$m.WaitOne(); [IO.File]::WriteAllText(''{1}'',''ready''); $s=[Diagnostics.Stopwatch]::StartNew(); while(![IO.File]::Exists(''{2}'') -and $s.Elapsed.TotalSeconds -lt 20){{Start-Sleep -Milliseconds 50}}; $m.ReleaseMutex(); $m.Dispose()' -f $mutexName.Replace("'", "''"), $ready.Replace("'", "''"), $release.Replace("'", "''")
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCode))
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -ArgumentList @('-NoProfile', '-EncodedCommand', $encoded) -WindowStyle Hidden -PassThru
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $ready) -and !$child.HasExited -and $watch.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 50 }
        Assert (Test-Path -LiteralPath $ready) '隔离互斥锁进程必须启动'
        Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $third -InstallRoot $destination -Adapters $adapters } '另一安装|稍后重试'
    } finally {
        [IO.File]::WriteAllText($release, 'release')
        if (!$child.WaitForExit(5000)) { throw '隔离互斥锁进程未按时排空' }
        $child.Dispose()
    }

    $environment.Fail = $true
    Reject { Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $destination -Adapters $adapters } 'injected shell failure'
    $environment.Fail = $false
    Assert ((Snapshot $destination) -ceq $before) '卸载失败必须保留完整旧安装'
    $shellBefore = $environment.Shell
    $environment.DeleteCount = 0; $environment.DeleteFailAfter = 2
    Reject { Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $destination -Adapters $adapters } 'injected second deletion failure'
    $environment.DeleteFailAfter = 0
    Assert ((Snapshot $destination) -ceq $before -and $environment.Shell -ceq $shellBefore) '已删除部分旧文件后的失败必须重建文件并恢复系统适配'
    $null = Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $destination -Adapters $adapters
    Assert (@(Get-ChildItem -LiteralPath $destination -Recurse -File).Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $destination 'personal.txt'))) '卸载只移除自有文件并保留未知文件'
    Assert ($environment.DataRemovalCount -eq 2) '默认交互式卸载不能调用用户数据清理'
    Write-Output 'PASS installer application transactions, upgrades, rollback, ownership and leases'
}
finally {
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-installer-[0-9a-f]{32}$') { throw '安装测试清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
