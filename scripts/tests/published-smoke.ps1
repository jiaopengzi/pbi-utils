# FilePath    : pbi-utils\scripts\tests\published-smoke.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 验证两种发布模式的文件哈希、运行时来源与 CLI/MCP 命令, 保存独立证据.

<#
.SYNOPSIS
验证已生成的发布目录与 CLI/MCP, 不重新发布或修改机器环境.
.DESCRIPTION
从仓库根定位产物, 用移除开发工具路径的子进程运行只读诊断、模板目录与帮助/版本命令.
结果按配置和发布模式写入 artifacts/evidence 的本轮唯一目录. 失败抛出错误; 不执行桌面 UI 或断网操作.
.PARAMETER Configuration
默认 Release 验证 without-dotnet; Debug 验证当前开发目录中的实际模式.
.PARAMETER DotNetMode
选择 Release 发布目录, 默认 without-dotnet; Debug 以目录中的清单为准.
.PARAMETER PublishRoot
显式验证一个发布目录, 以其 manifest 的配置和模式为准.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('with-dotnet', 'without-dotnet')][string]$DotNetMode = 'without-dotnet',
    [string]$PublishRoot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $root 'scripts/PublishArtifacts.psm1') -Force
. (Join-Path $PSScriptRoot 'PublishedRuntimeSmoke.ps1')
$publishRoots = if ($PublishRoot) { @([IO.Path]::GetFullPath($PublishRoot)) }
elseif ($Configuration -eq 'Debug') { @(Join-Path $root 'artifacts/build/debug/win-x64') }
else { @(Join-Path $root "artifacts/build/release/win-x64/$($DotNetMode.ToLowerInvariant())") }
$runEvidence = Join-Path $root ('artifacts/evidence/published-smoke-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
foreach ($packageRoot in $publishRoots) {
$manifest = Assert-PbiPublishedArtifacts $packageRoot
$modeDirectory = if ($manifest.deploymentMode -ceq 'self-contained') { 'with-dotnet' } else { 'without-dotnet' }
$evidenceRoot = Join-Path $runEvidence ($manifest.configuration.ToLowerInvariant() + '/' + $modeDirectory)
$null = New-Item -ItemType Directory -Path $evidenceRoot -Force
$sharedAssets = @()
if ($manifest.deploymentMode -ceq 'self-contained') {
    $cliDeps = Get-Content -LiteralPath (Join-Path $packageRoot 'pbi-utils.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $cliConfig = Get-Content -LiteralPath (Join-Path $packageRoot 'pbi-utils.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $hostConfig = Get-Content -LiteralPath (Join-Path $packageRoot 'PbiUtils.Host.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $frameworks = @($cliConfig.runtimeOptions.includedFrameworks)
    $windows = @($frameworks | Where-Object name -CEQ 'Microsoft.WindowsDesktop.App')
    if ($frameworks.Count -ne 2 -or $windows.Count -ne 1 -or
        ($frameworks | ConvertTo-Json -Compress) -cne ($hostConfig.runtimeOptions.includedFrameworks | ConvertTo-Json -Compress)) { throw 'CLI 与业务后台的框架集合不一致' }
    if ($manifest.scope -eq 'All') {
        $desktopConfig = Get-Content -LiteralPath (Join-Path $packageRoot 'PbiUtils.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
        if (($frameworks | ConvertTo-Json -Compress) -cne ($desktopConfig.runtimeOptions.includedFrameworks | ConvertTo-Json -Compress)) { throw '桌面与业务后台的框架集合不一致' }
    }
    $target = $cliDeps.targets[$cliDeps.runtimeTarget.name]
    if (@($target.Keys | Where-Object { $_ -like 'System.Security.Cryptography.ProtectedData/*' -or $_ -like 'PbiUtils.Desktop/*' -or $_ -like 'PbiUtils.Windows/*' }).Count) { throw '完整包 CLI 描述残留被接管的包或引入桌面产品依赖' }
    foreach ($name in @('Microsoft.VisualBasic.dll', 'System.Drawing.dll', 'WindowsBase.dll', 'System.Security.Cryptography.ProtectedData.dll')) {
        $path = Join-Path $packageRoot $name
        $owners = @(foreach ($library in $target.GetEnumerator()) {
            if (!$library.Value.Contains('runtime')) { continue }
            foreach ($asset in $library.Value.runtime.GetEnumerator()) {
                if ([IO.Path]::GetFileName($asset.Key) -ceq $name) { @{ Key = $library.Key; Metadata = $asset.Value } }
            }
        })
        if ($owners.Count -ne 1 -or $owners[0].Key -cnotmatch '^runtimepack\.Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64/') { throw "共享资产缺少唯一框架拥有者: $name" }
        $frameworkKey = $owners[0].Key
        $metadata = $owners[0].Metadata
        $assembly = [Reflection.AssemblyName]::GetAssemblyName($path)
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
        if ($assembly.Version -ne [Version]$metadata.assemblyVersion -or [Version]$fileVersion -ne [Version]$metadata.fileVersion) { throw "共享框架资产实际版本与 CLI 描述不一致: $name" }
        $sharedAssets += @{ file = $name; assemblyVersion = $assembly.Version.ToString(); fileVersion = $fileVersion; owner = $frameworkKey }
    }
}
$results = @()
$cases = @(
    @{ name = 'business-host'; arguments = @('--invalid-smoke-argument'); command = 'business-host startup'; format = 'host'; executable = 'PbiUtils.Host.exe'; expectedExitCode = 2 }
    @{ name = 'doctor'; arguments = @('doctor', '--json'); command = 'doctor'; format = 'json' }
    @{ name = 'templates'; arguments = @('new', 'templates', '--json'); command = 'new templates'; format = 'json' }
    @{ name = 'help'; arguments = @('--help', '--version', '--json'); command = '--help'; format = 'help' }
    @{ name = 'version'; arguments = @('--version', '--json'); command = '--version'; format = 'version' }
)
foreach ($case in $cases) {
    $executable = if ($case.ContainsKey('executable')) { $case.executable } else { 'pbi-utils.exe' }
    $expectedExitCode = if ($case.ContainsKey('expectedExitCode')) { $case.expectedExitCode } else { 0 }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $packageRoot $executable))
    # 从发布目录以外启动, 验证资源定位不依赖调用方的当前工作目录.
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    # 只改子进程环境; 每条真实入口分别证明包内或系统共享运行时来源.
    $tracePath = Join-Path $evidenceRoot "published-$($case.name)-host-trace.txt"
    Set-PbiSmokeRuntimeEnvironment $start $manifest.deploymentMode $tracePath
    foreach ($argument in $case.arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $caseFailure = $null
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { throw '发布 CLI 超时' }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        $extension = if ($case.format -eq 'json') { 'json' } else { 'txt' }
        Set-Content -LiteralPath (Join-Path $evidenceRoot "published-$($case.name).$extension") -Value $output -Encoding utf8
        Set-Content -LiteralPath (Join-Path $evidenceRoot "published-$($case.name).stderr.txt") -Value $errors -Encoding utf8
        if ($process.ExitCode -ne $expectedExitCode) { throw "发布入口返回非预期退出码: $($case.name), 实际=$($process.ExitCode), 模式=$modeDirectory; 详情见 $evidenceRoot" }
        $runtimeResolution = Assert-PbiSmokeRuntimeResolution $tracePath $packageRoot $manifest.deploymentMode @($sharedAssets | ForEach-Object file)
        if ($case.format -eq 'host') {
            # 无效固定参数在上下文创建前返回, 证明后台入口与实际依赖可解析, 不触碰用户数据.
            if ($output -or $errors) { throw '后台启动参数拒绝不应输出业务数据' }
        }
        elseif ($case.format -eq 'json') {
            $data = $output | ConvertFrom-Json
            if (!$data.ok -or $data.schemaVersion -ne 1 -or $data.command -ne $case.command) { throw '发布 CLI JSON 不符合契约' }
            if ($case.name -eq 'doctor' -and ($data.data.version -ne $manifest.version -or !$data.data.environment.runtime)) { throw '发布诊断缺少产品版本或运行时信息' }
            if ($case.name -eq 'templates') {
                $templateIds = @($data.data.templates | ForEach-Object { $_.id })
                foreach ($id in @('builtin:blank')) {
                    if ($templateIds -notcontains $id) { throw '发布模板目录缺少内置模板' }
                }
            }
        }
        elseif ($case.format -eq 'help') {
            if (!$output.Contains('pbi-utils doctor') -or !$output.Contains('pbi-utils new templates') -or $output.Contains('pbi-utils hello') -or $output.Contains('pbi-utils agent hello')) { throw '发布帮助不符合当前命令契约' }
        }
        elseif ($output.Trim() -cne $manifest.version) { throw '发布版本与清单不一致' }
        $results += @{ command = $case.command; status = 'PASS'; exitCode = $process.ExitCode; developmentToolsRemovedFromPath = $true; runtimeResolution = $runtimeResolution }
    }
    catch {
        $caseFailure = $_
        throw
    }
    finally {
        try {
            if (!$process.HasExited) {
                # 本轮只有只读 CLI 或参数拒绝探针; 仅结束准确自身, Host 经断开自行排空.
                $process.Kill()
                if (!$process.WaitForExit(10000)) { throw '只读发布入口清理超时; 未结束共享后台' }
            }
        }
        catch {
            if ($null -eq $caseFailure) { throw }
            [Console]::Error.WriteLine('发布入口清理失败, 保留原验收错误: ' + $_.Exception.Message)
        }
        finally { $process.Dispose(); $process = $null }
    }
}
& (Join-Path $PSScriptRoot 'mcp-published-smoke.ps1') -PublishRoot $packageRoot -EvidenceRoot $evidenceRoot
@{ platform = 'Windows x64'; version = $manifest.version; scope = $manifest.scope; configuration = $manifest.configuration; package = $modeDirectory; deploymentMode = $manifest.deploymentMode; layout = $manifest.layout; exactFileSetVerified = $true; manifestFilesVerified = $manifest.files.Count; sharedFrameworkAssets = $sharedAssets; runtimeResolutionVerified = $true; commands = $results; mcp = 'PASS'; isolatedCleanMachine = 'UNRUN'; physicallyOfflineMachine = 'UNRUN'; desktopInteraction = 'UNRUN' } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'published-smoke.json') -Encoding utf8
Write-Output "PASS $($manifest.configuration)/$modeDirectory CLI/MCP、运行时来源与发布文件清单: $evidenceRoot; 不等同干净机器或断网验收."
}
