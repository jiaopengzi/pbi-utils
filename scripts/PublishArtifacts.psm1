# FilePath    : pbi-utils\scripts\PublishArtifacts.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 共享发布路径、精确清单和原目录文件覆盖, 失败时按本次修改恢复文件.

Set-StrictMode -Version Latest
# 模块有独立错误偏好, 不继承调度模块的 Stop; 枚举/读取失败不得生成部分清单.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PublishRuntime.psm1')

<# .SYNOPSIS 不跟随发布路径或父级的重解析点. #>
function Assert-PbiPublishPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    if ($current.StartsWith('\\')) { throw '发布只允许本机目录' }
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '发布路径包含链接或重解析点' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

<# .SYNOPSIS 逐级检查后枚举真实文件, 避免先递归跟随链接. #>
function Get-PbiPublishFiles([string]$Root) {
    Assert-PbiPublishPath $Root
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop) {
        Assert-PbiPublishPath $entry.FullName
        if ($entry.PSIsContainer) { Get-PbiPublishFiles $entry.FullName } else { $entry }
    }
}

<#
.SYNOPSIS 把本轮前端完整装入构建临时目录, 不保留上一轮的资源.
.PARAMETER Source 已完成构建的 web/dist 本机目录, 必须含 index.html.
.PARAMETER Staging 本任务的共享发布临时目录, 只能写入其 web 子目录.
.NOTES 复制前拒绝整树链接, 缺少入口或路径越界时失败, 旧发布目录不受影响.
#>
function Set-PbiPublishedFrontend([string]$Source, [string]$Staging) {
    $absoluteStaging = [IO.Path]::GetFullPath($Staging)
    if ([IO.Path]::GetFileName($absoluteStaging) -notmatch '^\.pbi-utils-publish-[0-9a-f]{32}$') { throw '前端打包仅允许本任务的发布临时目录' }
    Assert-PbiPublishPath $absoluteStaging
    $sourceFiles = @(Get-PbiPublishFiles $Source)
    if (!(Test-Path -LiteralPath (Join-Path $Source 'index.html') -PathType Leaf)) { throw '前端构建缺少 web/dist/index.html, 未交付本轮产物' }
    $destination = Join-Path $absoluteStaging 'web'
    Assert-PbiPublishPath $destination
    if ([IO.Directory]::Exists($destination)) {
        $null = @(Get-PbiPublishFiles $destination)
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $destination -Force
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Source), $file.FullName)
        $target = Join-Path $destination $relative
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

