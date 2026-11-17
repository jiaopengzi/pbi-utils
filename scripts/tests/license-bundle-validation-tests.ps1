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
    $sharedRelative = 'licenses/source/Shared.txt'
    $sharedText = 'Full license shared by two components'
    $sharedSource = Write-Fixture $repository $sharedRelative $sharedText
    $sharedHash = (Get-FileHash -LiteralPath $sharedSource).Hash
    $sharedVersions = [ordered]@{ SharedFirst = '1.2.0'; SharedSecond = '2.3.0' }
    foreach ($entry in $sharedVersions.GetEnumerator()) {
        $origin = "https://example.invalid/$($entry.Key)/$($entry.Value)"
        $resources += @{ name = $entry.Key; version = $entry.Value; license = 'MIT'; scope = 'Core'; source = $origin; files = @(@{ path = $sharedRelative; source = "$origin/LICENSE"; sha256 = $sharedHash }) }
    }
    $null = Write-Fixture $repository 'licenses/catalog.json' (@{ schemaVersion = 1; resources = $resources; packages = @() } | ConvertTo-Json -Depth 10)
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host')) {
        $null = Write-Fixture $publish "$entry.deps.json" '{"libraries":{},"targets":{}}'
    }
    $generatedManifest = Export-PbiLicenseBundle $repository $publish Core
    Assert-PbiLicenseBundle $publish

    # 核对共用原文只分发一份, 两个组件仍保留各自的版本和来源.
    $sharedComponents = @($generatedManifest.components | Where-Object name -In @('SharedFirst', 'SharedSecond'))
    $sharedFiles = @($generatedManifest.files | Where-Object sha256 -CEQ $sharedHash)
    $sharedCopies = @(Get-ChildItem -LiteralPath (Join-Path $publish 'licenses') -File -Recurse | Where-Object {
        (Get-FileHash -LiteralPath $_.FullName).Hash -ceq $sharedHash
    })
    if ($sharedComponents.Count -ne 2 -or $sharedFiles.Count -ne 1 -or $sharedCopies.Count -ne 1) {
        throw 'Shared static resource licenses must retain both identities and publish exactly one original.'
    }
    $aggregate = Get-Content -LiteralPath (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -Raw
    if ([regex]::Matches($aggregate, [regex]::Escape($sharedText)).Count -ne 1) {
        throw 'Shared static resource license text must appear exactly once in the aggregate.'
    }
    foreach ($component in $sharedComponents) {
        $version = $sharedVersions[$component.name]
        $origin = "https://example.invalid/$($component.name)/$version"
        $document = $component.files[0]
        $section = [regex]::Match($aggregate, "(?ms)^resource: $($component.name) / $version\r?\n(.*?)(?=^={78}\r?$|\z)")
        if ($component.version -cne $version -or $component.source -cne $origin -or $component.files.Count -ne 1 -or
            $document.path -cne $sharedFiles[0].path -or $document.source -cne "$origin/LICENSE" -or
            !$section.Success -or !$section.Groups[1].Value.Contains($document.path, [StringComparison]::Ordinal)) {
            throw "Shared static resource license lost a component binding: $($component.name)"
        }
    }

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
    Reject-Manifest 'Unrelated license uses a shared resource document' {
        param($manifest)
        $manifest.components[0].files[0].path = $sharedFiles[0].path
    } '(component license|Static resource)'
    Reject-Manifest 'Shared license has the other component origin' {
        param($manifest)
        $first = $manifest.components | Where-Object name -CEQ 'SharedFirst'
        $second = $manifest.components | Where-Object name -CEQ 'SharedSecond'
        $first.files[0].source = $second.files[0].source
    } 'Static resource'
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
