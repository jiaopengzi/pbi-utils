# FilePath    : pbi-utils\scripts\tests\installer-external-tools-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 双 PowerShell 下验证外部工具描述、真实隔离文件事务、归属、提权边界和回滚, 不修改系统登记.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$registration = Import-Module (Join-Path $PSScriptRoot '../installer/ExternalToolRegistration.psm1') -Force -PassThru
$files = Get-Module -All ExternalToolFiles | Select-Object -First 1
$descriptor = Get-Module -All ExternalToolDescriptor | Select-Object -First 1
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-external-tools-test-' + [Guid]::NewGuid().ToString('N'))
$directory = Join-Path $sandbox 'external-tools'
$null = [IO.Directory]::CreateDirectory($directory)
foreach ($module in @(Get-Module -All ExternalToolDescriptor)) {
    & $module {
        param($path)
        $script:TestDirectory = $path
        <# .SYNOPSIS 仅在本测试进程中把固定两种文件名解析到临时目录, 不修改生产 API 的路径边界. #>
        function script:Get-PbiExternalToolDirectory { return $script:TestDirectory }
    } $directory
}
$icon = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../../web/public/branding/logo.svg'))
$installedTarget = Join-Path $sandbox 'installed/app/PbiUtils.Desktop.exe'
$installedPath = Join-Path $directory 'pbi-utils.pbitool.json'
$devPath = Join-Path $directory 'pbi-utils-dev.pbitool.json'

<# .SYNOPSIS 断言实际行为结果, 参数为条件和失败说明, 不成立时抛错. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 执行拒绝场景并返回异常, 参数为动作及错误匹配模式, 未拒绝或原因不符时抛错. #>
function Reject([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.Exception.Message -match $Pattern) ('预期拒绝未发生: ' + $Pattern + '; ' + $failure)
    return $failure
}
<# .SYNOPSIS 创建标准布局的隔离发布目录, 参数为仓库名和相对构建目录, 返回发布根; 不生成可执行产品. #>
function New-Publish([string]$Repository, [string]$Layout) {
    $root = Join-Path $sandbox ($Repository + '/artifacts/build/' + $Layout)
    $null = [IO.Directory]::CreateDirectory((Join-Path $root 'web/branding'))
    [IO.File]::WriteAllText((Join-Path $root 'PbiUtils.Desktop.exe'), 'isolated non-executable fixture')
    [IO.File]::WriteAllBytes((Join-Path $root 'web/branding/logo.svg'), $icon)
    return $root
}