<#
.SYNOPSIS 校验共享布局、部署模式、实际运行描述和精确集合; manifest 本身不循环哈希.
.PARAMETER Root 本轮 staging 或固定最终发布根目录.
.OUTPUTS 通过 schema、文件集合与 SHA256 校验的清单; 任一契约无效或读取失败时抛出错误.
#>
function Assert-PbiPublishedArtifacts([string]$Root) {
    $absolute = [IO.Path]::GetFullPath($Root)
    $actualFiles = @(Get-PbiPublishFiles $absolute)
    $manifest = Get-Content -LiteralPath (Join-Path $absolute 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 3 -or $manifest.layout -cne 'shared' -or $manifest.runtime -cne 'win-x64' -or $manifest.scope -cnotin @('Core', 'All') -or
        $manifest.configuration -cnotin @('Debug', 'Release') -or $manifest.deploymentMode -cnotin @('self-contained', 'framework-dependent') -or !$manifest.version) { throw '发布清单版本、布局、目标、范围、配置或部署模式无效' }
    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $manifest.files) {
        $relative = $item.file
        if ($relative -isnot [string] -or !$relative -or $relative.Contains('\') -or $relative.Contains(':') -or $relative.StartsWith('/') -or $relative.Split('/') -contains '..' -or $relative.Split('/') -contains '.' -or $relative.Split('/') -contains '' -or $relative -eq 'manifest.json') { throw '发布清单路径无效' }
        if ($relative.StartsWith('cli/', [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('host/', [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('desktop/', [StringComparison]::OrdinalIgnoreCase)) { throw '发布清单包含旧版独立目录' }
        if ($manifest.scope -eq 'Core' -and ($relative.StartsWith('web/', [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('PbiUtils.Desktop.', [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($relative).StartsWith('Microsoft.Web.WebView2.', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($relative) -ieq 'WebView2Loader.dll')) { throw '发布清单包含范围外文件' }
        if ($item.sha256 -isnot [string] -or $item.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or !$expected.TryAdd($relative, $item.sha256)) { throw '发布清单哈希无效或路径重复' }
    }
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $actualFiles) {
        $relative = [IO.Path]::GetRelativePath($absolute, $file.FullName).Replace('\', '/')
        if ($relative -ne 'manifest.json' -and !$actual.Add($relative)) { throw '发布文件路径重复' }
    }
    if (!$actual.SetEquals($expected.Keys)) { throw '发布文件集合与清单不一致 (额外或缺失文件)' }
    $required = @('pbi-utils.exe', 'pbi-utils.dll', 'pbi-utils.deps.json', 'pbi-utils.runtimeconfig.json', 'PbiUtils.Host.exe', 'PbiUtils.Host.dll', 'PbiUtils.Host.deps.json', 'PbiUtils.Host.runtimeconfig.json')
    if ($manifest.deploymentMode -ceq 'self-contained') { $required += @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll') }
    else {
        foreach ($file in $actualFiles) {
            if ($file.Name -iin (Get-PbiSelfContainedRuntimeFiles)) { throw "框架依赖发布不能包含自包含运行时文件: $($file.Name)" }
        }
    }
    if ($manifest.scope -eq 'All') { $required += @('PbiUtils.Desktop.exe', 'PbiUtils.Desktop.dll', 'PbiUtils.Desktop.deps.json', 'PbiUtils.Desktop.runtimeconfig.json', 'web/index.html') }
    foreach ($file in $required) { if (!$expected.ContainsKey($file)) { throw "发布必需文件缺失: $file" } }
    foreach ($file in $actualFiles) {
        $relative = [IO.Path]::GetRelativePath($absolute, $file.FullName).Replace('\', '/')
        if ($relative -ne 'manifest.json' -and (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected[$relative]) { throw "发布清单哈希不匹配: $relative" }
    }
    $entries = @('pbi-utils', 'PbiUtils.Host')
    if ($manifest.scope -ceq 'All') { $entries += 'PbiUtils.Desktop' }
    $frameworkIdentity = $null
    $descriptions = @{}; $assetOwners = @{}
    foreach ($entry in $entries) {
        $description = Read-PbiDeploymentDescription $absolute $entry $manifest.deploymentMode
        if ($description.Frameworks.Count -ne 2) { throw '共享发布入口必须声明相同的两个框架' }
        $identity = $description.Target + '|' + $description.Config.runtimeOptions.tfm + '|' + (($description.Frameworks | Sort-Object name | ForEach-Object { $_.name + '/' + $_.version }) -join '|')
        if ($null -ne $frameworkIdentity -and $identity -cne $frameworkIdentity) { throw '共享发布入口框架集合或版本不一致' }
        $frameworkIdentity = $identity
        if ($manifest.deploymentMode -ceq 'self-contained') { Assert-PbiSelfContainedFrameworks $description }
        $descriptions[$entry] = $description
        $assetOwners[$entry] = Get-PbiDeploymentAssetOwners $description
    }
    Assert-PbiSharedDeploymentDependencies $descriptions $assetOwners
    return $manifest
}

<# .SYNOPSIS 仅删除本任务创建并已检查整树无链接的同父目录临时目录. #>
function Remove-PbiPublishTemporary([string]$Path, [string]$Parent) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($absolute) -ne [IO.Path]::GetFullPath($Parent) -or [IO.Path]::GetFileName($absolute) -notmatch '^\.pbi-utils-(publish|previous)-[0-9a-f]{32}$') { throw '发布临时清理路径越界' }
    if ([IO.Directory]::Exists($absolute)) {
        $null = @(Get-PbiPublishFiles $absolute)
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}

<#
.SYNOPSIS 校验完整 staging 后按内容差异在原目录覆盖文件, 清理过期文件, 不重命名发布目录.
.PARAMETER Staging 本轮已生成清单的同父临时目录.
.PARAMETER Destination 固定发布目录, 允许资源管理器持有目录句柄, 拒绝文件占用和链接.
.NOTES 全部旧文件仍检查占用; 仅备份变化和过期文件, 清单最后覆盖并完整校验. 捕获覆盖失败后恢复已触及文件; 恢复失败保留备份并明确报错, 不保证多文件崩溃原子性. 单独清理失败也抛错, 与主操作同时失败时保留首个错误.
.OUTPUTS 无返回值; 路径、占用、内容校验或覆盖失败时抛出错误.
#>
function Set-PbiPublishedArtifacts([string]$Staging, [string]$Destination) {
    $Staging = [IO.Path]::GetFullPath($Staging)
    $Destination = [IO.Path]::GetFullPath($Destination)
    $manifest = Assert-PbiPublishedArtifacts $Staging
    Assert-PbiPublishPath $Destination
    $parent = [IO.Path]::GetDirectoryName($Destination)
    if ([IO.Path]::GetDirectoryName($Staging) -ne $parent -or [IO.Path]::GetFileName($Staging) -notmatch '^\.pbi-utils-publish-[0-9a-f]{32}$' -or $Staging -eq $Destination) { throw '发布 staging 必须为目标同父目录下的独立临时目录' }
    $previous = Join-Path $parent ('.pbi-utils-previous-' + [Guid]::NewGuid().ToString('N'))
    $gatePath = Join-Path $parent '.pbi-utils-publish.lock'
    Assert-PbiPublishPath $gatePath
    try { $gate = [IO.File]::Open($gatePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw '另一构建正在交付发布目录; 已保留原产物, 请稍后重试' }
    $leases = [Collections.Generic.List[IO.FileStream]]::new()
    $keepPrevious = $false
    $operationFailure = $null
    try {
        $oldFiles = @()
        if ([IO.Directory]::Exists($Destination)) {
            $oldFiles = @(Get-PbiPublishFiles $Destination)
            foreach ($file in $oldFiles) {
                try { $leases.Add([IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Delete)) }
                catch { throw '发布目录被占用或不可写; 已保留原产物, 请关闭应用后重试' }
            }
            # 完成文件占用检查后释放, 后续只覆盖文件, 保留发布根和所有子目录身份.
            foreach ($lease in $leases) { $lease.Dispose() }
            $leases.Clear()
        } elseif ([IO.File]::Exists($Destination)) { throw '发布目标不是目录' }

        $newFiles = @(Get-PbiPublishFiles $Staging | Where-Object FullName -ne (Join-Path $Staging 'manifest.json') | Sort-Object FullName)
        $newFiles += Get-Item -LiteralPath (Join-Path $Staging 'manifest.json')
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $newFiles) {
            $relative = [IO.Path]::GetRelativePath($Staging, $file.FullName)
            $null = $expected.Add($relative)
            $target = Join-Path $Destination $relative
            Assert-PbiPublishPath $target
            if ([IO.Directory]::Exists($target)) { throw "发布文件与现有目录冲突, 已保留原产物: $relative" }
            $ancestor = [IO.Path]::GetDirectoryName($target)
            while ($ancestor -ne $Destination) {
                if ([IO.File]::Exists($ancestor)) { throw "发布目录与现有文件冲突, 已保留原产物: $relative" }
                $ancestor = [IO.Path]::GetDirectoryName($ancestor)
            }
        }
        # 只信任已校验的 staging 清单, 与目标真实内容比较, 不依赖旧清单或时间戳.
        $hashes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in $manifest.files) { $hashes.Add($item.file.Replace('/', [IO.Path]::DirectorySeparatorChar), $item.sha256) }
        $changed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $newFiles) {
            $relative = [IO.Path]::GetRelativePath($Staging, $file.FullName)
            $target = Join-Path $Destination $relative
            if ($relative -eq 'manifest.json' -or ![IO.File]::Exists($target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction Stop).Hash -ne $hashes[$relative]) {
                $null = $changed.Add($relative)
            }
        }
        $originals = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        Assert-PbiPublishPath $previous
        foreach ($file in $oldFiles) {
            $relative = [IO.Path]::GetRelativePath($Destination, $file.FullName)
            # 未变化文件仍经过完整占用检查和最终校验, 但不备份或覆盖.
            if ($expected.Contains($relative) -and !$changed.Contains($relative)) { continue }
            $null = $originals.Add($relative)
            $backup = Join-Path $previous $relative
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $backup -ErrorAction Stop
        }
        $touched = [Collections.Generic.List[string]]::new()
        try {
            $null = New-Item -ItemType Directory -Path $Destination -Force
            # 清单最后覆盖, 只有全部文件覆盖和过期文件删除完成后才公布本轮清单.
            foreach ($file in $newFiles) {
                $relative = [IO.Path]::GetRelativePath($Staging, $file.FullName)
                if (!$changed.Contains($relative)) { continue }
                $target = Join-Path $Destination $relative
                Assert-PbiPublishPath $target
                $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
                if ($relative -eq 'manifest.json') {
                    foreach ($oldFile in $oldFiles) {
                        $oldRelative = [IO.Path]::GetRelativePath($Destination, $oldFile.FullName)
                        if (!$expected.Contains($oldRelative)) {
                            Assert-PbiPublishPath $oldFile.FullName
                            $touched.Add($oldRelative)
                            Remove-Item -LiteralPath $oldFile.FullName -ErrorAction Stop
                        }
                    }
                }
                $touched.Add($relative)
                Copy-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop
            }
            $null = Assert-PbiPublishedArtifacts $Destination
        }
        catch {
            $failure = $_
            for ($index = $touched.Count - 1; $index -ge 0; $index--) {
                $relative = $touched[$index]
                $target = Join-Path $Destination $relative
                try {
                    Assert-PbiPublishPath $target
                    if ($originals.Contains($relative)) { Copy-Item -LiteralPath (Join-Path $previous $relative) -Destination $target -ErrorAction Stop }
                    elseif ([IO.File]::Exists($target)) { Remove-Item -LiteralPath $target -ErrorAction Stop }
                }
                catch { $keepPrevious = $true }
            }
            if ($keepPrevious) { throw "发布文件覆盖失败且恢复未完成; 不应使用当前产物, 旧文件备份保留于: $previous. 原因: $($failure.Exception.Message)" }
            throw "发布文件覆盖失败, 已恢复本轮修改的文件; 请检查文件占用或权限后重试. 原因: $($failure.Exception.Message)"
        }
    }
    catch {
        $operationFailure = $_
        throw
    }
    finally {
        foreach ($lease in $leases) { $lease.Dispose() }
        $gate.Dispose()
        if (!$keepPrevious) {
            try { Remove-PbiPublishTemporary $previous $parent }
            catch {
                $cleanupMessage = "发布备份清理失败, 保留于: $previous. 原因: $($_.Exception.Message)"
                if ($null -eq $operationFailure) { throw "发布文件已交付, 但$cleanupMessage" }
                # 主操作已失败, 补充清理原因但不替换原始阶段和退出码.
                [Console]::Error.WriteLine($cleanupMessage)
            }
        }
    }
}

Export-ModuleMember -Function *-Pbi*
