# FilePath    : pbi-utils\scripts\tests\installer-package-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离目录验证安装包许可刷新、快照、原生编译失败和交付日志, 不调用 NSIS 或安装产品.

<#
.SYNOPSIS 使用共享隔离载荷验证指定打包行为, 任一断言失败返回非零.
.PARAMETER Scenario 分别运行快照、编译故障、许可更新和许可拒绝场景, 保持每项测试独立的超时边界.
#>
param([ValidateSet('Package', 'PackageFailure', 'LicenseRefresh', 'LicenseRefreshFailure')][string]$Scenario = 'Package')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils package ' + [Guid]::NewGuid().ToString('N'))
$savedDirectory = Get-Location
$registry = $null
$state = @{
    Messages = [Collections.Generic.List[object]]::new()
    Commands = [Collections.Generic.List[object]]::new()
    SwapField = ''
    SwapValue = ''
    CompileFailure = $false
    CleanupFailure = $false
    LicenseChangeOnCompile = ''
    ErrorOutput = ''
    Scenario = $Scenario
}

<# .SYNOPSIS 断言实际行为, 参数为条件和说明, 不满足时终止本测试. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<#
.SYNOPSIS 创建可通过真实发布清单验证的最小 FDD 测试载荷.
.PARAMETER Root 隔离仓库中的 Release All 目录.
.NOTES 内容为明确测试替身, 只验证文件、依赖声明和打包边界, 不宣称可执行.
#>
function New-TestPackageSource([string]$Root) {
    $null = New-Item -ItemType Directory -Path (Join-Path $Root 'web') -Force
    foreach ($entry in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
        foreach ($suffix in @('exe', 'dll')) { Set-Content -LiteralPath (Join-Path $Root "$entry.$suffix") -Value "synthetic $entry $suffix" }
        $targetName = '.NETCoreApp,Version=v10.0/win-x64'
        @{
            runtimeTarget = @{ name = $targetName }
            targets = @{ $targetName = @{ "$entry/1.2.3" = @{ runtime = @{ "$entry.dll" = @{} } } } }
            libraries = @{ "$entry/1.2.3" = @{ type = 'project' } }
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Root "$entry.deps.json") -Encoding utf8
        @{ runtimeOptions = @{ tfm = 'net10.0'; frameworks = @(
            @{ name = 'Microsoft.NETCore.App'; version = '10.0.0' },
            @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' }
        ) } } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Root "$entry.runtimeconfig.json") -Encoding utf8
    }
    Set-Content -LiteralPath (Join-Path $Root 'web/index.html') -Value 'synthetic web'
    # 最小合成依赖经过真实许可生成器, 避免打包边界测试反复扫描产品全部第三方内容.
    # 真实闭包和未知许可由 license-bundle-tests 独立覆盖, 此处只验证打包前后快照校验.
    $licenseInput = $sandbox
    $fixturePackage = Join-Path $licenseInput 'web/node_modules/license-fixture'
    $null = New-Item -ItemType Directory -Path $fixturePackage, (Join-Path $licenseInput 'licenses'), (Join-Path $licenseInput 'web/node_modules/.pnpm') -Force
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Set-Content -LiteralPath (Join-Path $licenseInput $name) -Value "Synthetic packaging fixture: $name"
    }
    $nsisLicense = Join-Path $licenseInput 'licenses/nsis.txt'
    Set-Content -LiteralPath $nsisLicense -Value 'synthetic NSIS license'
    $obsoleteLicense = Join-Path $licenseInput 'licenses/obsolete.txt'
    Set-Content -LiteralPath $obsoleteLicense -Value 'obsolete resource license marker'
    @{ schemaVersion = 1; packages = @(); resources = @(
        @{ name = 'NSIS-COPYING'; version = '3.13'; scope = 'All'; license = 'fixture'; source = 'fixture'; files = @(@{
            path = 'licenses/nsis.txt'; source = 'fixture'; sha256 = (Get-FileHash -LiteralPath $nsisLicense).Hash
        }) },
        @{ name = 'Removed-Resource'; version = '1.0'; scope = 'All'; license = 'fixture'; source = 'fixture'; files = @(@{
            path = 'licenses/obsolete.txt'; source = 'fixture'; sha256 = (Get-FileHash -LiteralPath $obsoleteLicense).Hash
        }) }
    ) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $licenseInput 'licenses/catalog.json')
    '{"dependencies":{"license-fixture":"1.0.0"}}' | Set-Content -LiteralPath (Join-Path $licenseInput 'web/package.json')
    '{"name":"license-fixture","version":"1.0.0","license":"MIT"}' | Set-Content -LiteralPath (Join-Path $fixturePackage 'package.json')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $fixturePackage 'LICENSE')
    if ($Scenario -eq 'LicenseRefreshFailure') {
        '{"name":"license-fixture","version":"1.0.0","license":"MIT","optionalDependencies":{"license-optional":"1.0.0"}}' | Set-Content -LiteralPath (Join-Path $fixturePackage 'package.json')
        $optionalPackage = Join-Path $fixturePackage 'node_modules/license-optional'
        $null = New-Item -ItemType Directory -Path $optionalPackage -Force
        '{"name":"license-optional","version":"1.0.0","license":"MIT"}' | Set-Content -LiteralPath (Join-Path $optionalPackage 'package.json')
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $optionalPackage 'LICENSE')
    }
    foreach ($lock in @('web/pnpm-lock.yaml', 'web/node_modules/.pnpm/lock.yaml')) {
        "lockfileVersion: '9.0'`npackages:`n  license-fixture@1.0.0: {}`n  license-optional@1.0.0: {}" | Set-Content -LiteralPath (Join-Path $licenseInput $lock)
    }
    Import-Module (Join-Path $repositoryRoot 'scripts/ThirdPartyLicenses.psm1') -Force
    $null = Export-PbiLicenseBundle -RepositoryRoot $licenseInput -PublishRoot $Root -Scope All
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        @{ file = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ schemaVersion = 3; layout = 'shared'; runtime = 'win-x64'; scope = 'All'; configuration = 'Release'; deploymentMode = 'framework-dependent'; version = '1.2.3'; sdkVersion = '10.0.403'; files = $files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root 'manifest.json') -Encoding utf8
}
<# .SYNOPSIS 读取隔离交付目录的精确字节快照, 不包含临时目录, 枚举或哈希失败直接传播. #>
function Get-TestPackageSnapshot([string]$Root) {
    return (@(Get-ChildItem -LiteralPath $Root -File | Where-Object Name -ne '.package.lock' | Sort-Object Name | ForEach-Object {
        $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join '|')
}
<# .SYNOPSIS 读取发布目录全部文件的相对路径与 SHA-256, 返回可比较快照, 确认打包不修改原始构建产物. #>
function Get-TestPublishedSnapshot([string]$Root) {
    return (@(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join '|')
}
<# .SYNOPSIS 确认本次临时目录清理且模式锁已释放, 参数为打包交付目录, 失败时抛错. #>
function Assert-TestPackageReleased([string]$Root) {
    Assert (@(Get-ChildItem -LiteralPath $Root -Directory -Filter '.pbi-utils-installer-*').Count -eq 0) '成功或已处理失败后必须清理本次打包临时目录'
    $lease = [IO.File]::Open((Join-Path $Root '.package.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $lease.Dispose()
}
<# .SYNOPSIS 通过真实命令注册表运行 package, 只替换外部编译和远程元数据, 返回实际退出码. #>
function Invoke-TestPackage {
    $state.Messages.Clear()
    $state.Commands.Clear()
    $savedError = [Console]::Error
    $errorCapture = [IO.StringWriter]::new()
    try {
        [Console]::SetError($errorCapture)
        return (& $registry { Invoke-PbiEntry -Arguments @('-Choice', 'package', '-NonInteractive') })
    } finally {
        [Console]::SetError($savedError)
        $state.ErrorOutput = $errorCapture.ToString()
        $errorCapture.Dispose()
    }
}
<# .SYNOPSIS 清理故意注入清理失败后留下的隔离 staging, 参数为打包父目录, 越界时拒绝删除. #>
function Remove-TestPackageTemporaries([string]$Root) {
    foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory -Filter '.pbi-utils-installer-*') {
        $absolute = [IO.Path]::GetFullPath($directory.FullName)
        Assert ([IO.Path]::GetDirectoryName($absolute) -ceq [IO.Path]::GetFullPath($Root) -and $directory.Name -match '^\.pbi-utils-installer-[0-9a-f]{32}$') '测试 staging 清理必须限制在已知父目录'
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}

try {
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'scripts/installer') -Force
    foreach ($sourceFile in @('CommandRegistry.psm1', 'package.ps1', 'PublishArtifacts.psm1', 'PublishRuntime.psm1', 'ThirdPartyLicenses.psm1', 'ThirdPartyLicenseSources.psm1', 'LicenseNpmDependencies.mjs')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts/$sourceFile") -Destination (Join-Path $sandbox 'scripts')
    }
    # 元数据生成已由独立行为测试覆盖; 此替身禁止联网, 保留维护清单的文件写入边界.
    @'
# FilePath    : isolated/scripts/InstallerMetadata.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 仅供安装包边界测试的无网络元数据替身.
<# .SYNOPSIS 将明确标记的合成前提清单写入指定隔离路径, 写失败直接传播. #>
function Export-PbiInstallerPrerequisites([string]$RepositoryRoot, [string]$PublishRoot, [string]$OutputPath) {
    $result = @{ schemaVersion = 1; synthetic = $true; dotnet = @{ sdkVersion = '10.0.403' } }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    return $result
}
Export-ModuleMember -Function Export-PbiInstallerPrerequisites
'@ | Set-Content -LiteralPath (Join-Path $sandbox 'scripts/InstallerMetadata.psm1') -Encoding utf8
    foreach ($maintenanceFile in @('Install-Application.ps1', 'Installation.psm1', 'Ensure-Prerequisites.ps1', 'Prerequisites.psm1', 'Prerequisite-Worker.ps1', 'InstallationPayload.psm1', 'InstallationShell.psm1', 'ExternalToolRegistration.psm1', 'ExternalToolDescriptor.psm1', 'ExternalToolFiles.psm1', 'ExternalTool-Worker.ps1')) {
        Set-Content -LiteralPath (Join-Path $sandbox "scripts/installer/$maintenanceFile") -Value '# synthetic maintenance; never executed'
    }
    Set-Content -LiteralPath (Join-Path $sandbox 'scripts/installer/installer.nsi') -Value '; synthetic NSIS input; never compiled'
    Set-Content -LiteralPath (Join-Path $sandbox 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $sandbox 'global.json') -Value '{"sdk":{"version":"10.0.403"}}'
    $source = Join-Path $sandbox 'artifacts/build/release/win-x64/without-dotnet'
    $destination = Join-Path $sandbox 'artifacts/release/win-x64/without-dotnet'
    New-TestPackageSource $source
    $state.Source = $source
    $registry = Import-Module (Join-Path $sandbox 'scripts/CommandRegistry.psm1') -Force -PassThru
    & $registry {
        param($testState)
        $script:PackageTest = $testState
        <# .SYNOPSIS 只允许 package 所需 NSIS 探测, 不要求开发工具, 不执行工具. #>
        function script:Assert-PbiTools([string[]]$Tools) {
            if (($Tools -join ',') -cne 'makensis') { throw 'package 不得检查开发工具' }
        }
        <# .SYNOPSIS 收集真实入口的阶段消息, 参数与生产日志器一致, 不改写消息内容. #>
        function script:Write-PbiMessage([string]$Message, [string]$Level = 'Info', [bool]$Quiet = $false) {
            $script:PackageTest.Messages.Add(@{ Message = $Message; Level = $Level })
        }
        <# .SYNOPSIS 模拟 NSIS 成功写盘或原生非零退出, 严禁启动真实编译器或安装器. #>
        function script:Invoke-PbiNative([string]$File, [string[]]$Arguments) {
            if ($File -cne 'makensis') { throw '打包不得启动其他工具' }
            $script:PackageTest.Commands.Add(@{ File = $File; Arguments = $Arguments.Clone() })
            if ($script:PackageTest.CompileFailure) { Stop-Pbi 47 '模拟 makensis 编译失败' }
            $payloadArgument = @($Arguments | Where-Object { $_.StartsWith('/DPBI_PAYLOAD=', [StringComparison]::Ordinal) })
            if ($payloadArgument.Count -ne 1) { throw '必须传递唯一载荷目录' }
            $appRoot = Join-Path $payloadArgument[0].Substring('/DPBI_PAYLOAD='.Length) 'app'
            # 生产入口已经执行真实载荷及许可校验; 此处仅读取实际编译文件, 不重复扫描或模拟许可生成行为.
            if ($script:PackageTest.Scenario -like 'LicenseRefresh*') {
                $licenseArguments = @($Arguments | Where-Object { $_.StartsWith('/DPBI_LICENSE_TEXT=', [StringComparison]::Ordinal) })
                if ($licenseArguments.Count -ne 1) { throw 'NSIS 必须读取显式 UTF-8 BOM 许可阅读文件' }
                $displayBytes = [IO.File]::ReadAllBytes($licenseArguments[0].Substring('/DPBI_LICENSE_TEXT='.Length))
                if ($displayBytes.Length -lt 3 -or $displayBytes[0] -ne 0xEF -or $displayBytes[1] -ne 0xBB -or $displayBytes[2] -ne 0xBF) { throw 'NSIS 许可阅读文件必须包含 UTF-8 BOM' }
                $licenseText = Get-Content -LiteralPath (Join-Path $appRoot 'LICENSE') -Raw
                if ([Text.Encoding]::UTF8.GetString($displayBytes, 3, $displayBytes.Length - 3) -cne $licenseText) { throw 'NSIS 阅读文本必须等于实际载荷 LICENSE' }
                $script:PackageTest.Payload = @{
                    License = $licenseText
                    Notices = Get-Content -LiteralPath (Join-Path $appRoot 'THIRD-PARTY-NOTICES.md') -Raw
                    Aggregate = Get-Content -LiteralPath (Join-Path $appRoot 'THIRD-PARTY-NOTICES.txt') -Raw
                    ManifestHash = (Get-FileHash -LiteralPath (Join-Path $appRoot 'manifest.json')).Hash
                }
            }
            $outputs = @($Arguments | Where-Object { $_.StartsWith('/DPBI_OUTFILE=', [StringComparison]::Ordinal) })
            if ($outputs.Count -ne 1) { throw '必须传递唯一输出文件' }
            [IO.File]::WriteAllText($outputs[0].Substring('/DPBI_OUTFILE='.Length), 'synthetic NSIS output; not executable')
            if ($script:PackageTest.LicenseChangeOnCompile) {
                Add-Content -LiteralPath $script:PackageTest.LicenseChangeOnCompile -Value 'concurrent source license edit' -Encoding utf8
            }
        }
        <# .SYNOPSIS 在第一次复制前替换完整有效版本, 模拟源目录在初始校验后被另一次发布交付. #>
        function script:Copy-Item {
            param([string]$LiteralPath, [string]$Destination, [switch]$Force)
            if ($script:PackageTest.SwapField -and $LiteralPath.StartsWith($script:PackageTest.Source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                $manifestPath = Join-Path $script:PackageTest.Source 'manifest.json'
                $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                $manifest.($script:PackageTest.SwapField) = $script:PackageTest.SwapValue
                $script:PackageTest.SwapField = ''
                $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
            }
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
        }
        <# .SYNOPSIS 只在 package 自己的 staging 清理阶段注入失败, 其他文件操作保持真实行为. #>
        function script:Remove-Item {
            param([string]$LiteralPath, [switch]$Recurse, [switch]$Force)
            if ($script:PackageTest.CleanupFailure -and [IO.Path]::GetFileName($LiteralPath) -match '^\.pbi-utils-installer-[0-9a-f]{32}$') { throw '模拟安装包临时清理失败' }
            Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Recurse:$Recurse -Force:$Force
        }
        <# .SYNOPSIS 为非可执行的测试文件返回明确的未签名状态, 不伪造可信签名. #>
        function script:Get-AuthenticodeSignature([string]$LiteralPath) { return [pscustomobject]@{ Status = 'NotSigned' } }
    } $state

    $baseName = 'pbi-utils-1.2.3-win-x64-without-dotnet-setup'
    if ($Scenario -in @('PackageFailure', 'LicenseRefreshFailure')) {
        # 失败用例仅要求旧交付字节保持; 直接放入明确替身, 不重复成功用例已覆盖的编译过程.
        $null = New-Item -ItemType Directory -Path $destination -Force
        foreach ($suffix in @('exe', 'json', 'sha256')) { Set-Content -LiteralPath (Join-Path $destination "$baseName.$suffix") -Value "previous synthetic delivery $suffix" }
    } else {
        $result = Invoke-TestPackage
        Assert ($result -eq 0) ('有效 Release All 快照应能完成隔离打包: ' + (($state.Messages | ForEach-Object Message) -join '; '))
        Assert ($state.Commands.Count -eq 1) '一次 package 只能编译一个选中模式安装包'
        $arguments = $state.Commands[0].Arguments
        Assert ($arguments -contains '/V3' -and $arguments -contains '/WX') 'NSIS 必须启用既定日志等级并把警告视为失败'
        Assert ($arguments -contains '/INPUTCHARSET' -and $arguments -contains 'UTF8') 'NSIS 必须显式按 UTF-8 读取双语安装向导源码'
        Assert ($arguments -contains '/DPBI_NSIS_VERSION=3.13') '编译器版本必须绑定载荷中审核过的 NSIS 许可版本'
        Assert (@($arguments | Where-Object { $_ -clike '/DPBI_PAYLOAD=*' }).Count -eq 1) '含空格路径必须作为完整的单个 NSIS 定义参数传递'
        Assert (@($state.Messages | Where-Object { $_.Message -match '^完成 package \(' -and $_.Level -eq 'Success' }).Count -eq 1) 'dot-source 打包脚本不得把完成动作名覆盖为交付文件名'
        $record = Get-Content -LiteralPath (Join-Path $destination "$baseName.json") -Raw | ConvertFrom-Json
        Assert ($record.version -ceq '1.2.3' -and $record.dotNetMode -ceq 'without-dotnet' -and $record.signatureStatus -ceq 'NotSigned') '安装记录必须保存当前版本、模式和真实签名分类'
        Assert ($record.sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $destination "$baseName.exe") -Algorithm SHA256).Hash) '安装记录必须对应实际交付字节'
        Assert ($record.bytes -eq (Get-Item -LiteralPath (Join-Path $destination "$baseName.exe")).Length -and $record.giteeAttachmentLimitBytes -eq 100000000 -and $record.fitsGiteeAttachmentLimit) 'Gitee 保守阈值报告必须对应实际包体积'
    }
    Assert-TestPackageReleased $destination
    $before = Get-TestPackageSnapshot $destination

    $publishedBefore = Get-TestPublishedSnapshot $source
    if ($Scenario -eq 'LicenseRefresh') {
        $inputManifestHash = (Get-FileHash -LiteralPath (Join-Path $source 'manifest.json')).Hash
        # 保持 mtime 不变但改变内容, 连续两轮证明每次 package 都读取当前源文件而非旧构建副本或时间缓存.
        foreach ($revision in @(1, 2)) {
            foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/nsis.txt')) {
                $currentPath = Join-Path $sandbox $name
                $originalTime = (Get-Item -LiteralPath $currentPath).LastWriteTimeUtc
                Set-Content -LiteralPath $currentPath -Value "当前许可 / current license revision $revision - $name" -Encoding utf8
                (Get-Item -LiteralPath $currentPath).LastWriteTimeUtc = $originalTime
            }
            $catalogPath = Join-Path $sandbox 'licenses/catalog.json'
            $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
            $catalog.resources[0].files[0].sha256 = (Get-FileHash -LiteralPath (Join-Path $sandbox 'licenses/nsis.txt')).Hash
            $catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $catalogPath -Encoding utf8
            Assert ((Invoke-TestPackage) -eq 0) ('只修改许可后重新打包应成功: ' + (($state.Messages | ForEach-Object Message) -join '; '))
            Assert ($state.Payload.License -ceq (Get-Content -LiteralPath (Join-Path $sandbox 'LICENSE') -Raw)) 'NSIS 载荷必须使用本次仓库 LICENSE, 不能沿用构建时旧副本'
            Assert ($state.Payload.Notices -ceq (Get-Content -LiteralPath (Join-Path $sandbox 'THIRD-PARTY-NOTICES.md') -Raw)) '来源声明必须与当前仓库同步'
            foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/nsis.txt')) {
                Assert ($state.Payload.Aggregate.Contains("current license revision $revision - $name")) "完整聚合声明必须包含本轮许可: $name"
            }
            Assert ($state.Payload.Aggregate.Contains('obsolete resource license marker')) '已构建资源仍随载荷分发时必须保留其许可全文'
            $record = Get-Content -LiteralPath (Join-Path $destination "$baseName.json") -Raw | ConvertFrom-Json
            Assert ($record.inputManifestSha256 -ceq $inputManifestHash) '旁车必须记录原始发布快照身份'
            Assert ($record.sourceManifestSha256 -ceq $state.Payload.ManifestHash -and $record.sourceManifestSha256 -cne $inputManifestHash) '旁车必须绑定更新许可后的实际安装载荷清单'
            Assert ((Get-TestPublishedSnapshot $source) -ceq $publishedBefore) '刷新安装许可不得写回原始发布目录或改变其清单'
            Assert-TestPackageReleased $destination
        }
        Write-Host 'PASS installer current license refresh and source isolation'
        return
    }

    if ($Scenario -eq 'LicenseRefreshFailure') {
        $catalogPath = Join-Path $sandbox 'licenses/catalog.json'
        $catalogBytes = [IO.File]::ReadAllBytes($catalogPath)
        try {
            $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
            $catalog.resources = @($catalog.resources | Where-Object name -CNE 'Removed-Resource')
            $catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $catalogPath -Encoding utf8
            Assert ((Invoke-TestPackage) -ne 0) '旧载荷仍携带构建资源时删除其许可清单必须要求重新构建'
            Assert ($state.Commands.Count -eq 0 -and (Get-TestPackageSnapshot $destination) -ceq $before) '资源身份变化不得编译或覆盖旧安装包'
            Assert ((Get-TestPublishedSnapshot $source) -ceq $publishedBefore) '资源身份拒绝不得修改原始发布目录'
            Assert-TestPackageReleased $destination
        } finally { [IO.File]::WriteAllBytes($catalogPath, $catalogBytes) }
        foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/nsis.txt')) {
            $currentPath = Join-Path $sandbox $name
            $currentBytes = [IO.File]::ReadAllBytes($currentPath)
            try {
                Remove-Item -LiteralPath $currentPath
                Assert ((Invoke-TestPackage) -ne 0) "仓库许可缺失时不得沿用构建副本: $name"
                Assert ($state.Commands.Count -eq 0 -and (Get-TestPackageSnapshot $destination) -ceq $before) '刷新许可失败不得编译或覆盖旧安装包'
                Assert ((Get-TestPublishedSnapshot $source) -ceq $publishedBefore) '刷新许可失败不得修改原始发布目录'
                Assert-TestPackageReleased $destination
            } finally { [IO.File]::WriteAllBytes($currentPath, $currentBytes) }
        }
        foreach ($name in @('web/package.json', 'web/pnpm-lock.yaml', 'licenses/nsis.txt')) {
            $currentPath = Join-Path $sandbox $name
            $currentBytes = [IO.File]::ReadAllBytes($currentPath)
            try {
                Add-Content -LiteralPath $currentPath -Value ' ' -Encoding utf8
                Assert ((Invoke-TestPackage) -ne 0) "依赖输入或审核过的许可哈希变化必须拒绝旧产物打包: $name"
                Assert ($state.Commands.Count -eq 0 -and (Get-TestPackageSnapshot $destination) -ceq $before) '依赖输入或许可哈希漂移不得编译或覆盖旧安装包'
                Assert ((Get-TestPublishedSnapshot $source) -ceq $publishedBefore) '输入漂移拒绝不得修改原始发布目录'
                Assert-TestPackageReleased $destination
            } finally { [IO.File]::WriteAllBytes($currentPath, $currentBytes) }
        }
        $optionalDirectory = [IO.Path]::GetFullPath((Join-Path $sandbox 'web/node_modules/license-fixture/node_modules/license-optional'))
        Assert ($optionalDirectory.StartsWith([IO.Path]::GetFullPath($sandbox) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '可选依赖测试只能修改本轮隔离目录'
        $optionalFiles = @{}
        foreach ($file in Get-ChildItem -LiteralPath $optionalDirectory -File) { $optionalFiles[$file.Name] = [IO.File]::ReadAllBytes($file.FullName) }
        try {
            foreach ($fileName in $optionalFiles.Keys) { Remove-Item -LiteralPath (Join-Path $optionalDirectory $fileName) }
            Remove-Item -LiteralPath $optionalDirectory
            Assert ((Invoke-TestPackage) -ne 0) '根声明和锁不变但已安装 optional 闭包变化时必须拒绝旧构建产物'
            Assert (@($state.Messages | Where-Object Message -Like '*依赖闭包已改变*重新构建*').Count -eq 1) '闭包漂移必须报告依赖变化及重新构建要求'
            Assert ($state.Commands.Count -eq 0 -and (Get-TestPackageSnapshot $destination) -ceq $before) '闭包漂移不得编译或覆盖已有安装包'
            Assert ((Get-TestPublishedSnapshot $source) -ceq $publishedBefore) '闭包漂移不得修改原始发布目录'
            Assert-TestPackageReleased $destination
        } finally {
            $null = New-Item -ItemType Directory -Path $optionalDirectory -Force
            foreach ($fileName in $optionalFiles.Keys) { [IO.File]::WriteAllBytes((Join-Path $optionalDirectory $fileName), $optionalFiles[$fileName]) }
        }
        $changingLicense = Join-Path $sandbox 'LICENSE'
        $currentLicenseBytes = [IO.File]::ReadAllBytes($changingLicense)
        $state.LicenseChangeOnCompile = $changingLicense
        try {
            Assert ((Invoke-TestPackage) -eq 4) '编译期间源许可变化必须拒绝交付本轮安装包'
            Assert ($state.Commands.Count -eq 1) '并发许可变化用例必须在真实打包流程进入编译后发生'
            Assert (@($state.Messages | Where-Object Message -Like '*许可来源已改变*').Count -eq 1) '并发许可变化必须说明拒绝交付原因'
            Assert ((Get-TestPackageSnapshot $destination) -ceq $before) '并发许可变化不得覆盖已有安装包及旁车'
            Assert ((Get-TestPublishedSnapshot $source) -ceq $publishedBefore) '并发许可变化不得修改原始发布目录'
            Assert-TestPackageReleased $destination
        } finally {
            $state.LicenseChangeOnCompile = ''
            [IO.File]::WriteAllBytes($changingLicense, $currentLicenseBytes)
        }

        Write-Host 'PASS installer license input drift, missing sources and concurrent edits rejected'
        return
    }

    if ($Scenario -eq 'Package') {
        $manifestPath = Join-Path $source 'manifest.json'
        $originalManifest = [IO.File]::ReadAllBytes($manifestPath)
        # 即使攻击/遗漏同时重写发布 manifest, 许可独立校验仍拒绝缺少必需法律文档的包.
        foreach ($relative in @('LICENSE', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
            $licensePath = Join-Path $source $relative
            $originalBytes = [IO.File]::ReadAllBytes($licensePath)
            try {
                Remove-Item -LiteralPath $licensePath
                $changedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                $changedManifest.files = @($changedManifest.files | Where-Object file -CNE $relative)
                $changedManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
                Assert ((Invoke-TestPackage) -ne 0) "缺失许可文件必须拒绝: $relative"
                Assert ($state.Commands.Count -eq 0 -and (Get-TestPackageSnapshot $destination) -ceq $before) '缺少许可时不得编译或覆盖上次安装包'
                Assert-TestPackageReleased $destination
            } finally {
                [IO.File]::WriteAllBytes($licensePath, $originalBytes)
                [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
            }
        }
        foreach ($change in @(@{ Field = 'version'; Value = '2.0.0' }, @{ Field = 'sdkVersion'; Value = '10.0.402' })) {
            $state.SwapField = $change.Field
            $state.SwapValue = $change.Value
            try {
                Assert ((Invoke-TestPackage) -eq 4) '并发替换为另一产品或 SDK 版本的有效快照必须明确失败'
                Assert ($state.Commands.Count -eq 0) '快照身份改变时不得继续编译安装器'
                Assert (@($state.Messages | Where-Object { $_.Message -like '*复制期间*版本*' }).Count -eq 1) '快照拒绝必须说明版本身份发生变化'
                Assert ((Get-TestPackageSnapshot $destination) -ceq $before) '快照身份拒绝不得覆盖上次安装包和旁车文件'
                Assert-TestPackageReleased $destination
            } finally { [IO.File]::WriteAllBytes($manifestPath, $originalManifest) }
        }
        foreach ($sdk in @('10.0.402', '')) {
            try {
                $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                if ($sdk) { $manifest.sdkVersion = $sdk } else { $manifest.PSObject.Properties.Remove('sdkVersion') }
                $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
                Assert ((Invoke-TestPackage) -eq 3) '产物 SDK 缺失或不匹配当前 global 时必须提示重新构建'
                Assert ($state.Commands.Count -eq 0 -and (Get-TestPackageSnapshot $destination) -ceq $before) 'SDK 拒绝不得编译或修改旧安装包'
                Assert-TestPackageReleased $destination
            } finally { [IO.File]::WriteAllBytes($manifestPath, $originalManifest) }
        }

        Write-Host 'PASS installer package snapshot identity and SDK boundaries'
        return
    }

    $state.CompileFailure = $true
    try {
        Assert ((Invoke-TestPackage) -eq 47) 'NSIS 编译失败必须保留工具退出码'
        Assert (@($state.Messages | Where-Object { $_.Message -like '*[[]package[]]*模拟 makensis 编译失败*' }).Count -eq 1) '编译错误必须保留 package 阶段与原始原因'
        Assert (@($state.Messages | Where-Object { $_.Message -match '^完成 ' }).Count -eq 0) '编译失败不得打印成功完成'
        Assert ((Get-TestPackageSnapshot $destination) -ceq $before) '编译失败不得覆盖已有安装包、JSON 或校验文件'
        Assert-TestPackageReleased $destination
    } finally { $state.CompileFailure = $false }

    $lease = [IO.File]::Open((Join-Path $destination '.package.lock'), 'Open', 'ReadWrite', 'None')
    try {
        Assert ((Invoke-TestPackage) -eq 3) '另一任务持有打包锁时必须拒绝本轮打包'
        Assert ($state.Commands.Count -eq 0 -and (Get-TestPackageSnapshot $destination) -ceq $before) '锁冲突不得编译或修改旧包'
    } finally { $lease.Dispose() }
    Assert-TestPackageReleased $destination
    $state.CleanupFailure = $true
    try {
        Assert ((Invoke-TestPackage) -eq 4) '交付成功但临时清理失败仍必须返回非零'
        Assert ($state.ErrorOutput -like '*安装包已交付*清理失败*保留于*') '清理单独失败必须报告实际已交付状态与残留位置'
        Assert (@($state.Messages | Where-Object { $_.Message -match '^完成 ' }).Count -eq 0) '清理失败不得打印动作完成'
        foreach ($suffix in @('exe', 'json', 'sha256')) { Assert (Test-Path -LiteralPath (Join-Path $destination "$baseName.$suffix") -PathType Leaf) '清理单独失败后已交付的三个文件必须保留' }
        $gate = [IO.File]::Open((Join-Path $destination '.package.lock'), 'Open', 'ReadWrite', 'None')
        $gate.Dispose()
    } finally {
        $state.CleanupFailure = $false
        Remove-TestPackageTemporaries $destination
    }
    $before = Get-TestPackageSnapshot $destination
    $state.CleanupFailure = $true
    $state.CompileFailure = $true
    try {
        Assert ((Invoke-TestPackage) -eq 47) '编译与清理同时失败时必须保留最先工具退出码'
        Assert ($state.ErrorOutput -like '*清理失败*保留原错误*') '第二次清理错误必须报告且不覆盖编译首因'
        Assert ((Get-TestPackageSnapshot $destination) -ceq $before) '双重失败仍不得覆盖旧包'
        $gate = [IO.File]::Open((Join-Path $destination '.package.lock'), 'Open', 'ReadWrite', 'None')
        $gate.Dispose()
    } finally {
        $state.CleanupFailure = $false
        $state.CompileFailure = $false
        Remove-TestPackageTemporaries $destination
    }
    Assert-TestPackageReleased $destination
    Write-Host 'PASS installer compiler failure, lock release and cleanup failure preservation'
}
finally {
    Set-Location -LiteralPath $savedDirectory
    if ($null -ne $registry) { Remove-Module $registry -Force }
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ([IO.Path]::GetDirectoryName($absolute) -ine $temporaryRoot -or [IO.Path]::GetFileName($absolute) -notmatch '^pbi-utils package [0-9a-f]{32}$') { throw '安装包测试清理目录越界' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
