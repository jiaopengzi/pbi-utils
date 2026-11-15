# FilePath    : pbi-utils\scripts\tests\gate-failure-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在正式入口的私有副本验证发布枚举、清理失败和首个错误的退出码及阶段不会被覆盖.

<# .SYNOPSIS 各案例在默认 Continue 的全新 pwsh 运行真实入口、构建和发布脚本, 不调用 SDK 或更改真实发布目录. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-gate-failure-' + [Guid]::NewGuid().ToString('N'))
$pwsh = (Get-Process -Id $PID).Path
$savedOutputEncoding = [Console]::OutputEncoding
$failures = [Collections.Generic.List[string]]::new()
$registrySource = Get-Content -LiteralPath (Join-Path $sourceRoot 'scripts/CommandRegistry.psm1') -Raw
$publisherSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'scripts/PublishArtifacts.psm1') -Raw

<# .SYNOPSIS 收集各独立案例的断言失败, 不让第一个错误遮住其余边界.
   .PARAMETER Condition 应满足的真实进程或文件事实.
   .PARAMETER Message 失败定位, 不包含用户数据.
#>
function Assert-Gate([bool]$Condition, [string]$Message) {
    if (!$Condition) { $failures.Add($Message); [Console]::Error.WriteLine("FAIL $Message") }
}

<# .SYNOPSIS 返回测试自有发布目录的文件内容快照, 用于证明失败未替换上轮产物.
   .PARAMETER Root 测试拥有且不含链接的发布目录.
   .OUTPUTS 按相对路径排序的路径和 SHA256 字符串.
#>
function Get-GateSnapshot([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join '|'
}

<# .SYNOPSIS 准备完整旧产物, 使枚举失败案例能验证发布前保护及清理案例的真实备份路径.
   .PARAMETER Root 测试独占的固定发布目标.
   .PARAMETER DeploymentMode 对应旧包的真实部署模式, 保证回滚基线也具备正确框架描述.
#>
function New-GatePublishedBaseline([string]$Root, [string]$DeploymentMode) {
    $null = New-Item -ItemType Directory -Path $Root -Force
    foreach ($name in @('pbi-utils.exe', 'pbi-utils.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) { [IO.File]::WriteAllText((Join-Path $Root $name), "old $name") }
    [IO.File]::WriteAllText((Join-Path $Root 'pbi-utils.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.13"}]}}')
    [IO.File]::WriteAllText((Join-Path $Root 'pbi-utils.deps.json'), '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64","signature":""},"compilationOptions":{},"targets":{".NETCoreApp,Version=v10.0":{},".NETCoreApp,Version=v10.0/win-x64":{"pbi-utils/1.2.3":{"dependencies":{"runtimepack.Microsoft.NETCore.App.Runtime.win-x64":"10.0.13"},"runtime":{"pbi-utils.dll":{}}},"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13":{"runtime":{"System.Private.CoreLib.dll":{}},"native":{"coreclr.dll":{},"hostfxr.dll":{},"hostpolicy.dll":{}}}}},"libraries":{"pbi-utils/1.2.3":{"type":"project","serviceable":false,"sha512":""},"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13":{"type":"runtimepack","serviceable":false,"sha512":""}}}')
    $baseDeps = Get-Content -LiteralPath (Join-Path $Root 'pbi-utils.deps.json') -Raw
    $frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.13' }, @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.13' })
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host')) {
        if ($entry -ne 'pbi-utils') {
            foreach ($suffix in @('exe', 'dll')) { [IO.File]::WriteAllText((Join-Path $Root "$entry.$suffix"), "old $entry.$suffix") }
        }
        $deps = $baseDeps.Replace('pbi-utils', $entry) | ConvertFrom-Json -AsHashtable
        $target = $deps.targets[$deps.runtimeTarget.name]
        $runtimeOptions = @{ tfm = 'net10.0' }
        if ($DeploymentMode -eq 'self-contained') {
            $frameworkId = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'
            $target["$entry/1.2.3"].dependencies[$frameworkId] = '10.0.13'
            $target["$frameworkId/10.0.13"] = @{}
            $deps.libraries["$frameworkId/10.0.13"] = @{ type = 'runtimepack'; serviceable = $false; sha512 = '' }
            $runtimeOptions.includedFrameworks = $frameworks
        } else {
            $target["$entry/1.2.3"].dependencies = @{}
            foreach ($name in @($target.Keys | Where-Object { $_ -like 'runtimepack.*' })) { $target.Remove($name); $deps.libraries.Remove($name) }
            $runtimeOptions.frameworks = $frameworks
        }
        $deps | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $Root "$entry.deps.json") -Encoding utf8
        @{ runtimeOptions = $runtimeOptions } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Root "$entry.runtimeconfig.json") -Encoding utf8
    }
    if ($DeploymentMode -eq 'framework-dependent') {
        foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) { Remove-Item -LiteralPath (Join-Path $Root $name) }
    }
    $files = Get-ChildItem -LiteralPath $Root -File | ForEach-Object { @{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } }
    @{ schemaVersion = 3; layout = 'shared'; deploymentMode = $DeploymentMode; version = '1.2.3'; builtAtUtc = '2026-01-01T00:00:00Z'; runtime = 'win-x64'; scope = 'Core'; configuration = 'Release'; files = @($files) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Root 'manifest.json') -Encoding utf8
}

