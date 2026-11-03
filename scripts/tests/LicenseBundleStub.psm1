# FilePath    : pbi-utils\scripts\tests\LicenseBundleStub.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 仅供隔离构建编排测试替换许可收集边界, 不读取外部依赖、网络或用户缓存.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<# .SYNOPSIS 在合成仓库中写入确定的许可产物并记录调用, 存在失败标记时在部分写入后抛出指定错误.
   .PARAMETER RepositoryRoot 调用构建入口的隔离仓库根目录, 必须与替身所属目录一致.
   .PARAMETER PublishRoot 本轮合成产物的 staging 目录, 必须位于隔离仓库内.
   .PARAMETER Scope 构建传入的有效产品范围, 用于核验编排参数未遗漏或改变.
   .OUTPUTS 无; 无效参数或注入失败均终止, 不执行外部工具.
#>
function Export-PbiLicenseBundle {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)][ValidateSet('Core', 'All')][string]$Scope
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $expectedRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
    $publish = [IO.Path]::GetFullPath($PublishRoot)
    if ($root -cne $expectedRoot -or $root -cne (Get-Location).Path -or
        !$publish.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($publish) -notmatch '^\.pbi-utils-publish-[0-9a-f]{32}$') { throw '隔离许可生成器收到错误的仓库或 staging 参数' }
    @{ repositoryRoot = $root; publishRoot = $publish; scope = $Scope } | ConvertTo-Json -Compress |
        Add-Content -LiteralPath (Join-Path $root 'license-stub-calls.jsonl') -Encoding utf8
    [IO.File]::WriteAllText((Join-Path $publish 'LICENSE'), 'synthetic project license')
    if ([IO.File]::Exists((Join-Path $root 'license-stub-failure'))) {
        $failure = [InvalidOperationException]::new('受控许可生成失败')
        $failure.Data['ExitCode'] = 53
        throw $failure
    }
    foreach ($name in @('THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt')) {
        [IO.File]::WriteAllText((Join-Path $publish $name), 'synthetic dependency notices')
    }
    $null = [IO.Directory]::CreateDirectory((Join-Path $publish 'licenses'))
    [IO.File]::WriteAllText((Join-Path $publish 'licenses/manifest.json'), '{"schemaVersion":1,"fixture":true}')
}

Export-ModuleMember -Function Export-PbiLicenseBundle
