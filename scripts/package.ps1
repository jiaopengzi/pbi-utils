# FilePath    : pbi-utils\scripts\package.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 从已验证的 Release 产物和当前许可来源生成 NSIS 安装包, 不执行安装.

<# .SYNOPSIS 使用本机 makensis 打包选定 Release/All 目录, 元数据或编译失败保留原安装包. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    exit (Invoke-PbiEntry -Arguments $args -Action package)
}
$ErrorActionPreference = 'Stop'
$options = $PbiInvocation.Options
$repository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Import-Module (Join-Path $PSScriptRoot 'PublishArtifacts.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'InstallerMetadata.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ThirdPartyLicenses.psm1') -Force
$source = Join-Path $repository "artifacts/build/release/win-x64/$($options.DotNetMode)"
$manifest = Assert-PbiPublishedArtifacts $source
$null = Assert-PbiLicenseBundle -PublishRoot $source
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$sdkVersion = [string](Get-Content -LiteralPath (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json).sdk.version
$mode = if ($options.DotNetMode -eq 'with-dotnet') { 'self-contained' } else { 'framework-dependent' }
if ($manifest.configuration -cne 'Release' -or $manifest.scope -cne 'All' -or $manifest.deploymentMode -cne $mode -or $manifest.version -cne $version -or !$manifest.ContainsKey('sdkVersion') -or $manifest.sdkVersion -cne $sdkVersion) {
    Stop-Pbi 3 '安装包需要与当前产品/SDK 版本一致的 Release/All 所选模式产物; 请先运行 release 或对应 all 命令'
}
if ($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or @($version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count) {
    Stop-Pbi 3 '安装包版本必须为三段或四段数字, 每段不超过 65535'
}
$fileVersion = if ($version.Split('.').Count -eq 3) { "$version.0" } else { $version }
$destination = Join-Path $repository "artifacts/release/win-x64/$($options.DotNetMode)"
Assert-PbiPublishPath $destination
$null = New-Item -ItemType Directory -Path $destination -Force
$stage = Join-Path $destination ('.pbi-utils-installer-' + [Guid]::NewGuid().ToString('N'))
$baseName = "pbi-utils-$version-win-x64-$($options.DotNetMode)-setup"
$gate = $null; $failure = $null; $keepStage = $false
$delivered = [Collections.Generic.List[string]]::new()
$previous = @{}
try {
    $gatePath = Join-Path $destination '.package.lock'
    Assert-PbiPublishPath $gatePath
    try { $gate = [IO.File]::Open($gatePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { Stop-Pbi 3 '另一任务正在生成此模式安装包, 请稍后重试' }
    $payload = Join-Path $stage 'payload'
    $app = Join-Path $payload 'app'
    $maintenance = Join-Path $payload 'maintenance'
    $null = New-Item -ItemType Directory -Path $app, $maintenance -Force
    foreach ($file in Get-PbiPublishFiles $source) {
        $target = Join-Path $app ([IO.Path]::GetRelativePath($source, $file.FullName))
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    # 校验复制后的独立快照; 不让并发构建产生新旧混合载荷.
    $snapshotManifest = Assert-PbiPublishedArtifacts $app
    $null = Assert-PbiLicenseBundle -PublishRoot $app
    if ($snapshotManifest.configuration -cne 'Release' -or $snapshotManifest.scope -cne 'All' -or $snapshotManifest.deploymentMode -cne $mode -or $snapshotManifest.version -cne $version -or !$snapshotManifest.ContainsKey('sdkVersion') -or $snapshotManifest.sdkVersion -cne $sdkVersion) {
        throw '复制期间发布产物已改变配置、模式或版本, 未生成安装包; 请重试'
    }
    $inputManifestHash = (Get-FileHash -LiteralPath (Join-Path $app 'manifest.json') -Algorithm SHA256).Hash
    $builtLicenses = Get-Content -LiteralPath (Join-Path $app 'licenses/manifest.json') -Raw | ConvertFrom-Json
    # 仅刷新许可, 不把当前前端依赖的声明套到旧构建的 JavaScript 上.
    $licenseInputs = @{}
    foreach ($relative in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/catalog.json', 'web/package.json', 'web/pnpm-lock.yaml')) {
        $inputPath = Join-Path $repository $relative
        Assert-PbiPublishPath $inputPath
        $licenseInputs[$inputPath] = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
        if ($relative.StartsWith('web/', [StringComparison]::Ordinal)) {
            $builtInput = Join-Path $app ('licenses/inputs/' + [IO.Path]::GetFileName($relative))
            if ((Get-FileHash -LiteralPath $builtInput -Algorithm SHA256).Hash -cne $licenseInputs[$inputPath]) {
                Stop-Pbi 3 "前端依赖输入已改变: $relative; 请先重新构建对应 Release/All 产物再打包"
            }
        }
    }
    $catalog = Get-Content -LiteralPath (Join-Path $repository 'licenses/catalog.json') -Raw | ConvertFrom-Json
    $builtCatalog = Get-Content -LiteralPath (Join-Path $app 'licenses/inputs/catalog.json') -Raw | ConvertFrom-Json
    # 字体、图标及内嵌源码仍来自旧构建; 只允许同身份的许可修订. NSIS 是本轮实际编译输入.
    $currentResources = @($catalog.resources | Where-Object name -CNE 'NSIS-COPYING' | ForEach-Object { "$($_.scope)|$($_.name)|$($_.version)" } | Sort-Object)
    $builtResources = @($builtCatalog.resources | Where-Object name -CNE 'NSIS-COPYING' | ForEach-Object { "$($_.scope)|$($_.name)|$($_.version)" } | Sort-Object)
    if (($currentResources -join "`n") -cne ($builtResources -join "`n")) {
        Stop-Pbi 3 '内嵌第三方资源身份已改变; 请先重新构建对应 Release/All 产物再打包'
    }
    foreach ($entry in @($catalog.packages) + @($catalog.resources)) {
        foreach ($document in $entry.files) {
            $inputPath = [IO.Path]::GetFullPath((Join-Path $repository $document.path))
            if (!$inputPath.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '许可来源路径越界' }
            Assert-PbiPublishPath $inputPath
            $licenseInputs[$inputPath] = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
        }
    }
    # 编译前后按内容复核本轮许可来源, 不用 mtime 缓存, 也不交付并发修改后的混合版本.
    $assertLicenseInputs = {
        foreach ($inputPath in $licenseInputs.Keys) {
            Assert-PbiPublishPath $inputPath
            if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -cne $licenseInputs[$inputPath]) { throw "打包期间许可来源已改变: $inputPath; 请重新打包" }
        }
    }
    $licenseOutput = [IO.Path]::GetFullPath((Join-Path $app 'licenses'))
    if ([IO.Path]::GetDirectoryName($licenseOutput) -cne [IO.Path]::GetFullPath($app) -or
        !$licenseOutput.StartsWith([IO.Path]::GetFullPath($stage) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '安装许可临时目录越界' }
    $null = @(Get-PbiPublishFiles $licenseOutput)
    Remove-Item -LiteralPath $licenseOutput -Recurse -Force
    $currentLicenses = Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot $app -Scope All
    # 声明及锁文件相同也可能缺少已安装 optional/peer 依赖, 不能丢掉旧前端实际闭包的许可.
    $currentNpm = @($currentLicenses.components | Where-Object ecosystem -CEQ 'npm' | ForEach-Object { "$($_.name)/$($_.version)" } | Sort-Object)
    $builtNpm = @($builtLicenses.components | Where-Object ecosystem -CEQ 'npm' | ForEach-Object { "$($_.name)/$($_.version)" } | Sort-Object)
    if (($currentNpm -join "`n") -cne ($builtNpm -join "`n")) { Stop-Pbi 3 '前端实际依赖闭包已改变; 请先重新构建对应 Release/All 产物再打包' }
    & $assertLicenseInputs
    # 原始发布目录保持不变; 仅更新私有载荷清单中的文件身份, 保留原二进制构建元数据.
    $snapshotManifest.files = @(Get-PbiPublishFiles $app | Where-Object FullName -NE (Join-Path $app 'manifest.json') | Sort-Object FullName | ForEach-Object {
        @{ file = [IO.Path]::GetRelativePath($app, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $snapshotManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $app 'manifest.json') -Encoding utf8
    $null = Assert-PbiPublishedArtifacts $app
    $null = Assert-PbiLicenseBundle -PublishRoot $app
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'installer') -File) {
        if ($file.Extension -notin @('.ps1', '.psm1')) { continue }
        Assert-PbiPublishPath $file.FullName
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $maintenance $file.Name)
    }
    foreach ($required in @('Install-Application.ps1', 'Installation.psm1', 'InstallationPayload.psm1', 'InstallationShell.psm1', 'ExternalToolRegistration.psm1', 'ExternalToolDescriptor.psm1', 'ExternalToolFiles.psm1', 'ExternalTool-Worker.ps1', 'Ensure-Prerequisites.ps1', 'Prerequisites.psm1', 'Prerequisite-Worker.ps1')) {
        if (!(Test-Path -LiteralPath (Join-Path $maintenance $required) -PathType Leaf)) { Stop-Pbi 3 "安装维护文件缺失: $required" }
    }
    $prerequisitesPath = Join-Path $maintenance 'prerequisites.json'
    $prerequisites = Export-PbiInstallerPrerequisites -RepositoryRoot $repository -PublishRoot $app -OutputPath $prerequisitesPath
    if ($prerequisites.dotnet.sdkVersion -cne $sdkVersion) { throw '生成安装依赖时 SDK 绑定发生变化, 未生成安装包; 请重试' }
    $installer = Join-Path $stage "$baseName.exe"
    # LicenseData 不按脚本 INPUTCHARSET 解码无 BOM 文本. 阅读副本显式标记 UTF-8, app/LICENSE 保留上游原始字节.
    $installerLicense = Join-Path $stage 'installer-license.txt'
    $licenseText = [IO.File]::ReadAllText((Join-Path $app 'LICENSE'), [Text.UTF8Encoding]::new($false, $true))
    [IO.File]::WriteAllText($installerLicense, $licenseText, [Text.UTF8Encoding]::new($true))
    $licenseManifest = Get-Content -LiteralPath (Join-Path $app 'licenses/manifest.json') -Raw | ConvertFrom-Json
    $nsisLicenses = @($licenseManifest.components | Where-Object { $_.ecosystem -ceq 'resource' -and $_.name -ceq 'NSIS-COPYING' })
    if ($nsisLicenses.Count -ne 1 -or $nsisLicenses[0].version -notmatch '^\d+\.\d+(?:\.\d+)?$') { throw '安装器缺少精确版本的 NSIS 分发许可' }
    # NSIS 在编译期核对自身版本, 防止开发工具升级后继续分发旧版本许可快照.
    $arguments = @('/V3', '/WX', '/INPUTCHARSET', 'UTF8', "/DPBI_NSIS_VERSION=$($nsisLicenses[0].version)", "/DPBI_PAYLOAD=$payload", "/DPBI_LICENSE_TEXT=$installerLicense", "/DPBI_OUTFILE=$installer", "/DPBI_VERSION=$version", "/DPBI_FILE_VERSION=$fileVersion", "/DPBI_ICON=$(Join-Path $repository 'web/public/branding/logo.ico')", (Join-Path $PSScriptRoot 'installer/installer.nsi'))
    Invoke-PbiNative makensis $arguments
    & $assertLicenseInputs
    if (!(Test-Path -LiteralPath $installer -PathType Leaf) -or (Get-Item -LiteralPath $installer).Length -le 0) { throw 'makensis 未生成有效安装包' }
    $installerBytes = (Get-Item -LiteralPath $installer).Length
    # 官方标记为 100M, 未明确进制, 使用十进制 100,000,000 字节保守判断.
    $giteeLimitBytes = 100000000L
    Write-Host ("Gitee 单附件 100M 保守阈值: $installerBytes / $giteeLimitBytes bytes; 在阈值内: " + ($installerBytes -le $giteeLimitBytes))
    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $packageRecord = [ordered]@{
        schemaVersion = 1; productId = 'pbi-utils-desktop'; version = $version; dotNetMode = $options.DotNetMode
        builtAtUtc = [DateTime]::UtcNow.ToString('O'); installer = "$baseName.exe"; sha256 = $installerHash
        bytes = $installerBytes
        giteeAttachmentLimitBytes = $giteeLimitBytes; fitsGiteeAttachmentLimit = ($installerBytes -le $giteeLimitBytes)
        inputManifestSha256 = $inputManifestHash
        sourceManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'manifest.json') -Algorithm SHA256).Hash
        prerequisites = Get-Content -LiteralPath $prerequisitesPath -Raw | ConvertFrom-Json
        signatureStatus = [string](Get-AuthenticodeSignature -LiteralPath $installer).Status
    }
    $packageRecord | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $stage "$baseName.json") -Encoding utf8
    "$installerHash  $baseName.exe" | Set-Content -LiteralPath (Join-Path $stage "$baseName.sha256") -Encoding ascii
    $names = @("$baseName.exe", "$baseName.json", "$baseName.sha256")
    # 只备份/替换本版本的三个已知文件; 不清理其他版本或目录中的用户文件.
    foreach ($artifactName in $names) {
        $target = Join-Path $destination $artifactName
        Assert-PbiPublishPath $target
        if (Test-Path -LiteralPath $target) {
            if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw '安装包目标被同名目录占用' }
            $backup = Join-Path $stage ("previous-$artifactName")
            Copy-Item -LiteralPath $target -Destination $backup
            $previous[$artifactName] = $backup
        }
    }
    foreach ($artifactName in $names) {
        # 先登记可能部分写入的目标, 写失败也回滚该项.
        $delivered.Add($artifactName)
        Copy-Item -LiteralPath (Join-Path $stage $artifactName) -Destination (Join-Path $destination $artifactName) -Force
    }
    if ((Get-FileHash -LiteralPath (Join-Path $destination "$baseName.exe") -Algorithm SHA256).Hash -cne $installerHash) { throw '安装包交付后的哈希核验失败' }
}
catch {
    $failure = $_
    foreach ($artifactName in $delivered) {
        try {
            $target = Join-Path $destination $artifactName
            if ($previous.ContainsKey($artifactName)) { Copy-Item -LiteralPath $previous[$artifactName] -Destination $target -Force }
            elseif (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
        }
        catch { $keepStage = $true; [Console]::Error.WriteLine("安装包恢复失败, 保留恢复材料于 $stage : $($_.Exception.Message)") }
    }
    throw
}
finally {
    try {
        if (!$keepStage -and [IO.Directory]::Exists($stage)) {
            $absolute = [IO.Path]::GetFullPath($stage)
            if ([IO.Path]::GetDirectoryName($absolute) -cne [IO.Path]::GetFullPath($destination) -or [IO.Path]::GetFileName($absolute) -notmatch '^\.pbi-utils-installer-[0-9a-f]{32}$') { throw '安装打包临时目录越界' }
            $null = @(Get-PbiPublishFiles $absolute)
            Remove-Item -LiteralPath $absolute -Recurse -Force
        }
    }
    catch {
        if ($null -eq $failure) {
            [Console]::Error.WriteLine("安装包已交付至 $destination, 但临时目录清理失败, 保留于 $stage")
            throw
        }
        [Console]::Error.WriteLine("安装包临时清理失败, 保留原错误: $($_.Exception.Message)")
    }
    finally { if ($null -ne $gate) { $gate.Dispose() } }
}
$finalInstaller = Join-Path $destination "$baseName.exe"
Write-PbiMessage ("安装包: {0}; {1:N2} MiB; SHA256={2}" -f $finalInstaller, ((Get-Item -LiteralPath $finalInstaller).Length / 1MB), $installerHash) 'Success'
Write-PbiMessage "签名状态: $($packageRecord.signatureStatus); 本命令只生成文件, 未执行安装或对外发布."