$registryBoundary = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'TestProjectGraphFixture.ps1') -Raw) + [Environment]::NewLine + @'
# 保留生产原生命令边界, 快速失败案例通过它启动真实长时子进程; 下方替身随后覆盖同名函数.
$script:GateOriginalNative = ${function:Invoke-PbiNative}
<# .SYNOPSIS 隔离工具和还原探测, 真实参数解析与动作错误边界保持. #>
function Assert-PbiTools([string[]]$Tools) { }
function Assert-PbiRestoreAssets([string]$Scope, [bool]$IncludeTests = $true) { }
<# .SYNOPSIS 固定为并行门禁, 失败断言不随测试机逻辑处理器数量变化. #>
function Test-PbiParallelGate { return $true }
<# .SYNOPSIS 固定测试输入指纹, 避免在私有仓库调用 Node. #>
function Get-PbiFrontendInputFingerprint { return ('a' * 64) }
<#
.SYNOPSIS 经生产原生命令边界启动 600 秒的真实 pwsh 子进程, 先登记 PID 与启动时刻再阻塞.
.PARAMETER Role 所属阶段, 写入登记文件名便于诊断.
.NOTES 只有快速失败终止整棵在途进程树时才会提前结束; 案例超时短于 600 秒, 未终止即失败.
#>
function Invoke-GateSleeper([string]$Role) {
    $command = "[IO.File]::WriteAllText((Join-Path `$env:PBI_GATE_SLEEPERS ('$Role-' + [Guid]::NewGuid().ToString('N') + '.txt')), [string]`$PID + '|' + (Get-Process -Id `$PID).StartTime.ToUniversalTime().Ticks); Start-Sleep -Seconds 600"
    & $script:GateOriginalNative (Join-Path $PSHOME 'pwsh.exe') @('-NoProfile', '-NonInteractive', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)))
}
<# .SYNOPSIS 等待指定数量的长时子进程登记, 使受控失败发生在全部在途阶段运行之后; 最多等待 45 秒. #>
function Wait-GateSleepers([int]$Count) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (@([IO.Directory]::GetFiles($env:PBI_GATE_SLEEPERS, '*.txt')).Count -lt $Count -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
}
<# .SYNOPSIS 只模拟编译文件或原生阶段错误, 真实发布清单、覆盖和清理不替换. #>
function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
    $failFast = $script:GateFailureMode -in @('lane-fail-fast', 'main-fail-fast')
    if ($File -eq 'pnpm') {
        $vitest = $Arguments -contains 'exec'
        if ($script:GateFailureMode -eq 'main-fail-fast') { Invoke-GateSleeper $(if ($vitest) { 'testVue' } else { 'checkVue' }); return }
        if ($vitest) {
            if ($script:GateFailureMode -eq 'lane-fail-fast') { Invoke-GateSleeper 'testVue' }
            # 其他案例的测试通道直接通过, 只有 lint 失败, 首个失败阶段确定为 checkVue.
            return
        }
        if ($script:GateFailureMode -eq 'lane-fail-fast') { Wait-GateSleepers 4 }
        Stop-Pbi 49 '受控前端主错误'
    }
    if ($File -ne 'dotnet') { throw '测试未声明的工具' }
    if ($Arguments[0] -eq 'format') {
        if ($failFast) { Invoke-GateSleeper 'checkC#'; return }
        [IO.File]::WriteAllText($Arguments[[Array]::IndexOf($Arguments, '--report') + 1], '[]')
        return
    }
    if ($Arguments[0] -eq 'msbuild') {
        if ($failFast) { Invoke-GateSleeper 'testC#'; return }
        $graph = Get-TestProjectGraphFixture $File $Arguments
        $now = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        # 嵌套前端失败案例使用真实 C# 测试门禁, 仅提供一项本轮实际通过的合成回执.
        $xml = @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times start="$now" finish="$now" />
  <Results><UnitTestResult outcome="Passed" /></Results>
  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" /></ResultSummary>
</TestRun>
"@
        Write-TestProjectGraphFixtureReports $graph $xml
        return
    }
    if ($Arguments[0] -ne 'publish') { throw '测试未声明的 dotnet 动作' }
    if ($script:GateFailureMode -eq 'lane-fail-fast') { Invoke-GateSleeper 'buildC#'; return }
    if ($script:GateFailureMode -eq 'main-fail-fast') { Wait-GateSleepers 4; Stop-Pbi 47 '受控发布主错误' }
    if ($script:GateFailureMode -eq 'primary-publish') { Stop-Pbi 47 '受控发布主错误' }
    $output = $Arguments[[Array]::IndexOf($Arguments, '-o') + 1]
    $null = New-Item -ItemType Directory -Path $output -Force
    [IO.File]::WriteAllText((Join-Path $output 'pbi-utils.exe'), 'new executable')
    foreach ($name in @('pbi-utils.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) { [IO.File]::WriteAllText((Join-Path $output $name), "new $name") }
    [IO.File]::WriteAllText((Join-Path $output 'pbi-utils.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.13"}]}}')
    [IO.File]::WriteAllText((Join-Path $output 'pbi-utils.deps.json'), '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64","signature":""},"compilationOptions":{},"targets":{".NETCoreApp,Version=v10.0":{},".NETCoreApp,Version=v10.0/win-x64":{"pbi-utils/1.2.3":{"dependencies":{"runtimepack.Microsoft.NETCore.App.Runtime.win-x64":"10.0.13"},"runtime":{"pbi-utils.dll":{}}},"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13":{"runtime":{"System.Private.CoreLib.dll":{}},"native":{"coreclr.dll":{},"hostfxr.dll":{},"hostpolicy.dll":{}}}}},"libraries":{"pbi-utils/1.2.3":{"type":"project","serviceable":false,"sha512":""},"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13":{"type":"runtimepack","serviceable":false,"sha512":""}}}')
    # 后台夹具使用相同框架文件, 但保留独立入口与完整 Windows 框架声明.
    if ($Arguments[1] -like '*Host') {
        $depsPath = Join-Path $output 'pbi-utils.deps.json'
        $deps = (Get-Content -LiteralPath $depsPath -Raw).Replace('pbi-utils', 'PbiUtils.Host') | ConvertFrom-Json -AsHashtable
        $target = $deps.targets[$deps.runtimeTarget.name]
        $frameworkId = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'
        $target['PbiUtils.Host/1.2.3'].dependencies[$frameworkId] = '10.0.13'
        $target["$frameworkId/10.0.13"] = @{}
        $deps.libraries["$frameworkId/10.0.13"] = @{ type = 'runtimepack'; serviceable = $false; sha512 = '' }
        $deps | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $depsPath -Encoding utf8
        $configPath = Join-Path $output 'pbi-utils.runtimeconfig.json'
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
        $config.runtimeOptions.includedFrameworks += @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.13' }
        $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
        foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
            Move-Item -LiteralPath (Join-Path $output "pbi-utils.$suffix") -Destination (Join-Path $output "PbiUtils.Host.$suffix")
        }
    }
    if ($Arguments[[Array]::IndexOf($Arguments, '--self-contained') + 1] -eq 'false') {
        $entry = if ($Arguments[1] -like '*Host') { 'PbiUtils.Host' } else { 'pbi-utils' }
        $depsPath = Join-Path $output "$entry.deps.json"
        $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
        $target = $deps.targets[$deps.runtimeTarget.name]
        $target["$entry/1.2.3"].dependencies = @{}
        foreach ($name in @($target.Keys | Where-Object { $_ -like 'runtimepack.*' })) { $target.Remove($name); $deps.libraries.Remove($name) }
        $deps | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $depsPath -Encoding utf8
        $configPath = Join-Path $output "$entry.runtimeconfig.json"
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
        $config.runtimeOptions.frameworks = $config.runtimeOptions.includedFrameworks
        $config.runtimeOptions.Remove('includedFrameworks')
        $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
        foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) { Remove-Item -LiteralPath (Join-Path $output $name) }
    }
    if ($script:GateFailureMode -eq 'inventory') {
        $null = New-Item -ItemType Directory -Path (Join-Path $output 'blocked') -Force
        [IO.File]::WriteAllText((Join-Path $output 'blocked/required-asset.bin'), 'must not disappear from inventory')
    }
}
'@
$publisherBoundary = @'
$script:GateOriginalCleanup = (Get-Command Remove-PbiPublishTemporary).ScriptBlock
<# .SYNOPSIS 只在本测试指定清理边界抛错, 其余临时目录仍执行原有安全清理. #>
function Remove-PbiPublishTemporary([string]$Path, [string]$Parent) {
    $leaf = [IO.Path]::GetFileName($Path)
    if ($script:GateFailureMode -eq 'cleanup-backup' -and $leaf -like '.pbi-utils-previous-*') { throw '受控备份清理失败' }
    if ($script:GateFailureMode -in @('cleanup-staging', 'primary-publish', 'primary-frontend') -and $leaf -like '.pbi-utils-publish-*') { throw '受控 staging 清理失败' }
    & $script:GateOriginalCleanup $Path $Parent
}
<# .SYNOPSIS 模拟真实枚举 cmdlet 的非终止错误, 由发布模块自身错误偏好决定是否能继续. #>
function Get-ChildItem {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Force)
    if ($script:GateFailureMode -eq 'inventory' -and [IO.Path]::GetFileName($LiteralPath) -eq 'blocked') { Write-Error '受控枚举失败'; return }
    Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $LiteralPath -Force:$Force
}
'@