try {
    foreach ($module in @($registration, $files)) {
        $resolved = & $module { Get-PbiExternalToolRegistrationPath Development }
        Assert ($resolved -ieq [IO.Path]::GetFullPath($devPath)) '所有登记层均须先核验已隔离目标, 再执行任何写入'
    }
    $release = New-Publish 'repo' 'release/win-x64/without-dotnet'
    $debug = New-Publish 'repo' 'debug/win-x64'
    $bundled = New-Publish 'repo' 'release/win-x64/with-dotnet'
    $foreign = New-Publish 'foreign' 'release/win-x64/without-dotnet'
    $result = Register-PbiDevelopmentExternalTool -PublishRoot $release -Version '1.2.3' -NonInteractive
    Assert ($result.Path -ieq [IO.Path]::GetFullPath($devPath)) ('开发登记必须使用独立固定文件名: ' + $result.Path + '; ' + $devPath)
    $json = [IO.File]::ReadAllText($devPath) | ConvertFrom-Json
    $expectedName = 'pbi-utils (' + [char]0x5f00 + [char]0x53d1 + ')'
    Assert ($json.name -ceq $expectedName) 'Windows PowerShell 5.1 必须生成正确中文开发版名称'
    Assert ($json.path -ceq (Join-Path $release 'PbiUtils.Desktop.exe') -and $json.version -ceq '1.2.3') '描述必须携带实际绝对路径和本轮版本'
    Assert ($json.arguments -ceq '--server "%server%" --database "%database%"') '只能传递固定的 Power BI 上下文占位参数'
    Assert ($json.iconData -ceq ('image/svg+xml;base64,' + [Convert]::ToBase64String($icon))) '必须复用当前发布图标原始字节'
    $original = [IO.File]::ReadAllBytes($devPath)
    $firstWrite = [IO.File]::GetLastWriteTimeUtc($devPath)
    $null = Register-PbiDevelopmentExternalTool -PublishRoot $release -Version '1.2.3' -NonInteractive
    Assert ([IO.File]::GetLastWriteTimeUtc($devPath) -eq $firstWrite) '同内容登记必须只读成功, 不重复写文件'
    foreach ($publish in @($debug, $bundled, $release)) {
        $null = Register-PbiDevelopmentExternalTool -PublishRoot $publish -Version '1.2.4' -NonInteractive
        Assert (([IO.File]::ReadAllText($devPath) | ConvertFrom-Json).path -ceq (Join-Path $publish 'PbiUtils.Desktop.exe')) '同仓库 Debug/Release 和运行时模式切换必须更新开发入口'
    }
    $beforeForeign = [IO.File]::ReadAllBytes($devPath)
    $null = Reject { Register-PbiDevelopmentExternalTool -PublishRoot $foreign -Version '1.2.4' -NonInteractive } '不属于|冲突'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($devPath)) -ceq [Convert]::ToBase64String($beforeForeign)) '不同仓库不得接管开发登记'

    $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
    Set-PbiExternalToolRegistration $snapshot '1.0.0' Install $true
    Assert (([IO.File]::ReadAllText($installedPath) | ConvertFrom-Json).name -ceq 'pbi-utils') '安装登记与开发登记应同时存在且名称独立'
    $installedBefore = [IO.File]::ReadAllBytes($installedPath)
    $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
    Set-PbiExternalToolRegistration $snapshot '1.1.0' Install $true
    Restore-PbiExternalToolRegistration $snapshot
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($installedPath)) -ceq [Convert]::ToBase64String($installedBefore)) '升级失败必须恢复前版本精确字节'
    $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
    Set-PbiExternalToolRegistration $snapshot '1.0.0' Uninstall $true
    Assert (![IO.File]::Exists($installedPath) -and [IO.File]::Exists($devPath)) '卸载仅移除安装版登记, 开发版保持'
    Restore-PbiExternalToolRegistration $snapshot
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($installedPath)) -ceq [Convert]::ToBase64String($installedBefore)) '卸载后续阶段失败必须重建原始登记'

    $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
    [IO.File]::WriteAllText($installedPath, '{"name":"foreign"}')
    $null = Reject { Set-PbiExternalToolRegistration $snapshot '2.0.0' Install $true } '发生变化'
    $unknown = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
    Set-PbiExternalToolRegistration $unknown '1.0.0' Uninstall $true
    Assert ([IO.File]::ReadAllText($installedPath) -ceq '{"name":"foreign"}') '卸载必须保留用户改写的未知登记'
    $null = Reject { Set-PbiExternalToolRegistration $unknown '1.0.0' Install $true } '不属于'
    [IO.File]::WriteAllBytes($installedPath, $installedBefore)
    $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
    Set-PbiExternalToolRegistration $snapshot '1.1.0' Install $true
    [IO.File]::WriteAllText($installedPath, 'user changed after apply')
    $null = Reject { Restore-PbiExternalToolRegistration $snapshot } '未覆盖用户更改'
    Assert ([IO.File]::ReadAllText($installedPath) -ceq 'user changed after apply') '回滚不得覆盖安装后并发修改'
    [IO.File]::WriteAllBytes($installedPath, $installedBefore)

    # 在真实独占句柄写入后注入失败, 验证原位回读及恢复使用同一文件租约.
    $originalWriter = & $files { return ${function:Write-PbiExternalToolStream} }
    & $files {
        param($writer)
        $script:OriginalWriter = $writer; $script:FailWrite = $true
        <# .SYNOPSIS 首次真实写入后注入失败, 参数与生产写入器一致, 恢复写入正常执行. #>
        function script:Write-PbiExternalToolStream([IO.FileStream]$Stream, [string]$Content) {
            & $script:OriginalWriter $Stream $Content
            if ($script:FailWrite) { $script:FailWrite = $false; throw 'injected post-write failure' }
        }
    } $originalWriter
    try {
        $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
        $null = Reject { Set-PbiExternalToolRegistration $snapshot '1.3.0' Install $true } 'post-write failure'
        Restore-PbiExternalToolRegistration $snapshot
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($installedPath)) -ceq [Convert]::ToBase64String($installedBefore)) '真实写后失败必须恢复旧字节'
    } finally { & $files { param($writer) Set-Item -LiteralPath Function:script:Write-PbiExternalToolStream -Value $writer } $originalWriter }
    $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
    $lease = [IO.File]::Open($installedPath, 'Open', 'Read', 'Read')
    try { $null = Reject { Set-PbiExternalToolRegistration $snapshot '1.3.0' Install $true } '独占|占用|used|process' }
    finally { $lease.Dispose() }
    Restore-PbiExternalToolRegistration $snapshot

    # 改写产品参数和重复字段必须在任何固定文件写入前拒绝, helper 不能成为任意程序登记代理.
    $valid = [IO.File]::ReadAllText($installedPath)
    $bad = $valid.Replace('--server', '--arbitrary')
    $badContent = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bad))
    $null = Reject { & $files { param($before, $after) Invoke-PbiExternalToolFileChange Installed $before $after } ([Convert]::ToBase64String($installedBefore)) $badContent } '不属于'
    $duplicate = $valid.Replace('"version":', '"version":"9.9.9", "version":')
    $null = Reject { & $descriptor { param($content) Read-PbiExternalToolDescriptor Installed $content } ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($duplicate))) } '重复'
    $link = Join-Path $sandbox 'linked'
    $null = New-Item -ItemType Junction -Path $link -Target $directory
    try { $null = Reject { & $descriptor { param($path) Resolve-PbiExternalToolPath $path } (Join-Path $link 'pbi-utils.pbitool.json') } '重解析|链接' }
    finally { [IO.Directory]::Delete($link) }

    # 以真实注册 API 驱动权限分支, 只替换权限适配和进程启动, 不弹 UAC、不接触系统目录.
    $originalChange = & $registration { return ${function:Invoke-PbiExternalToolFileChange} }
    $privilege = @{ Calls = 0; Cancel = $false; Expected = ''; Desired = ''; Files = $files; CopiedRoot = ''; Command = '' }
    & $registration {
        param($state)
        $script:PrivilegeTest = $state
        <# .SYNOPSIS 固定模拟共享目录无权限, 供真实注册流程验证非交互及最小提权分支. #>
        function script:Invoke-PbiExternalToolFileChange([string]$Kind, [string]$Expected, [string]$Desired) {
            $script:PrivilegeTest.Expected = $Expected; $script:PrivilegeTest.Desired = $Desired
            throw (New-Object UnauthorizedAccessException('isolated access denied'))
        }
        <# .SYNOPSIS 捕获真实提权命令并在隔离目录代办文件事务, 不启动外部进程; 可模拟 UAC 取消. #>
        function script:Start-Process($FilePath, $ArgumentList, $Verb, $WindowStyle, [switch]$PassThru, [switch]$Wait) {
            $state = $script:PrivilegeTest; $state.Calls++; $state.Command = $ArgumentList
            if ($Verb -cne 'RunAs' -or $WindowStyle -cne 'Hidden' -or !$Wait -or !$FilePath.EndsWith('\WindowsPowerShell\v1.0\powershell.exe')) { throw '提权启动参数不受限' }
            if ($ArgumentList -notmatch '-File "(?<worker>[^"]+)"') { throw '提权缺少固定 worker' }
            $state.CopiedRoot = [IO.Path]::GetDirectoryName($Matches.worker)
            foreach ($name in @('ExternalTool-Worker.ps1', 'ExternalToolFiles.psm1', 'ExternalToolDescriptor.psm1')) {
                $path = Join-Path $state.CopiedRoot $name
                if (![IO.File]::Exists($path)) { throw '提权缺少预载维护文件' }
                $read = [IO.File]::ReadAllBytes($path)
                if ($read.Length -eq 0) { throw '提权维护文件必须允许子进程读取' }
                $blocked = $false
                try { [IO.File]::WriteAllText($path, 'untrusted replacement') } catch [IO.IOException] { $blocked = $true }
                if (!$blocked) { throw 'UAC 等待期间必须拒绝替换维护代码' }
                $blocked = $false
                try { [IO.File]::Delete($path) } catch [IO.IOException] { $blocked = $true }
                if (!$blocked) { throw 'UAC 等待期间必须拒绝删除维护代码' }
            }
            if ($state.Cancel) { throw (New-Object ComponentModel.Win32Exception(1223)) }
            & $state.Files { param($before, $after) Invoke-PbiExternalToolFileChange Installed $before $after } $state.Expected $state.Desired
            $result = [PSCustomObject]@{ ExitCode = 0 }
            $result | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
            return $result
        }
    } $privilege
    try {
        $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
        Set-PbiExternalToolRegistration $snapshot '1.0.0' Install $true
        Assert ($privilege.Calls -eq 0) '同内容登记不应申请管理员权限'
        $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
        $failure = Reject { Set-PbiExternalToolRegistration $snapshot '1.4.0' Install $true } '管理员 PowerShell'
        Assert ($failure.Exception.Data['ExitCode'] -eq 3 -and $privilege.Calls -eq 0) '非交互权限不足须返回前置错误且绝不弹 UAC'
        Restore-PbiExternalToolRegistration $snapshot
        $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
        Set-PbiExternalToolRegistration $snapshot '1.4.0' Install $false
        Assert ($privilege.Calls -eq 1 -and ![IO.Directory]::Exists($privilege.CopiedRoot)) '交互提权必须使用临时预载维护副本并清理'
        Restore-PbiExternalToolRegistration $snapshot
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($installedPath)) -ceq [Convert]::ToBase64String($installedBefore)) '需要权限的恢复也须按精确字节回滚'
        $privilege.Cancel = $true
        $snapshot = Get-PbiExternalToolSnapshot Installed $installedTarget $icon
        $failure = Reject { Set-PbiExternalToolRegistration $snapshot '1.5.0' Install $false } '取消'
        Assert ($failure.Exception.Data['ExitCode'] -eq 130) 'UAC 取消应保留取消退出码'
        Restore-PbiExternalToolRegistration $snapshot
    } finally {
        & $registration {
            param($change)
            Set-Item -LiteralPath Function:script:Invoke-PbiExternalToolFileChange -Value $change
            Remove-Item -LiteralPath Function:script:Start-Process
        } $originalChange
    }
    Write-Output 'PASS external tool registration, ownership, rollback and privilege boundaries'
} finally {
    $absolute = [IO.Path]::GetFullPath($sandbox)
    if ([IO.Path]::GetDirectoryName($absolute) -ine [IO.Path]::GetTempPath().TrimEnd('\') -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-external-tools-test-[0-9a-f]{32}$') { throw '外部工具测试清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
