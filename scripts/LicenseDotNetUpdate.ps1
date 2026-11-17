# FilePath    : pbi-utils\scripts\LicenseDotNetUpdate.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 按 SDK 实际解析的发布资产核对 apphost 和 Windows 运行时许可, 仅更新内存清单.

. (Join-Path $PSScriptRoot 'LicenseDotNetArchive.ps1')

<#
.SYNOPSIS 查询当前 SDK 对项目的发布资产选择.
.PARAMETER Project 产品入口项目目录.
.PARAMETER SelfContained 与正式发布对应的部署参数, 使用 true 或 false.
.OUTPUTS SDK 工具链及选定资产; MSBuild 失败或输出无法解析时抛错.
#>
function Get-PbiDotNetLicenseAssets([string]$Project, [string]$SelfContained) {
    $arguments = @(
        'msbuild', $Project, '-nologo', '-t:ResolveFrameworkReferences,ResolveRuntimePackAssets',
        '-getProperty:NetCoreRoot,NETCoreSdkVersion,BundledNETCoreAppPackageVersion,NETCoreSdkRuntimeIdentifier',
        '-getItem:AppHostPack,ResolvedRuntimePack,RuntimePackAsset',
        '-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64', "-p:SelfContained=$SelfContained",
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishAot=false'
    )
    $PSNativeCommandUseErrorActionPreference = $false
    $result = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "无法读取 .NET SDK 发布许可输入: $Project (SelfContained=$SelfContained, 退出码 $LASTEXITCODE)`n$($result -join "`n")"
    }
    try { return ($result -join "`n" | ConvertFrom-Json -AsHashtable) }
    catch { throw "无法解析 .NET SDK 发布许可输入: $Project (SelfContained=$SelfContained)。$($_.Exception.Message)" }
}

<#
.SYNOPSIS 从已恢复的运行时包元数据读取上游固定提交.
.PARAMETER Pack SDK 解析出的运行时包身份、版本及包目录.
.OUTPUTS 完整 Git 提交; 包身份、声明或官方仓库不符时抛错.
#>
function Get-PbiDotNetLicensePackCommit($Pack) {
    $id = [string]$Pack.NuGetPackageId
    $directory = [string]$Pack.PackageDirectory
    if (!$directory -or $Pack.NuGetPackageVersion -cnotmatch '^\d+\.\d+\.\d+$') {
        throw ".NET 运行时包缺失或使用未经审核的版本: $id/$($Pack.NuGetPackageVersion)"
    }
    $nuspecPath = Get-PbiLicenseSafePath $directory (Join-Path $directory ($id.ToLowerInvariant() + '.nuspec'))
    [xml]$spec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $repo = $metadata.SelectSingleNode('*[local-name()="repository"]')
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -ine $id -or
        $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $Pack.NuGetPackageVersion -or
        !$license -or $license.GetAttribute('type') -cne 'expression' -or $license.InnerText -cne 'MIT' -or
        !$repo -or $repo.GetAttribute('url') -cne 'https://github.com/dotnet/dotnet' -or
        $repo.GetAttribute('commit') -cnotmatch '^[0-9a-f]{40}$') {
        throw ".NET 运行时包的身份、MIT 声明或官方提交变化, 请核对许可来源: $id/$($Pack.NuGetPackageVersion)"
    }
    return $repo.GetAttribute('commit')
}

<#
.SYNOPSIS 从已审核条目定位唯一文档.
.PARAMETER Entry 对应的 apphost 或运行时清单条目.
.PARAMETER FileName 稳定的跟踪路径文件名.
.OUTPUTS 文档条目; 缺失或重复时抛错.
#>
function Get-PbiDotNetLicenseDocument($Entry, [string]$FileName) {
    $documents = @($Entry.files | Where-Object { [IO.Path]::GetFileName($_.path) -ceq $FileName })
    if ($documents.Count -ne 1) { throw ".NET 许可清单缺失或重复: $($Entry.name)/$FileName" }
    return $documents[0]
}

