# FilePath    : pbi-utils\scripts\tests\installer-application-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离目录和内存系统适配中验证安装升级、降级拒绝、租约与失败回滚, 不修改真实注册表或快捷方式.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../installer/Installation.psm1') -Force
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-installer-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sandbox
$environment = @{ Shell = 'original'; Fail = $false; Writes = 0; Idle = $true; Leased = $false; DeleteCount = 0; DeleteFailAfter = 0; RegistrationNonInteractive = $false }
$adapters = @{
    UserId = 'isolated-test-user'
    AssertIdle = { param($root) if (!$environment.Idle) { throw '请关闭正在运行的 pbi-utils' } }.GetNewClosure()
    CaptureShell = { param($root, $installed, $desktop, $startMenu, $payloadRoot, $nonInteractive) $environment.RegistrationNonInteractive = $nonInteractive; return $environment.Shell }.GetNewClosure()
    ApplyShell = { param($root, $version, $operation)
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
        if ($environment.DeleteFailAfter -gt 0 -and $environment.DeleteCount -eq $environment.DeleteFailAfter) { throw 'injected second deletion failure' }
        [IO.File]::Delete($path)
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
<# .SYNOPSIS 创建带完整 hash 清单的隔离产品载荷, 参数为名称、版本、额外文件、部署模式及可选缺失许可路径, 返回根目录, I/O 失败抛错. #>
function New-Payload([string]$Name, [string]$Version, [string]$Extra = 'old.dll', [string]$DeploymentMode = 'framework-dependent', [string]$OmitLicense = '') {
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
    [IO.File]::WriteAllText((Join-Path $directory "app/$Extra"), $Version)
    foreach ($name in @('Install-Application.ps1', 'Installation.psm1', 'InstallationPayload.psm1', 'InstallationShell.psm1', 'ExternalToolRegistration.psm1', 'ExternalToolDescriptor.psm1', 'ExternalToolFiles.psm1', 'ExternalTool-Worker.ps1')) { [IO.File]::WriteAllText((Join-Path $directory "maintenance/$name"), $Version) }
    [IO.File]::WriteAllText((Join-Path $directory 'uninstall.exe'), $Version)
    $files = @(Get-ChildItem -LiteralPath (Join-Path $directory 'app') -Recurse -File | ForEach-Object {
        @{ file = $_.FullName.Substring((Join-Path $directory 'app').Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    @{ schemaVersion = 3; layout = 'shared'; runtime = 'win-x64'; scope = 'All'; configuration = 'Release'; deploymentMode = $DeploymentMode; version = $Version; files = $files } |
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

try {
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
<# .SYNOPSIS 返回入口传递的两个布尔选择, 参数与生产安装事务一致, 不修改系统或文件. #>
function Invoke-PbiApplicationInstallation {
    param($Operation, $InstallRoot, $PayloadRoot, [bool]$CreateDesktopShortcut = $true, [bool]$CreateStartMenuShortcut = $true, [bool]$RegistrationNonInteractive = $false)
    return @{ desktop = $CreateDesktopShortcut; startMenu = $CreateStartMenuShortcut; registrationNonInteractive = $RegistrationNonInteractive }
}
Export-ModuleMember -Function Invoke-PbiApplicationInstallation
'@ | Set-Content -LiteralPath (Join-Path $entryDirectory 'Installation.psm1') -Encoding UTF8
    $entryResult = Invoke-EntryOptions $entry ''
    Assert ($entryResult.ExitCode -eq 0 -and !$entryResult.Error) 'Windows PowerShell 5.1 维护入口的默认参数必须成功'
    $choices = $entryResult.Output | ConvertFrom-Json
    Assert ($choices.desktop -eq $true -and $choices.startMenu -eq $true) '维护入口省略选项时必须默认选择两处入口'
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
    foreach ($option in @('CreateDesktopShortcut', 'CreateStartMenuShortcut', 'RegistrationNonInteractive')) {
        $entryResult = Invoke-EntryOptions $entry ('-' + $option + ' 2')
        Assert ($entryResult.ExitCode -ne 0 -and !$entryResult.Output -and $entryResult.Error) '非法创建选项必须在执行业务模块前被拒绝'
    }

    $bundled = New-Payload 'payload-bundled' '1.0.0' -DeploymentMode 'self-contained'
    $bundledRoot = Join-Path $sandbox 'installed-bundled'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $bundled -InstallRoot $bundledRoot -Adapters $adapters -RegistrationNonInteractive $true
    Assert ($environment.RegistrationNonInteractive -eq $true) '静默安装必须将无 UAC 约束传入权威 Shell 快照'
    Assert ((Get-Content -LiteralPath (Join-Path $bundledRoot 'app/manifest.json') -Raw | ConvertFrom-Json).deploymentMode -ceq 'self-contained') '包含 .NET 的安装包必须能够安装实际载荷'
    $null = Invoke-PbiApplicationInstallation -Operation Uninstall -InstallRoot $bundledRoot -Adapters $adapters -RegistrationNonInteractive $true
    Assert ($environment.RegistrationNonInteractive -eq $true) '静默卸载必须将无 UAC 约束传入权威 Shell 快照'
    Assert (!(Test-Path -LiteralPath $bundledRoot)) '包含 .NET 的安装包必须按自有清单完整卸载'
    $invalidMode = New-Payload 'payload-invalid-mode' '1.0.0' -DeploymentMode 'unknown'
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $invalidMode -InstallRoot $bundledRoot -Adapters $adapters } '发布清单'
    Assert (!(Test-Path -LiteralPath $bundledRoot)) '未知部署模式必须在修改文件前拒绝'
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        $missing = New-Payload ('payload-missing-' + $license.Replace('/', '-')) '1.0.0' -OmitLicense $license
        $writesBefore = $environment.Writes
        Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $missing -InstallRoot $bundledRoot -Adapters $adapters } ('必需文件缺失: app/' + [Regex]::Escape($license))
        Assert (!(Test-Path -LiteralPath $bundledRoot) -and $environment.Writes -eq $writesBefore) '许可未列入有效文件清单时须在安装文件和系统入口写入前拒绝'
    }
    $first = New-Payload 'payload-one' '1.0.0'
    $next = New-Payload 'payload-two' '1.1.0' 'new.dll'
    $destination = Join-Path $sandbox 'installed'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $first -InstallRoot $destination -Adapters $adapters
    Assert $environment.Leased '注册与快捷方式更新前, 产品入口必须始终被禁止打开'
    $state = Get-Content -LiteralPath (Join-Path $destination 'maintenance/installation-state.json') -Raw | ConvertFrom-Json
    Assert ($state.productId -eq 'pbi-utils-desktop' -and $state.version -eq '1.0.0') '必须记录当前安装身份及版本'
    foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
        Assert ([IO.File]::ReadAllText((Join-Path $destination "app/$license")) -ceq "1.0.0 license fixture $license") '安装后许可原文必须可从应用目录读取'
        Assert (@($state.files | Where-Object { $_.file -ceq "app/$license" }).Count -eq 1) '安装身份必须记录许可文件以覆盖修复、回滚和卸载'
    }
    [IO.File]::WriteAllText((Join-Path $destination 'personal.txt'), 'preserve unknown user file')
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $next -InstallRoot $destination -Adapters $adapters
    [IO.File]::WriteAllText((Join-Path $destination 'app/PbiUtils.Host.dll'), 'damaged own product file')
    [IO.File]::Delete((Join-Path $destination 'app/new.dll'))
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $next -InstallRoot $destination -Adapters $adapters
    Assert ((Get-Content -LiteralPath (Join-Path $destination 'app/PbiUtils.Host.dll') -Raw) -eq '1.1.0 PbiUtils.Host dll' -and (Test-Path -LiteralPath (Join-Path $destination 'app/new.dll'))) '同版修复必须恢复已损坏或缺失的自有程序文件'
    Assert (!(Test-Path -LiteralPath (Join-Path $destination 'app/old.dll')) -and (Test-Path -LiteralPath (Join-Path $destination 'app/new.dll'))) '升级只删除旧版自有过期文件'
    Assert ((Get-Content -LiteralPath (Join-Path $destination 'personal.txt') -Raw) -eq 'preserve unknown user file') '升级应保留未知文件'
    $before = Snapshot $destination
    Reject { Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $first -InstallRoot $destination -Adapters $adapters } '降级'
    Assert ((Snapshot $destination) -ceq $before) '降级拒绝不得修改文件'
    $null = Invoke-PbiApplicationInstallation -Operation Install -PayloadRoot $next -InstallRoot $destination -Adapters $adapters

    $third = New-Payload 'payload-three' '1.2.0' 'third.dll'
    $before = Snapshot $destination
    $shellBefore = $environment.Shell
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
    $statePath = Join-Path $destination 'maintenance/installation-state.json'
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
    Write-Output 'PASS installer application transactions, upgrades, rollback, ownership and leases'
}
finally {
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-installer-[0-9a-f]{32}$') { throw '安装测试清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