<# .SYNOPSIS 返回登记目录中仍以同一 PID 与启动时刻存活的长时子进程; 登记未写完或进程已退出时跳过.
   .PARAMETER Directory 本案例私有的子进程登记目录.
   .OUTPUTS 存活的进程对象, 调用方负责释放.
#>
function Get-GateLiveSleepers([string]$Directory) {
    foreach ($file in [IO.Directory]::GetFiles($Directory, '*.txt')) {
        $parts = ([IO.File]::ReadAllText($file)).Split('|')
        if ($parts.Count -ne 2) { continue }
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$parts[0])
            if ($process.StartTime.ToUniversalTime().Ticks -eq [long]$parts[1] -and !$process.HasExited) { $process } else { $process.Dispose() }
        }
        catch { continue }
    }
}

<# .SYNOPSIS 在私有源副本通过正式 run.ps1 入口运行, 子进程不继承测试脚本的 ErrorActionPreference.
   .PARAMETER Mode 仅用于受控边界的固定场景标识.
   .PARAMETER DotNetMode 选中的发布模式, 另一模式只作为受保护旧包.
   .PARAMETER Action 正式动作 buildC# 或 verifyAndBuild.
   .PARAMETER TimeoutMilliseconds 入口最长运行时间; 超时终止整棵进程树并失败.
   .OUTPUTS 真实退出码、stdout/stderr、旧产物快照、测试发布路径、耗时与长时子进程登记.
   .NOTES 存活的子进程会持有输出管道, 读取限时; 结束时终止仍存活的已登记子进程, 不遗留测试进程.
