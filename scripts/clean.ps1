# FilePath    : pbi-utils\scripts\clean.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 验证固定生成目录的路径和链接边界, 支持零删除清理预览.

<# .SYNOPSIS 整体验证后仅清理固定生成目录; WhatIf 使用相同计划且零删除. #>
if ($MyInvocation.InvocationName -ne '.' -or !(Test-Path Variable:PbiInvocation)) {
    Import-Module (Join-Path $PSScriptRoot 'CommandRegistry.psm1') -Force
    exit (Invoke-PbiEntry -Arguments $args -Action clean)
}
$InternalOptions = $PbiInvocation.Options
$InternalAction = $PbiInvocation.Action
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (!(Test-Path -LiteralPath (Join-Path $root 'AGENTS.md')) -or !(Test-Path -LiteralPath (Join-Path $root 'pbi-utils.slnx')) -or !(Test-Path -LiteralPath (Join-Path $root 'Directory.Build.props'))) { Stop-Pbi 4 '仓库标记缺失, 拒绝清理' }

<# .SYNOPSIS 检查精确路径及全部父级, 拒绝任何 reparse point 或越界. #>
function Assert-SafePath([string]$Target) {
    $full = [IO.Path]::GetFullPath($Target)
    $comparison = [StringComparison]::OrdinalIgnoreCase
    if (!$full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison)) { Stop-Pbi 4 '清理目标越界' }
    $current = $full
    while ($current) {
        $pathErrors = @()
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue -ErrorVariable pathErrors
        if (@($pathErrors | Where-Object { $_.CategoryInfo.Category -ne 'ObjectNotFound' }).Count) { Stop-Pbi 4 '无法验证清理路径, 拒绝整个计划' }
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Stop-Pbi 4 '清理路径包含链接, 拒绝整个计划' }
        $parent = [IO.Directory]::GetParent($current)
        $current = if ($parent) { $parent.FullName } else { $null }
    }
}

<# .SYNOPSIS 不跟随链接逐层验证待删除子树, 无法枚举时终止. #>
function Assert-SafeTree([string]$Target) {
    Assert-SafePath $Target
    if (!(Test-Path -LiteralPath $Target)) { return }
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Target)
    while ($queue.Count) {
        $path = $queue.Dequeue()
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Stop-Pbi 4 '子树包含链接, 拒绝整个计划' }
        if ($item.PSIsContainer) {
            foreach ($child in (Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop)) { $queue.Enqueue($child.FullName) }
        }
    }
}

$relative = @('artifacts', 'web/dist', 'web/coverage')
foreach ($project in (Get-PbiProjects All)) { foreach ($suffix in @('bin', 'obj', 'TestResults')) { $relative += "$project/$suffix" } }
$plan = @($relative | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $root $_)) })
foreach ($target in $plan) { Assert-SafeTree $target }
foreach ($target in $plan) {
    if (Test-Path -LiteralPath $target) {
        Write-Host ([IO.Path]::GetRelativePath($root, $target))
        if (!$InternalOptions.WhatIf) { Assert-SafeTree $target; Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }
    }
}
