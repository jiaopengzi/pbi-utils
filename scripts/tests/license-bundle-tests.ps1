# FilePath    : pbi-utils\scripts\tests\license-bundle-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 用独立真实文件、NuGet 元数据和前端依赖图验证离线许可完整性及失败边界.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$licenseModule = Import-Module (Join-Path $root 'scripts/ThirdPartyLicenses.psm1') -Force -PassThru
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-license-' + [Guid]::NewGuid().ToString('N'))
$savedNuget = $env:NUGET_PACKAGES
$count = 0

<# .SYNOPSIS 断言行为结果, 失败保留场景说明. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 写入夹具 JSON, 自动建立父目录; 参数为路径和对象, 失败抛错. #>
function Write-Json([string]$Path, $Value) {
    $null = New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8
}
<# .SYNOPSIS 验证操作失败且原因匹配, 防止其他错误误充负向成功. #>
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw 'Expected failure did not occur.' } catch { if ($_.Exception.Message -notmatch $Pattern) { throw } }
    $script:count++
}
<# .SYNOPSIS 创建包含必要入口 deps 的新发布夹具; 参数为场景名与范围, 返回目录. #>
function New-Publish([string]$Name, [string]$Scope = 'Core') {
    $directory = Join-Path $sandbox $Name
    $deps = @{ runtimeTarget = @{ name = 'net10.0/win-x64' }; targets = @{ 'net10.0/win-x64' = @{ 'Example/1.0.0' = @{ runtime = @{ 'Example.dll' = @{} } } } }; libraries = @{ 'Example/1.0.0' = @{ type = 'package'; sha512 = ''; path = 'example/1.0.0' } } }
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host')) { Write-Json (Join-Path $directory "$entry.deps.json") $deps }
    if ($Scope -eq 'All') { Write-Json (Join-Path $directory 'PbiUtils.Desktop.deps.json') $deps }
    return $directory
}
<# .SYNOPSIS 创建已安装 npm 包, 不调用网络或安装器; 参数为相对目录与声明, 返回物理路径. #>
function New-Npm([string]$Relative, [string]$Name, [string]$Version, $Dependencies = @{}) {
    $directory = Join-Path $repository "web/node_modules/$Relative"
    Write-Json (Join-Path $directory 'package.json') @{ name = $Name; version = $Version; license = 'MIT'; dependencies = $Dependencies }
    Set-Content -LiteralPath (Join-Path $directory 'LICENSE') -Value "MIT permission and warranty fixture: $Name $Version / copyright Original Author."
    return $directory
}

