# FilePath    : pbi-utils\scripts\dev.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 启动显式浏览器预览或 Debug 桌面开发, 管理本次子进程.

<# .SYNOPSIS 显式 mock 预览或 Debug 桌面开发, 仅清理本次创建的进程. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    $raw = @($args)
    if ($raw.Count -ne 2 -or $raw[0] -ne '-Target' -or $raw[1] -notin @('Desktop', 'Web')) { exit 2 }
    exit (Invoke-PbiEntry -Arguments @() -Action $(if ($raw[1] -eq 'Desktop') { 'devDesktop' } else { 'devWeb' }))
}
$InternalOptions = $PbiInvocation.Options
$InternalAction = $PbiInvocation.Action
if ($InternalAction -eq 'devWeb') { Invoke-PbiNative pnpm @('--dir', 'web', 'dev:mock'); return }
$root = Split-Path $PSScriptRoot -Parent
if ((Get-PbiWebViewRuntime) -eq 'missing') { Stop-Pbi 3 '缺少 WebView2 Evergreen Runtime, 参见 .docs/toolchain.md' }
Import-Module (Join-Path $PSScriptRoot 'DevelopmentOutputs.psm1') -Force
Assert-PbiDevelopmentDirectoryAvailable (Get-PbiDevelopmentDirectory $root)
foreach ($project in @('src/PbiUtils.Host', 'src/PbiUtils.Cli', 'src/PbiUtils.Desktop')) {
    Invoke-PbiNative dotnet @('build', $project, '-c', 'Debug', '--no-restore')
}
$executable = Set-PbiDevelopmentOutputs $root
$start = [Diagnostics.ProcessStartInfo]::new('node')
$start.WorkingDirectory = Join-Path $root 'web'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in @('node_modules/vite/bin/vite.js', '--host', '127.0.0.1', '--port', '5173', '--strictPort')) { $start.ArgumentList.Add($argument) }
$server = $null; $desktop = $null
try {
    # 在启动前拒绝已有监听, 避免连接到其他开发服务器.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 5173)
    try { $listener.Start() } catch { Stop-Pbi 3 '5173 端口已占用' } finally { $listener.Stop() }
    $server = [Diagnostics.Process]::Start($start)
    $ready = $false; $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($server.HasExited) { Stop-Pbi $server.ExitCode 'Vite 启动失败' }
        try { $response = Invoke-WebRequest 'http://127.0.0.1:5173/' -TimeoutSec 1; $ready = $response.StatusCode -eq 200 } catch { $ready = $false }
        if ($ready) { break }
        Start-Sleep -Milliseconds 150
    }
    if (!$ready) { Stop-Pbi 3 'Vite 启动超时' }
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($executable)
    $start.Environment['PBI_UTILS_DEV_URL'] = 'http://127.0.0.1:5173'
    $desktop = [Diagnostics.Process]::Start($start)
    $desktop.WaitForExit()
    if ($desktop.ExitCode -ne 0) { Stop-Pbi $desktop.ExitCode '桌面进程退出失败' }
}
finally {
    if ($desktop) {
        # GUI 正常关闭才能排空本连接; 共享 Host 不属于本脚本, 不按进程树结束.
        if (!$desktop.HasExited) { $null = $desktop.CloseMainWindow(); $desktop.WaitForExit() }
        $desktop.Dispose()
    }
    if ($server) {
        if (!$server.HasExited) { $server.Kill($true); $server.WaitForExit() }
        $server.Dispose()
    }
}
