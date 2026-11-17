# FilePath    : pbi-utils\scripts\LicenseDotNetArchive.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 仅在 SDK 许可来源变化时从官方精确归档核验 apphost 模板和 .NET Library 条款.

<#
.SYNOPSIS 读取官方版本、SHA-512 和 win-x64 SDK ZIP 的 LICENSE, 对比归档中的 apphost 模板.
.PARAMETER RepositoryRoot 固定 global.json 版本的仓库根目录.
.PARAMETER SdkVersion 实际解析的 SDK 版本.
.PARAMETER AppHostVersion SDK 解析的 apphost 版本.
.PARAMETER AppHostPaths 当前各入口选用的 apphost 模板路径集合.
.OUTPUTS 包含许可字节及来源的字典; 发布元数据、归档或模板不符时抛错, 清理临时 ZIP.
#>
function Get-PbiDotNetArchiveLicense([string]$RepositoryRoot, [string]$SdkVersion, [string]$AppHostVersion, [string[]]$AppHostPaths) {
    Import-Module (Join-Path $PSScriptRoot 'InstallerMetadata.psm1')
    $metadataUri = Get-PbiReleaseMetadataUri $RepositoryRoot
    $metadata = Invoke-RestMethod -Uri $metadataUri -MaximumRedirection 0 -TimeoutSec 60
    $sdkEntries = @(
        foreach ($release in $metadata.releases) {
            $sdks = if ($release.PSObject.Properties['sdks']) { $release.sdks } elseif ($release.PSObject.Properties['sdk']) { $release.sdk }
            foreach ($candidate in $sdks) {
                if ($candidate.version -ceq $SdkVersion) { $candidate }
            }
        }
    )
    if ($sdkEntries.Count -ne 1 -or $sdkEntries[0].'runtime-version' -cne $AppHostVersion) {
        throw "微软发布元数据缺失、重复或运行时版本不符: SDK $SdkVersion / apphost $AppHostVersion"
    }
    $archives = @($sdkEntries[0].files | Where-Object { $_.name -ceq 'dotnet-sdk-win-x64.zip' -and $_.rid -ceq 'win-x64' })
    $expectedUri = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-x64.zip"
    if ($archives.Count -ne 1 -or $archives[0].url -cne $expectedUri -or $archives[0].hash -notmatch '^[A-Fa-f0-9]{128}$') {
        throw "微软 SDK $SdkVersion 的 win-x64 ZIP 地址或 SHA-512 无法核验"
    }
    $release = $archives[0]
    $zipPath = [IO.Path]::GetTempFileName()
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $expectedUri -OutFile $zipPath -MaximumRedirection 0 -TimeoutSec 300
        if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA512).Hash -ine $release.hash) {
            throw "微软 SDK $SdkVersion 的 win-x64 ZIP SHA-512 与官方元数据不符"
        }
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $appHostEntryName = "packs/Microsoft.NETCore.App.Host.win-x64/$AppHostVersion/runtimes/win-x64/native/apphost.exe"
            $licenses = @($zip.Entries | Where-Object FullName -IEQ 'LICENSE.txt')
            $templates = @($zip.Entries | Where-Object FullName -IEQ $appHostEntryName)
            if ($licenses.Count -ne 1 -or $licenses[0].Length -le 0 -or $licenses[0].Length -gt 1MB -or $templates.Count -ne 1) {
                throw "微软 SDK $SdkVersion 的 win-x64 ZIP 缺少唯一的根许可证或 apphost 模板"
            }
            $templateStream = $templates[0].Open()
            try { $templateHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($templateStream)) }
            finally { $templateStream.Dispose() }
            foreach ($template in $AppHostPaths) {
                if ((Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash -cne $templateHash) {
                    throw "本机 SDK 选用的 apphost 模板与官方 SDK $SdkVersion ZIP 不符: $template"
                }
            }
            $licenseStream = $licenses[0].Open()
            $licenseBytes = [IO.MemoryStream]::new()
            try {
                $licenseStream.CopyTo($licenseBytes)
                return @{ Bytes = $licenseBytes.ToArray(); Source = "$expectedUri (SHA-512 $($release.hash.ToUpperInvariant()); LICENSE.txt)" }
            }
            finally { $licenseBytes.Dispose(); $licenseStream.Dispose() }
        }
        finally { $zip.Dispose() }
    }
    finally {
        try { [IO.File]::Delete($zipPath) }
        catch { [Console]::Error.WriteLine("无法删除本轮 SDK 许可核验临时文件: $zipPath。$($_.Exception.Message)") }
    }
}