try {
    $repository = Join-Path $sandbox 'repository'
    $env:NUGET_PACKAGES = Join-Path $sandbox 'nuget'
    $package = Join-Path $env:NUGET_PACKAGES 'example/1.0.0'
    $null = New-Item -ItemType Directory -Path $package -Force
    $spec = '<package><metadata><id>Example</id><version>1.0.0</version><license type="file">LICENSE.txt</license><copyright>Original Package Author</copyright></metadata></package>'
    Set-Content -LiteralPath (Join-Path $package 'example.nuspec') -Value $spec
    Set-Content -LiteralPath (Join-Path $package 'LICENSE.txt') -Value 'Original full license fixture with permission and warranty.'
    Set-Content -LiteralPath (Join-Path $package 'NOTICE') -Value 'Upstream NOTICE must survive verbatim.'
    Write-Json (Join-Path $repository 'licenses/catalog.json') @{ schemaVersion = 1; packages = @(); resources = @() }
    Set-Content -LiteralPath (Join-Path $repository 'LICENSE') -Value 'Project MIT fixture.'
    Set-Content -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Value 'Source acknowledgments fixture.'

    $core = New-Publish 'core'
    $manifest = Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot $core -Scope Core
    Assert ($manifest.components.Count -eq 1 -and $manifest.components[0].version -eq '1.0.0') 'Core package identity must be exact.'
    $text = Get-Content -LiteralPath (Join-Path $core 'THIRD-PARTY-NOTICES.txt') -Raw
    Assert ($text.Contains('Upstream NOTICE must survive verbatim.') -and $text.Contains('Project MIT fixture.') -and $text.Contains('Original Package Author')) 'Aggregate must preserve project license, upstream notice and copyright.'
    $bytes = [IO.File]::ReadAllBytes((Join-Path $core 'THIRD-PARTY-NOTICES.txt'))
    Assert ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) 'NSIS aggregate must be UTF-8 with BOM.'
    $count++
    Assert-PbiLicenseBundle -PublishRoot $core
    $licenseFile = Join-Path $core $manifest.components[0].files[0].path
    $original = [IO.File]::ReadAllBytes($licenseFile)
    Set-Content -LiteralPath $licenseFile -Value 'changed'
    Assert-Fails { Assert-PbiLicenseBundle -PublishRoot $core } 'integrity mismatch'
    [IO.File]::WriteAllBytes($licenseFile, $original)
    Set-Content -LiteralPath (Join-Path $core 'licenses/unlisted.txt') -Value 'extra'
    Assert-Fails { Assert-PbiLicenseBundle -PublishRoot $core } 'Unlisted license'
    Remove-Item -LiteralPath (Join-Path $core 'licenses/unlisted.txt')
    $depsPath = Join-Path $core 'pbi-utils.deps.json'
    $depsBytes = [IO.File]::ReadAllBytes($depsPath)
    Add-Content -LiteralPath $depsPath -Value ' '
    Assert-Fails { Assert-PbiLicenseBundle -PublishRoot $core } 'dependency descriptors'
    [IO.File]::WriteAllBytes($depsPath, $depsBytes)

    # 在依赖文档复制期间真实改写仓库根声明, 防止聚合文本与独立 LICENSE 来自两次不同读取.
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        $documentPath = Join-Path $repository $name
        $documentBytes = [IO.File]::ReadAllBytes($documentPath)
        $mutation = @{ Trigger = (Join-Path $package 'LICENSE.txt'); Document = $documentPath; Applied = $false }
        try {
            & $licenseModule {
                param($state)
                $script:DocumentMutation = $state
                <# .SYNOPSIS 保持真实文件复制, 仅在指定依赖复制后改写夹具根文档以确定性复现并发变更. #>
                function script:Copy-Item([string]$LiteralPath, [string]$Destination) {
                    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination
                    if (!$script:DocumentMutation.Applied -and $LiteralPath -ceq $script:DocumentMutation.Trigger) {
                        [IO.File]::WriteAllText($script:DocumentMutation.Document, 'Concurrent replacement: 焦棚子(jiaopengzi).')
                        $script:DocumentMutation.Applied = $true
                    }
                }
            } $mutation
            $drifted = New-Publish ('root-drift-' + $name)
            Assert-Fails { Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot $drifted -Scope Core } 'Distribution document changed during generation'
            Assert $mutation.Applied 'Concurrent root document mutation must occur during generation.'
        } finally {
            & $licenseModule { Remove-Item -LiteralPath Function:Copy-Item; Remove-Variable -Name DocumentMutation -Scope Script }
            [IO.File]::WriteAllBytes($documentPath, $documentBytes)
        }
    }

    Set-Content -LiteralPath (Join-Path $package 'example.nuspec') -Value $spec.Replace('type="file">LICENSE.txt', 'type="expression">Unreviewed-99')
    $unknown = New-Publish 'unknown'
    Assert-Fails { Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot $unknown -Scope Core } 'Unreviewed or missing NuGet license'
    Assert (!(Test-Path -LiteralPath (Join-Path $unknown 'licenses'))) 'Unknown licenses must fail before output is generated.'
    Set-Content -LiteralPath (Join-Path $package 'example.nuspec') -Value $spec
    Move-Item -LiteralPath (Join-Path $package 'LICENSE.txt') -Destination (Join-Path $package 'original.txt')
    Assert-Fails { Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot (New-Publish 'missing') -Scope Core } 'Missing declared NuGet license'
    Move-Item -LiteralPath (Join-Path $package 'original.txt') -Destination (Join-Path $package 'LICENSE.txt')

    # 长组件身份和原文名保留在清单, 发布文件通过旧 Windows 文件 API 实际读取, 防止安装器 MAX_PATH 回归.
    $longSourceName = ('ThirdPartyDistributionLicense-' * 3) + '2026.txt'
    $longSourceRelative = 'licenses/source/' + $longSourceName
    $longSource = Join-Path $repository $longSourceRelative
    $null = New-Item -ItemType Directory -Path (Split-Path $longSource -Parent) -Force
    Set-Content -LiteralPath $longSource -Value 'Long identity license must retain its complete original text and attribution.'
    $longHash = (Get-FileHash -LiteralPath $longSource).Hash
    $longName = 'Long source derived component ' * 5
    $longVersion = 'SHA256-' + ('0123456789abcdef' * 4)
    Write-Json (Join-Path $repository 'licenses/catalog.json') @{ schemaVersion = 1; packages = @(); resources = @(@{ name = $longName; version = $longVersion; scope = 'Core'; license = 'MIT'; source = 'https://example.invalid/long'; files = @(@{ path = $longSourceRelative; source = 'https://example.invalid/long'; sha256 = $longHash }) }) }
    $longPublish = New-Publish ('installer-stage-' + ('nested-' * 9))
    $longManifest = Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot $longPublish -Scope Core
    $longComponent = $longManifest.components | Where-Object ecosystem -EQ 'resource'
    Assert ($longComponent.name -ceq $longName -and $longComponent.version -ceq $longVersion -and $longComponent.files[0].sourceDocument -ceq $longSourceName) 'Compact paths must preserve full identity and original document name in the manifest.'
    $longDestination = Join-Path $longPublish $longComponent.files[0].path
    Assert ((Get-FileHash -LiteralPath $longDestination).Hash -ceq $longHash) 'Compact distribution must preserve the original license bytes.'
    $reader = Join-Path $sandbox 'read-legacy.ps1'
    Set-Content -LiteralPath $reader -Encoding ascii -Value 'param([string]$Path) $ErrorActionPreference="Stop"; $sha=[Security.Cryptography.SHA256]::Create(); try { [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace("-", "") } finally { $sha.Dispose() }'
    $legacyShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $legacyHash = & $legacyShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $reader -Path $longDestination
    Assert ($LASTEXITCODE -eq 0 -and $legacyHash -ceq $longHash) 'Legacy Windows file access must read the actual staged license without a long-path prefix.'
    Assert-PbiLicenseBundle -PublishRoot $longPublish
    $count++

    Write-Json (Join-Path $repository 'web/package.json') @{ dependencies = @{ app = '1.0.0' }; devDependencies = @{ buildtool = '99.0.0' } }
    $null = New-Npm 'app' 'app' '1.0.0' @{ a = '1.0.0'; b = '1.0.0'; alias = 'npm:actual@1.0.0' }
    $null = New-Npm 'app/node_modules/a' 'a' '1.0.0' @{ shared = '1.0.0' }
    $null = New-Npm 'app/node_modules/b' 'b' '1.0.0' @{ shared = '1.0.0' }
    $null = New-Npm 'app/node_modules/alias' 'actual' '1.0.0'
    $null = New-Npm 'app/node_modules/a/node_modules/shared' 'shared' '1.0.0' @{ x = '1.0.0' }
    $null = New-Npm 'app/node_modules/b/node_modules/shared' 'shared' '1.0.0' @{ x = '2.0.0' }
    $null = New-Npm 'app/node_modules/a/node_modules/shared/node_modules/x' 'x' '1.0.0'
    $x2 = New-Npm 'app/node_modules/b/node_modules/shared/node_modules/x' 'x' '2.0.0'
    $null = New-Npm 'buildtool' 'buildtool' '99.0.0'
    $lock = "lockfileVersion: '9.0'`npackages:`n" + ((@('app@1.0.0', 'a@1.0.0', 'b@1.0.0', 'actual@1.0.0', 'shared@1.0.0', 'x@1.0.0', 'x@2.0.0', 'buildtool@99.0.0') | ForEach-Object { "  '${_}': {}" }) -join "`n")
    Set-Content -LiteralPath (Join-Path $repository 'web/pnpm-lock.yaml') -Value $lock
    $null = New-Item -ItemType Directory -Path (Join-Path $repository 'web/node_modules/.pnpm') -Force
    Set-Content -LiteralPath (Join-Path $repository 'web/node_modules/.pnpm/lock.yaml') -Value $lock
    $all = New-Publish 'all' 'All'
    $allManifest = Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot $all -Scope All
    $npm = @($allManifest.components | Where-Object ecosystem -EQ 'npm')
    Assert ($npm.Count -eq 7 -and @($npm | Where-Object name -EQ 'buildtool').Count -eq 0) 'Production graph must exclude root development tools.'
    Assert (@($npm | Where-Object name -EQ 'x').Count -eq 2) 'Both peer-context physical instances must contribute their different transitive versions.'
    Assert (@($npm | Where-Object name -EQ 'actual').Count -eq 1) 'npm alias must retain the real package identity.'
    $shared = $npm | Where-Object name -EQ 'shared'
    Assert ($shared.dependencies -contains 'x/1.0.0' -and $shared.dependencies -contains 'x/2.0.0') 'Merged package identity must retain all transitive edges.'
    $count++
    Remove-Item -LiteralPath (Join-Path $x2 'LICENSE')
    Assert-Fails { Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot (New-Publish 'npm-missing' 'All') -Scope All } 'Missing npm license'
    Set-Content -LiteralPath (Join-Path $x2 'LICENSE') -Value 'MIT fixture restored.'
    Set-Content -LiteralPath (Join-Path $repository 'web/pnpm-lock.yaml') -Value $lock.Replace("  'x@2.0.0': {}", '')
    Assert-Fails { Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot (New-Publish 'npm-drift' 'All') -Scope All } 'Frontend license dependency discovery failed'
    Set-Content -LiteralPath (Join-Path $repository 'web/pnpm-lock.yaml') -Value $lock

    $external = Join-Path $sandbox 'outside'
    $null = New-Item -ItemType Directory -Path $external
    Set-Content -LiteralPath (Join-Path $external 'sentinel.txt') -Value 'untouched'
    $linked = New-Publish 'linked'
    $null = New-Item -ItemType Junction -Path (Join-Path $linked 'licenses') -Target $external
    Assert-Fails { Export-PbiLicenseBundle -RepositoryRoot $repository -PublishRoot $linked -Scope Core } 'must not contain a link'
    Assert ((Get-Content -LiteralPath (Join-Path $external 'sentinel.txt') -Raw).Trim() -eq 'untouched') 'Refusing a link must preserve its target.'
    # 显式移除链接本身后才清理自有夹具树, 不递归进入外部目标.
    [IO.Directory]::Delete((Join-Path $linked 'licenses'))
    Write-Output "License bundle tests passed. $count behavioral checks."
} finally {
    $env:NUGET_PACKAGES = $savedNuget
    $full = [IO.Path]::GetFullPath($sandbox)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notlike 'pbi-utils-license-*') { throw 'Unsafe license test cleanup target.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