#>
function Invoke-GateFailure([string]$Mode, [string]$DotNetMode, [string]$Action = 'buildC#', [int]$TimeoutMilliseconds = 30000) {
    $root = Join-Path $sandbox ($Mode + '-' + $DotNetMode)
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'scripts') -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'run.ps1') -Destination $root
    foreach ($scriptName in @('build.ps1', 'check.ps1', 'test.ps1', 'lane.ps1', 'GateWatchdog.psm1', 'TestResults.psm1', 'SharedPublish.psm1', 'PublishRuntime.psm1', 'ReleaseAutomation.psm1')) {
        if (Test-Path -LiteralPath (Join-Path $sourceRoot "scripts/$scriptName")) { Copy-Item -LiteralPath (Join-Path $sourceRoot "scripts/$scriptName") -Destination (Join-Path $root 'scripts') }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LicenseBundleStub.psm1') -Destination (Join-Path $root 'scripts/ThirdPartyLicenses.psm1')
    if ($Mode -eq 'license-failure') { [IO.File]::WriteAllText((Join-Path $root 'license-stub-failure'), 'fail after partial license output') }
    # 固定包脚本使测试通道走只运行 Vitest 的路径, 失败阶段只由受控边界决定.
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'web') -Force
    [IO.File]::WriteAllText((Join-Path $root 'web/package.json'), '{"scripts":{"test":"pnpm lint && vitest run","build":"pnpm lint && vite build"}}')
    $sleepers = Join-Path $root 'sleepers'
    $null = New-Item -ItemType Directory -Path $sleepers -Force
    $modeSource = "`$script:GateFailureMode = '$Mode'`n"
    [IO.File]::WriteAllText((Join-Path $root 'scripts/CommandRegistry.psm1'), $registrySource.Replace('Export-ModuleMember -Function *-Pbi*', $modeSource + $registryBoundary + "`nExport-ModuleMember -Function *-Pbi*"))
    [IO.File]::WriteAllText((Join-Path $root 'scripts/PublishArtifacts.psm1'), $publisherSource.Replace('Export-ModuleMember -Function *-Pbi*', $modeSource + $publisherBoundary + "`nExport-ModuleMember -Function *-Pbi*"))
    [IO.File]::WriteAllText((Join-Path $root 'version'), "v1.2.3`n")
    [IO.File]::WriteAllText((Join-Path $root 'global.json'), '{"sdk":{"version":"10.0.403"}}')
    $published = Join-Path $root "artifacts/build/release/win-x64/$DotNetMode"
    $otherMode = if ($DotNetMode -eq 'with-dotnet') { 'without-dotnet' } else { 'with-dotnet' }
    $otherPublished = Join-Path $root "artifacts/build/release/win-x64/$otherMode"
    New-GatePublishedBaseline $published $(if ($DotNetMode -eq 'with-dotnet') { 'self-contained' } else { 'framework-dependent' })
    New-GatePublishedBaseline $otherPublished $(if ($otherMode -eq 'with-dotnet') { 'self-contained' } else { 'framework-dependent' })
    $before = Get-GateSnapshot $published
    $otherBefore = Get-GateSnapshot $otherPublished
    $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.WorkingDirectory = $root
    $start.Environment['PBI_GATE_SLEEPERS'] = $sleepers
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $root 'run.ps1'), '-Choice', $Action, '-Scope', 'Core', '-Configuration', 'Release', '-DotNetMode', $DotNetMode, '-NonInteractive')) { $start.ArgumentList.Add($argument) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutMilliseconds)) { $process.Kill($true); $process.WaitForExit(); throw '门禁边界子进程超时' }
        $elapsed = $watch.Elapsed
        # 终止请求异步完成, 给已终止的子进程最多 3 秒结束; 仍存活即为遗留.
        $settle = [Diagnostics.Stopwatch]::StartNew()
        do {
            $live = @(Get-GateLiveSleepers $sleepers)
            $liveCount = $live.Count
            foreach ($survivor in $live) { $survivor.Dispose() }
            if ($liveCount) { Start-Sleep -Milliseconds 100 }
        } while ($liveCount -and $settle.ElapsedMilliseconds -lt 3000)
        if (!$stdout.Wait(15000) -or !$stderr.Wait(15000)) { throw '入口退出后仍有子进程持有输出管道' }
        $licenseCallsPath = Join-Path $root 'license-stub-calls.jsonl'
        $licenseCalls = @(if ([IO.File]::Exists($licenseCallsPath)) { Get-Content -LiteralPath $licenseCallsPath | ForEach-Object { $_ | ConvertFrom-Json } })
        return @{ Mode = $Mode; Code = $process.ExitCode; Out = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult(); Published = $published; Before = $before; OtherPublished = $otherPublished; OtherBefore = $otherBefore; LicenseCalls = $licenseCalls; Elapsed = $elapsed; SleeperCount = @([IO.Directory]::GetFiles($sleepers, '*.txt')).Count; LiveSleepers = $liveCount }
    }
    finally {
        foreach ($survivor in @(Get-GateLiveSleepers $sleepers)) {
            try { $survivor.Kill($true) } catch { [Console]::Error.WriteLine('测试子进程清理失败: ' + $_.Exception.Message) }
            $survivor.Dispose()
        }
        $process.Dispose()
    }
}