<#
.SYNOPSIS 同一来源不重复下载, 来源变化时核验官方原文.
.PARAMETER RepositoryRoot 许可原文所在仓库.
.PARAMETER Document 已确认身份的许可文档.
.PARAMETER Source 新版官方固定提交对应的原文 URL.
.OUTPUTS 无; 条款变化或下载失败时抛错, 原文相同则更新来源.
#>
function Update-PbiDotNetLicenseDocument([string]$RepositoryRoot, $Document, [string]$Source) {
    if ($Document.source -cne $Source) {
        $bytes = Get-PbiLicenseUpdateBytes ([uri]$Source)
        Confirm-PbiLicenseUpdateContent $RepositoryRoot $Document $bytes $Source
    }
}

<#
.SYNOPSIS 核验两种部署模式的 apphost, 按实际自包含运行资产更新三项版本和许可来源.
.PARAMETER RepositoryRoot 产品仓库根目录.
.PARAMETER Catalog 本轮尚未写入的许可证清单.
.PARAMETER Context 包含已恢复的产品入口 Projects.
.OUTPUTS 无; SDK 资产不一致、许可映射失效或条款改变时抛错, 不写工作区.
#>
function Update-PbiDotNetLicenseCatalog([string]$RepositoryRoot, [System.Collections.IDictionary]$Catalog, [hashtable]$Context) {
    $appHostId = 'Microsoft.NETCore.App.Host.win-x64'
    $coreId = 'Microsoft.NETCore.App.Runtime.win-x64'
    $desktopId = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
    $expectedSdk = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $assetNames = @{}
    $runtimePacks = @{}
    $appHostPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $windowsSdkPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $windowsSdkFile = 'D3DCompiler_47_cor3.dll'
    $sdk = $null
    $appHostVersion = $null
    Push-Location -LiteralPath $RepositoryRoot
    try {
        foreach ($project in $Context.Projects) {
            foreach ($selfContained in @('false', 'true')) {
                $selection = Get-PbiDotNetLicenseAssets $project $selfContained
                $properties = $selection.Properties
                if ($properties.NETCoreSdkVersion -cne $expectedSdk -or $properties.NETCoreSdkRuntimeIdentifier -cne 'win-x64' -or
                    !$properties.NetCoreRoot -or ($sdk -and $properties.NetCoreRoot -ine $sdk.NetCoreRoot)) {
                    throw "查询的 .NET SDK 与产品 x64 SDK 不符: $project (要求 $expectedSdk)"
                }
                $sdk = $properties
                $appHost = @($selection.Items.AppHostPack)
                if ($appHost.Count -ne 1 -or $appHost[0].RuntimeIdentifier -cne 'win-x64' -or
                    !$appHost[0].PackageDirectory -or ![IO.File]::Exists($appHost[0].Path)) {
                    throw "SDK 未为 $project (SelfContained=$selfContained) 选定唯一的 Windows x64 apphost"
                }
                $packDirectory = Split-Path $appHost[0].PackageDirectory -Parent
                $version = Split-Path $appHost[0].PackageDirectory -Leaf
                if ((Split-Path $packDirectory -Leaf) -ine $appHostId -or $version -cne $sdk.BundledNETCoreAppPackageVersion -or
                    ($appHostVersion -and $version -cne $appHostVersion)) {
                    throw "apphost 来源与已审核的 SDK 捆绑运行时不一致: $($appHost[0].PackageDirectory)"
                }
                $appHostVersion = $version
                $null = $appHostPaths.Add((Get-PbiLicenseSafePath $appHost[0].PackageDirectory $appHost[0].Path))
                foreach ($group in ($selection.Items.RuntimePackAsset | Group-Object NuGetPackageId)) {
                    $id = $group.Name
                    $pack = @($selection.Items.ResolvedRuntimePack | Where-Object NuGetPackageId -CEQ $id)
                    if ($selfContained -cne 'true' -or $id -cnotin @($coreId, $desktopId) -or $pack.Count -ne 1 -or
                        $pack[0].RuntimeIdentifier -cne 'win-x64' -or $pack[0].NuGetPackageVersion -cne $appHostVersion -or
                        ($runtimePacks.ContainsKey($id) -and $runtimePacks[$id].PackageDirectory -ine $pack[0].PackageDirectory)) {
                        throw "发布运行时包超出已审核的 SDK 许可映射: $project/$id"
                    }
                    $runtimePacks[$id] = $pack[0]
                    foreach ($asset in ($group.Group | Where-Object AssetType -CEQ 'native')) {
                        $assetNames[$id] += @($asset.DestinationSubPath)
                        if ($id -ceq $desktopId -and $asset.DestinationSubPath -ceq $windowsSdkFile) {
                            $null = $windowsSdkPaths.Add((Get-PbiLicenseSafePath $pack[0].PackageDirectory $asset.Identity))
                        }
                    }
                }
            }
        }
    }
    finally { Pop-Location }

    # 核对已经审核的原生分发范围, 诊断组件 mscordaccore 的版本化文件名会随补丁改变.
    $mappedNativeFiles = @{
        $coreId = @('coreclr.dll', 'Microsoft.DiaSymReader.Native.amd64.dll', 'System.IO.Compression.Native.dll',
            'clretwrc.dll', 'clrgc.dll', 'clrgcexp.dll', 'clrjit.dll', 'createdump.exe', 'hostfxr.dll', 'hostpolicy.dll',
            'mscordaccore.dll', 'mscordbi.dll', 'mscorrc.dll', 'msquic.dll')
        $desktopId = @($windowsSdkFile, 'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll')
    }
    foreach ($id in @($coreId, $desktopId)) {
        $actual = @($assetNames[$id] | Sort-Object -Unique)
        if ($id -ceq $coreId) { $actual = @($actual | Where-Object { $_ -cnotmatch '^mscordaccore_amd64_amd64_\d+\.\d+\.\d+\.\d+\.dll$' }) }
        $different = @($mappedNativeFiles[$id] | Where-Object { $_ -cnotin $actual }) + @($actual | Where-Object { $_ -cnotin $mappedNativeFiles[$id] })
        if ($different.Count) { throw "运行时原生资产许可映射变化, 请核对 $id 中的 $($different -join ', ')" }
    }
    # 此 PE 已核对 10.0.14 和 10.0.15 随包字节相同, 对应现有 Windows SDK EULA.
    foreach ($path in $windowsSdkPaths) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne 'A05F99734F7C4822FEFC12B367AF21FD0976ED6608752FB1E1E80B6ECE7ECBBB') {
            throw "Windows SDK 的 $windowsSdkFile 已变化, 请重新核对对应分发条款"
        }
    }
    $coreCommit = Get-PbiDotNetLicensePackCommit $runtimePacks[$coreId]
    $desktopCommit = Get-PbiDotNetLicensePackCommit $runtimePacks[$desktopId]
    $appHostEntries = @($Catalog.resources | Where-Object name -CEQ "$appHostId apphost")
    $coreEntries = @($Catalog.packages | Where-Object { $_.ecosystem -ceq 'nuget' -and $_.name -ceq $coreId })
    $desktopEntries = @($Catalog.packages | Where-Object { $_.ecosystem -ceq 'nuget' -and $_.name -ceq $desktopId })
    if ($appHostEntries.Count -ne 1 -or $coreEntries.Count -ne 1 -or $desktopEntries.Count -ne 1) {
        throw '.NET apphost 或自包含运行时的许可清单缺失或重复'
    }
    $appHostEntry = $appHostEntries[0]
    $coreEntry = $coreEntries[0]
    $desktopEntry = $desktopEntries[0]
    foreach ($entry in @($appHostEntry, $coreEntry, $desktopEntry)) {
        $null = Get-PbiCatalogDocuments $RepositoryRoot $entry
    }
    $appHostSource = "https://raw.githubusercontent.com/dotnet/dotnet/$coreCommit/src/runtime"
    $desktopSource = "https://raw.githubusercontent.com/dotnet/dotnet/$desktopCommit/src"
    $appHostPackageSource = "https://www.nuget.org/packages/$appHostId/$appHostVersion"
    $appHostLicense = Get-PbiDotNetLicenseDocument $appHostEntry 'dotnet-apphost-MIT.txt'
    $appHostNotice = Get-PbiDotNetLicenseDocument $appHostEntry 'dotnet-apphost-NOTICES.txt'
    $wpfNotice = Get-PbiDotNetLicenseDocument $desktopEntry 'WindowsDesktop-WPF-NOTICES.txt'
    $winFormsNotice = Get-PbiDotNetLicenseDocument $desktopEntry 'WindowsDesktop-WinForms-NOTICES.txt'
    $coreLibrary = Get-PbiDotNetLicenseDocument $coreEntry 'dotnet-library.txt'
    $desktopLibrary = Get-PbiDotNetLicenseDocument $desktopEntry 'dotnet-library.txt'
    if ($coreLibrary.path -cne $desktopLibrary.path -or $coreLibrary.sha256 -cne $desktopLibrary.sha256) {
        throw '.NET Core 和 Windows Desktop 运行时必须引用同一份 .NET Library 条款原文'
    }
    $windowsSdkLicense = Get-PbiDotNetLicenseDocument $desktopEntry 'Windows-SDK-Software-License.txt'
    if ($windowsSdkLicense.source -cnotmatch '^https://raw\.githubusercontent\.com/microsoft/win32metadata/[0-9a-f]{40}/licenses/sdk_license\.txt \(D3DCompiler_47_cor3\.dll; EULAID:WIN10SDK\.RTM\.AUG_2018_en-US\)$') {
        throw "Windows SDK 的 $windowsSdkFile 分发许可来源变化, 请核对对应条款"
    }
    # 已审核的精确版本可继续离线使用, SDK 或 apphost 来源变化才核验官方 ZIP. 多版本共享安装根的 LICENSE.txt 无法证明精确 SDK 的条款.
    $reviewedArchiveUri = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$expectedSdk/dotnet-sdk-$expectedSdk-win-x64.zip"
    $archiveEvidence = '^' + [regex]::Escape($reviewedArchiveUri) + ' \(SHA-512 [A-F0-9]{128}; LICENSE\.txt\)$'
    $verifiedProvenance = $coreLibrary.source -cmatch $archiveEvidence -and $desktopLibrary.source -ceq $coreLibrary.source
    if (!$verifiedProvenance -or $appHostLicense.source -cne "$appHostSource/LICENSE.TXT" -or
        $appHostNotice.source -cne "$appHostSource/THIRD-PARTY-NOTICES.TXT" -or
        $appHostEntry.source -cne $appHostPackageSource -or
        @($appHostEntry, $coreEntry, $desktopEntry | Where-Object version -CNE $appHostVersion).Count) {
        $archive = Get-PbiDotNetArchiveLicense $RepositoryRoot $expectedSdk $appHostVersion @($appHostPaths)
        Confirm-PbiLicenseUpdateContent $RepositoryRoot $coreLibrary $archive.Bytes $archive.Source
        Confirm-PbiLicenseUpdateContent $RepositoryRoot $desktopLibrary $archive.Bytes $archive.Source
    }
    Update-PbiDotNetLicenseDocument $RepositoryRoot $appHostLicense "$appHostSource/LICENSE.TXT"
    Update-PbiDotNetLicenseDocument $RepositoryRoot $appHostNotice "$appHostSource/THIRD-PARTY-NOTICES.TXT"
    Update-PbiDotNetLicenseDocument $RepositoryRoot $wpfNotice "$desktopSource/wpf/THIRD-PARTY-NOTICES.TXT"
    Update-PbiDotNetLicenseDocument $RepositoryRoot $winFormsNotice "$desktopSource/winforms/THIRD-PARTY-NOTICES.TXT"
    $appHostEntry.version = $appHostVersion
    $appHostEntry.source = $appHostPackageSource
    $coreEntry.version = $runtimePacks[$coreId].NuGetPackageVersion
    $desktopEntry.version = $runtimePacks[$desktopId].NuGetPackageVersion
}
