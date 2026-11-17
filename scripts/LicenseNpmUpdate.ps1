# FilePath    : pbi-utils\scripts\LicenseNpmUpdate.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 以锁定构建工具的实际包内文件核对前端注入代码许可, 更新内存清单.

<#
.SYNOPSIS 核对 Vite、Vue 插件和 Rolldown 的锁定版本及原文, 更新许可来源.
.PARAMETER RepositoryRoot 含已冻结安装的 web 依赖和审核许可的仓库根.
.PARAMETER Catalog 本轮待验证的可变许可清单.
.OUTPUTS 无返回值, 仅更新内存中的资源版本及来源; 缺依赖、版本或条款漂移时抛错.
#>
function Update-PbiNpmLicenseCatalog([string]$RepositoryRoot, [System.Collections.IDictionary]$Catalog) {
    $web = Get-PbiLicenseSafePath $RepositoryRoot (Join-Path $RepositoryRoot 'web')
    $modules = Get-PbiLicenseSafePath $web (Join-Path $web 'node_modules')
    $helperOutput = & node (Join-Path $PSScriptRoot 'LicenseNpmDependencies.mjs') $web '--build-helpers'
    if ($LASTEXITCODE -ne 0) { throw "Frontend build helper license discovery failed ($LASTEXITCODE). Run setupVue and check the frontend lockfile." }
    $helpers = ($helperOutput | ConvertFrom-Json -AsHashtable).buildHelpers
    $rules = @(
        @{ Name = 'vite'; Resource = 'Vite preload helpers'; Source = 'https://github.com/vitejs/vite/tree/v'; Documents = [ordered]@{ 'LICENSE.md' = 'LICENSE.md, complete Vite core license section' } }
        @{ Name = '@vitejs/plugin-vue'; Resource = '@vitejs/plugin-vue export helper'; Source = 'https://www.npmjs.com/package/@vitejs/plugin-vue/v/'; Documents = [ordered]@{ 'LICENSE' = 'LICENSE' } }
        @{ Name = 'rolldown'; Resource = 'Rolldown runtime helpers'; Source = 'https://github.com/rolldown/rolldown/tree/v'; Documents = [ordered]@{ 'LICENSE' = 'LICENSE'; 'THIRD-PARTY-LICENSE' = 'THIRD-PARTY-LICENSE' } }
    )
    foreach ($rule in $rules) {
        $entries = @($Catalog.resources | Where-Object name -CEQ $rule.Resource)
        $packages = @($helpers | Where-Object name -CEQ $rule.Name)
        if ($entries.Count -ne 1 -or $packages.Count -ne 1) { throw "Missing or ambiguous build helper license entry: $($rule.Name)" }
        $entry = $entries[0]
        $package = $packages[0]
        if ($entry.license -cne $package.license -or $entry.files.Count -ne $rule.Documents.Count) {
            throw "Review the build helper license for $($package.name)/$($package.version) in licenses/catalog.json."
        }
        $directory = Get-PbiLicenseSafePath $modules $package.directory
        $leaf = $package.name.Split('/')[-1]
        $tarball = "https://registry.npmjs.org/$($package.name)/-/$leaf-$($package.version).tgz"
        foreach ($file in $rule.Documents.Keys) {
            $label = $rule.Documents[$file]
            $suffix = " ($label)"
            $documents = @($entry.files | Where-Object { $_.source.EndsWith($suffix, [StringComparison]::Ordinal) })
            if ($documents.Count -ne 1) { throw "Review the build helper license mapping for $($package.name): $file" }
            $path = Get-PbiLicenseSafePath $directory (Join-Path $directory $file)
            [byte[]]$bytes = [IO.File]::ReadAllBytes($path)
            if ($package.name -ceq 'vite') {
                # 产物复制的是 Vite 核心辅助代码, 保留完整原始核心许可节和换行.
                $encoding = [Text.UTF8Encoding]::new($false, $true)
                $text = $encoding.GetString($bytes)
                $boundary = $text.IndexOf("`n# Licenses of bundled dependencies", [StringComparison]::Ordinal)
                if (!$text.StartsWith('# Vite core license', [StringComparison]::Ordinal) -or $boundary -lt 1) {
                    throw "Review Vite's changed LICENSE.md structure for $($package.version)."
                }
                $bytes = $encoding.GetBytes($text.Substring(0, $boundary + 1))
            }
            Confirm-PbiLicenseUpdateContent $RepositoryRoot $documents[0] $bytes "$tarball$suffix"
        }
        $entry.version = $package.version
        $entry.source = $rule.Source + $package.version
    }
}
