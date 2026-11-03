# FilePath    : pbi-utils\scripts\tests\license-source-git-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 验证许可证来源哈希及 Git 暂存与检出时保留原始字节.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-license-git-' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $sandbox 'repository'
$ownsSandbox = $false
$savedGitEnvironment = @{}
foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GIT_*' })) {
    $savedGitEnvironment[$variable.Name] = $variable.Value
}

<# .SYNOPSIS 根据文件清单核对来源哈希; 参数为仓库根和路径到哈希的映射, 缺失或不匹配时抛错. #>
function Assert-CatalogHashes([string]$Repository, [hashtable]$Expected) {
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($relative in $Expected.Keys) {
        $path = Join-Path $Repository $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $failures.Add("missing: $relative"); continue }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $Expected[$relative]) { $failures.Add("hash mismatch: $relative") }
    }
    if ($failures.Count) { throw ($failures -join "`n") }
}

try {
    $catalog = Get-Content -LiteralPath (Join-Path $root 'licenses/catalog.json') -Raw | ConvertFrom-Json -AsHashtable
    $expected = @{}
    foreach ($entry in @($catalog.resources) + @($catalog.packages)) {
        foreach ($file in @($entry.files)) {
            if (!$file) { continue }
            $relative = $file.path
            if (!$relative -or $relative -notmatch '^[^\\:]+$' -or $relative.StartsWith('/') -or $relative -match '(^|/)\.\.?(/|$)' -or !$file.sha256) { throw "Invalid catalog file: $relative" }
            if ($expected.ContainsKey($relative) -and $expected[$relative] -cne $file.sha256) { throw "Conflicting catalog hashes: $relative" }
            $expected[$relative] = $file.sha256
        }
    }
    if (!$expected.Count) { throw 'No catalog license files found.' }
    Assert-CatalogHashes $root $expected

    $null = New-Item -ItemType Directory -Path $sandbox
    $ownsSandbox = $true
    $null = New-Item -ItemType Directory -Path $repository
    $template = Join-Path $sandbox 'empty-template'
    $null = New-Item -ItemType Directory -Path $template -Force
    # 清除继承的 Git 仓库、索引与配置重定向, 避免隔离测试触及外部仓库.
    foreach ($name in $savedGitEnvironment.Keys) { Remove-Item -LiteralPath "Env:$name" }
    $env:GIT_CONFIG_GLOBAL = Join-Path $sandbox 'empty-gitconfig'
    [IO.File]::WriteAllText($env:GIT_CONFIG_GLOBAL, '')
    $env:GIT_CONFIG_NOSYSTEM = '1'
    & git init --quiet "--template=$template" $repository
    if ($LASTEXITCODE -ne 0) { throw 'Failed to initialize isolated Git repository.' }
    Copy-Item -LiteralPath (Join-Path $root '.gitattributes') -Destination (Join-Path $repository '.gitattributes')
    $catalogDirectory = Join-Path $repository 'licenses'
    $null = New-Item -ItemType Directory -Path (Join-Path $catalogDirectory 'source') -Force
    Copy-Item -LiteralPath (Join-Path $root 'licenses/catalog.json') -Destination $catalogDirectory
    foreach ($relative in $expected.Keys) {
        $destination = Join-Path $repository $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
        Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
    }

    # 在默认 Windows 换行环境下实际暂存、覆盖检出, 检查 Git 不会改变清单绑定的原始字节.
    & git -C $repository -c core.autocrlf=true add -- .
    if ($LASTEXITCODE -ne 0) { throw 'Failed to stage isolated Git files.' }
    & git -C $repository -c core.autocrlf=true checkout-index --all --force
    if ($LASTEXITCODE -ne 0) { throw 'Failed to check out isolated Git files.' }
    Assert-CatalogHashes $repository $expected
    Write-Host "License source Git tests passed. Verified $($expected.Count) catalog files."
} finally {
    foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GIT_*' })) {
        Remove-Item -LiteralPath "Env:$($variable.Name)"
    }
    foreach ($name in $savedGitEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedGitEnvironment[$name], 'Process') }
    if ($ownsSandbox -and (Test-Path -LiteralPath $sandbox)) {
        $sandboxItem = Get-Item -LiteralPath $sandbox -Force
        $resolvedSandbox = [IO.Path]::GetFullPath($sandboxItem.FullName).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $expectedSandbox = [IO.Path]::GetFullPath($sandbox).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ($resolvedSandbox -ine $expectedSandbox -or ($sandboxItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing to remove an unexpected temporary repository path.' }
        $links = @(Get-ChildItem -LiteralPath $sandbox -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($links.Count) { throw 'Refusing to remove a temporary repository containing links.' }
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}