try {
    # 正式 C# 包装器按 UTF-8 读取输出; CreateNoWindow 的全新 pwsh 可能初始使用系统代码页.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($dotNetMode in @('with-dotnet', 'without-dotnet')) {
        foreach ($mode in @('inventory', 'cleanup-staging', 'cleanup-backup', 'primary-publish', 'primary-frontend', 'license-failure')) {
            $case = Invoke-GateFailure $mode $dotNetMode $(if ($mode -eq 'primary-frontend') { 'verifyAndBuild' } else { 'buildC#' })
            $expectedCode = switch ($mode) { 'primary-publish' { 47 }; 'primary-frontend' { 49 }; 'license-failure' { 53 }; default { 4 } }
            $expectedAction = if ($mode -eq 'primary-frontend') { 'checkVue' } else { 'buildC#' }
            [Console]::Out.WriteLine("$dotNetMode / $mode : exit=$($case.Code)")
            Assert-Gate ($case.Code -eq $expectedCode) "$dotNetMode / $mode 必须返回最先错误的退出码 $expectedCode, 实际 $($case.Code)"
            Assert-Gate ($case.Error -match [Regex]::Escape("执行失败 [$expectedAction] (退出码 $expectedCode):")) "$mode 必须保留最先失败阶段 $expectedAction"
            Assert-Gate ($case.Out -notmatch '完成 buildC#|完成 构建与打包|verifyAndBuild 全部通过|发布产物 \(|构建计时') "$mode 失败不得打印完成、成功构建计时或总成功"
            Assert-Gate ((Get-GateSnapshot $case.OtherPublished) -ceq $case.OtherBefore) "$mode 无论选中模式是否已交付, 均不得改动未选模式"
            if ($mode -in @('cleanup-staging', 'cleanup-backup', 'license-failure')) {
                Assert-Gate ($case.LicenseCalls.Count -eq 1 -and $case.LicenseCalls[0].scope -ceq 'Core') "$mode 必须恰好一次按当前范围调用许可生成器"
            }
            if ($mode -eq 'inventory') {
                Assert-Gate ($case.Error.Contains('受控枚举失败')) '枚举真实错误必须可诊断'
                Assert-Gate ((Get-GateSnapshot $case.Published) -ceq $case.Before) '枚举失败不得以部分清单替换原产物'
            }
            elseif ($mode -eq 'license-failure') {
                Assert-Gate ($case.Error.Contains('受控许可生成失败')) '许可生成失败必须保留真实错误原因'
                Assert-Gate ((Get-GateSnapshot $case.Published) -ceq $case.Before) '许可仅部分生成后失败不得修改上轮文件或发布清单'
            }
            elseif ($mode -in @('primary-publish', 'primary-frontend')) {
                $primary = if ($mode -eq 'primary-publish') { '受控发布主错误' } else { '受控前端主错误' }
                Assert-Gate ($case.Error.Contains($primary)) "$mode 不得以清理错误替换原始原因"
                Assert-Gate ($case.Error.Contains('受控 staging 清理失败')) "$mode 清理失败仍须可诊断"
                Assert-Gate ((Get-GateSnapshot $case.Published) -ceq $case.Before) "$mode 主错误发生于交付前, 原产物必须保持"
            }
            else {
                $cleanup = if ($mode -eq 'cleanup-backup') { '受控备份清理失败' } else { '受控 staging 清理失败' }
                Assert-Gate ($case.Error.Contains($cleanup)) "$mode 不得吞掉清理错误原因"
                Assert-Gate ($case.Error.Contains('发布文件已交付, 但')) "$mode 必须明确报告文件已经交付"
                Assert-Gate ((Get-GateSnapshot $case.Published) -cne $case.Before) "$mode 清理单独失败发生于交付后, 本轮产物必须已替换"
                Assert-Gate ([IO.File]::ReadAllText((Join-Path $case.Published 'pbi-utils.exe')) -ceq 'new executable') "$mode 已交付文件必须来自本轮"
                $manifest = Get-Content -LiteralPath (Join-Path $case.Published 'manifest.json') -Raw | ConvertFrom-Json
                $paths = @($manifest.files | ForEach-Object { $_.file } | Sort-Object)
                $expectedPaths = @('pbi-utils.exe', 'pbi-utils.dll', 'pbi-utils.deps.json', 'pbi-utils.runtimeconfig.json', 'PbiUtils.Host.exe', 'PbiUtils.Host.dll', 'PbiUtils.Host.deps.json', 'PbiUtils.Host.runtimeconfig.json')
                $expectedPaths += @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')
                if ($dotNetMode -eq 'with-dotnet') { $expectedPaths += @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll') }
                $expectedPaths = $expectedPaths | Sort-Object
                Assert-Gate (($paths -join '|') -ceq ($expectedPaths -join '|')) "$mode 已交付清单必须包含选中模式的完整文件集合"
                $deploymentMode = if ($dotNetMode -eq 'with-dotnet') { 'self-contained' } else { 'framework-dependent' }
                Assert-Gate ($manifest.schemaVersion -eq 3 -and $manifest.layout -eq 'shared' -and $manifest.deploymentMode -eq $deploymentMode) "$mode 已交付清单必须声明正确部署模式"
                foreach ($entry in $manifest.files) {
                    Assert-Gate ((Get-FileHash -LiteralPath (Join-Path $case.Published $entry.file) -Algorithm SHA256).Hash -ceq $entry.sha256) "$mode 已交付清单的哈希必须匹配真实产物"
                }
            }
        }
    }
    # 快速失败: 受控失败发生在其余阶段的真实长时子进程都已运行之后, 门禁必须立即终止它们并报告首个失败.
    foreach ($fast in @(
            @{ Mode = 'lane-fail-fast'; Code = 49; Action = 'checkVue'; Terminated = @('checkC#', 'testC#', 'testVue', 'buildC#') },
            @{ Mode = 'main-fail-fast'; Code = 47; Action = 'buildC#'; Terminated = @('checkC#', 'testC#', 'checkVue', 'testVue') }
        )) {
        $case = Invoke-GateFailure $fast.Mode 'without-dotnet' 'verifyAndBuild' 120000
        [Console]::Out.WriteLine("without-dotnet / $($fast.Mode) : exit=$($case.Code) elapsed=$($case.Elapsed.TotalSeconds.ToString('F1', [Globalization.CultureInfo]::InvariantCulture))s")
        Assert-Gate ($case.Code -eq $fast.Code) "$($fast.Mode) 必须返回首个失败的退出码 $($fast.Code), 实际 $($case.Code)"
        Assert-Gate ($case.Error -match [Regex]::Escape("执行失败 [$($fast.Action)] (退出码 $($fast.Code)):")) "$($fast.Mode) 必须报告首个失败阶段 $($fast.Action)"
        Assert-Gate ($case.SleeperCount -eq 4) "$($fast.Mode) 受控失败前必须有 4 个真实长时子进程在运行, 实际 $($case.SleeperCount)"
        Assert-Gate ($case.LiveSleepers -eq 0) "$($fast.Mode) 退出后不得遗留在途子进程, 实际 $($case.LiveSleepers)"
        $listed = [Regex]::Match($case.Error, '首个失败后已立即终止在途阶段: ([^\r\n]+)')
        $actualTerminated = if ($listed.Success) { (@($listed.Groups[1].Value.Trim() -split '、') | Sort-Object) -join ',' } else { '' }
        Assert-Gate ($actualTerminated -ceq ((@($fast.Terminated) | Sort-Object) -join ',')) "$($fast.Mode) 必须准确列出被终止的阶段, 实际 $actualTerminated"
        foreach ($name in @($fast.Terminated | Where-Object { $_ -ne 'buildC#' })) {
            Assert-Gate ($case.Out -match ([Regex]::Escape("并行计时 [$name] ") + '\S+ 已终止')) "$($fast.Mode) 计时必须标记 $name 已终止"
        }
        Assert-Gate ($case.Out -notmatch '完成 buildC#|完成 构建与打包|verifyAndBuild 全部通过|发布产物 \(|构建计时') "$($fast.Mode) 失败不得打印完成、成功构建计时或总成功"
        Assert-Gate ((Get-GateSnapshot $case.Published) -ceq $case.Before) "$($fast.Mode) 交付前失败必须保留原产物"
        if ($fast.Mode -eq 'lane-fail-fast') {
            Assert-Gate ($case.Error.Contains('并行阶段 [checkVue] 失败 (退出码 49), 立即终止其余在途阶段.')) '看护必须在通道失败时立即提示终止其余阶段'
        }
    }
    if ($failures.Count) { throw "$($failures.Count) 项门禁失败传播断言未通过" }
    Write-Output 'PASS 发布枚举与清理错误传播、首个退出码及阶段保持契约'
    Write-Output 'PASS 门禁首个失败立即终止在途阶段与进程树契约'
}
finally {
    [Console]::OutputEncoding = $savedOutputEncoding
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils-gate-failure-[0-9a-f]{32}$') { throw '门禁测试清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
