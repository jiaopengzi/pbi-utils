# FilePath    : pbi-utils\scripts\tests\license-bundle-validation-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 用离线隔离夹具验证最终许可清单的静态资源、范围及文档归属, 不安装产品或读取外部依赖.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) 'ThirdPartyLicenses.psm1') -Force
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-license-validation-' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $sandbox 'repository'
$publish = Join-Path $sandbox 'publish'
$failures = [Collections.Generic.List[string]]::new()

<# .SYNOPSIS 在隔离目录写 UTF-8 文件并创建父目录, 返回文件路径; I/O 失败抛错. #>
function Write-Fixture([string]$Root, [string]$Relative, [string]$Content) {
    $path = Join-Path $Root $Relative
    $null = [IO.Directory]::CreateDirectory((Split-Path $path -Parent))
    [IO.File]::WriteAllText($path, $Content)
    return $path
}

<# .SYNOPSIS 修改清单后要求真实验证器拒绝, 参数为案例、改写动作和预期原因; 记录错误并恢复原清单. #>
function Reject-Manifest([string]$Name, [scriptblock]$Change, [string]$Pattern) {
    $path = Join-Path $publish 'licenses/manifest.json'
    $original = [IO.File]::ReadAllBytes($path)
    try {
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        & $Change $manifest
        $manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
        try { Assert-PbiLicenseBundle $publish; $failures.Add("$Name was accepted") }
        catch { if ($_.Exception.Message -notmatch $Pattern) { $failures.Add("$Name failed for an unrelated reason: $($_.Exception.Message)") } }
    } finally { [IO.File]::WriteAllBytes($path, $original) }
}

try {
    $null = Write-Fixture $repository 'LICENSE' 'Project MIT text'
    $null = Write-Fixture $repository 'THIRD-PARTY-NOTICES.md' 'Source notices'
    $resources = @()
    foreach ($name in @('First', 'Second')) {
        $relative = "licenses/source/$name.txt"
        $path = Write-Fixture $repository $relative "$name resource full license"
        $resources += @{ name = $name; version = '1.0.0'; license = 'MIT'; scope = 'Core'; source = "https://example.invalid/$name"; files = @(@{ path = $relative; source = "https://example.invalid/$name"; sha256 = (Get-FileHash -LiteralPath $path).Hash }) }
    }
    $null = Write-Fixture $repository 'licenses/catalog.json' (@{ schemaVersion = 1; resources = $resources; packages = @() } | ConvertTo-Json -Depth 10)
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host')) {
        $null = Write-Fixture $publish "$entry.deps.json" '{"libraries":{},"targets":{}}'
    }
    $generatedManifest = Export-PbiLicenseBundle $repository $publish Core
    Assert-PbiLicenseBundle $publish

    # JSON 字段顺序不是清单身份; 强制反转本进程生成顺序, 再由全新 PowerShell 进程验证同一许可包.
    $manifestPath = Join-Path $publish 'licenses/manifest.json'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    try {
        $reordered = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        $reordered.dependencyDescriptors = @(foreach ($descriptor in $generatedManifest.dependencyDescriptors) {
            $keys = @($descriptor.Keys)
            [Array]::Reverse($keys)
            $record = [ordered]@{}
            foreach ($key in $keys) { $record[$key] = $descriptor[$key] }
            $record
        })
        $reordered | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        Assert-PbiLicenseBundle $publish
        $child = Write-Fixture $sandbox 'validate-in-new-process.ps1' @'
param([string]$ModulePath, [string]$PublishRoot)
$ErrorActionPreference = 'Stop'
Import-Module -Name $ModulePath -Force
Assert-PbiLicenseBundle -PublishRoot $PublishRoot
Write-Output 'Independent license validation passed.'
'@
        $modulePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'ThirdPartyLicenses.psm1'
        $childOutput = @(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $child -ModulePath $modulePath -PublishRoot $publish)
        if ($LASTEXITCODE -ne 0 -or $childOutput -cnotcontains 'Independent license validation passed.') { throw 'A separate PowerShell process must accept the unchanged license identities regardless of JSON field order.' }
    } finally { [IO.File]::WriteAllBytes($manifestPath, $manifestBytes) }

    Reject-Manifest 'Missing catalog resource' { param($manifest) $manifest.components = @($manifest.components | Where-Object name -NE 'First') } 'Static resource'
    Reject-Manifest 'Changed resource license' { param($manifest) $manifest.components[0].license = 'ISC' } 'Static resource'
    Reject-Manifest 'Foreign component document' { param($manifest) $manifest.components[0].files = @($manifest.components[1].files) } 'component license'
    Reject-Manifest 'Missing original document name' { param($manifest) $manifest.components[0].files[0].sourceDocument = '' } 'component license'
    Reject-Manifest 'Invalid original document path' { param($manifest) $manifest.components[0].files[0].sourceDocument = '../LICENSE' } 'component license'
    Reject-Manifest 'Changed original resource document name' { param($manifest) $manifest.components[0].files[0].sourceDocument = 'Wrong.txt' } 'Static resource'
    Reject-Manifest 'Path alias into the root license' {
        param($manifest)
        $alias = @{} + ($manifest.files | Where-Object path -CEQ 'LICENSE')
        $alias.path = 'licenses/../LICENSE'
        $manifest.files += $alias
    } 'Invalid license manifest path'

    $desktop = Write-Fixture $publish 'PbiUtils.Desktop.deps.json' '{"libraries":{},"targets":{}}'
    try {
        Reject-Manifest 'Core bundle with Desktop descriptor' {
            param($manifest)
            $manifest.dependencyDescriptors += @{ path = 'PbiUtils.Desktop.deps.json'; sha256 = (Get-FileHash -LiteralPath $desktop).Hash }
        } 'scope'
    } finally { Remove-Item -LiteralPath $desktop }
    Assert-PbiLicenseBundle $publish
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
    Write-Host 'License bundle validation tests passed.'
} finally {
    $absolute = [IO.Path]::GetFullPath($sandbox)
    if ([IO.Path]::GetDirectoryName($absolute) -ine [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-license-validation-[0-9a-f]{32}$') { throw 'Validation test cleanup path is invalid.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
