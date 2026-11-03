# FilePath    : pbi-utils\scripts\installer\ExternalToolDescriptor.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : Power BI 外部工具固定路径、描述文件及归属校验, 兼容 Windows PowerShell 5.1.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<# .SYNOPSIS 核验本机绝对路径及全部已存在祖先不含重解析点, 返回规范路径; 非法路径、链接或读取错误均拒绝. #>
function Resolve-PbiExternalToolPath([string]$Path) {
    if (!$Path -or ![IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\') -or $Path -match '["\x00-\x1f]') { throw '外部工具路径必须为本机绝对路径' }
    $absolute = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($absolute -eq [IO.Path]::GetPathRoot($absolute).TrimEnd('\')) { throw '外部工具路径不能使用磁盘根目录' }
    $current = $absolute
    while ($current) {
        try {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '外部工具路径包含链接或重解析点' }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $absolute
}

<# .SYNOPSIS 从系统已知目录取得官方 32 位共享注册目录, 不采用可由调用方指定的目标路径; 缺少目录契约时抛错. #>
function Get-PbiExternalToolDirectory {
    $common = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonProgramFilesX86)
    if (!$common) { throw '无法获取 Power BI 外部工具使用的 32 位 Common Files 目录' }
    return Resolve-PbiExternalToolPath (Join-Path $common 'Microsoft Shared\Power BI Desktop\External Tools')
}

<# .SYNOPSIS 仅返回开发版或安装版固定文件名, 参数为 Kind, 不接收任意文件名称. #>
function Get-PbiExternalToolRegistrationPath([ValidateSet('Development', 'Installed')][string]$Kind) {
    $name = if ($Kind -ceq 'Development') { 'pbi-utils-dev.pbitool.json' } else { 'pbi-utils.pbitool.json' }
    return Resolve-PbiExternalToolPath (Join-Path (Get-PbiExternalToolDirectory) $name)
}

<# .SYNOPSIS 由开发目标路径取得仓库身份, 仅接受正式 Debug/Release 两种模式的布局; 非发布布局抛错. #>
function Get-PbiExternalToolDevelopmentOwner([string]$ExecutablePath) {
    $path = Resolve-PbiExternalToolPath $ExecutablePath
    if ($path -notmatch '^(?<root>.+)\\artifacts\\build\\(?:debug\\win-x64|release\\win-x64\\(?:with-dotnet|without-dotnet))\\PbiUtils\.Desktop\.exe$') { throw '开发外部工具必须指向当前仓库的标准发布目录' }
    return $Matches.root
}

<# .SYNOPSIS 将已发布 SVG 字节转换为官方 iconData, 参数为图标字节, 空值或超限时抛错. #>
function ConvertTo-PbiExternalToolIcon([byte[]]$Bytes) {
    if ($null -eq $Bytes -or $Bytes.Length -eq 0 -or $Bytes.Length -gt 32768) { throw '外部工具品牌图标为空或超过限制' }
    $text = (New-Object Text.UTF8Encoding($false, $true)).GetString($Bytes)
    if ($text -notmatch '<svg[\s>]') { throw '外部工具品牌图标必须为 SVG' }
    return 'image/svg+xml;base64,' + [Convert]::ToBase64String($Bytes)
}

<# .SYNOPSIS 创建固定产品描述, 参数为类别、真实桌面目标、版本和已验证图标; 不读取目标 EXE, 避免破坏安装入口租约. #>
function New-PbiExternalToolDescriptor([ValidateSet('Development', 'Installed')][string]$Kind, [string]$ExecutablePath, [string]$Version, [string]$IconData) {
    $versionValue = $null
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or ![Version]::TryParse($Version, [ref]$versionValue)) { throw '外部工具版本无效' }
    $target = Resolve-PbiExternalToolPath $ExecutablePath
    if ([IO.Path]::GetFileName($target) -cne 'PbiUtils.Desktop.exe') { throw '外部工具只能启动 PbiUtils.Desktop.exe' }
    if ($Kind -ceq 'Development') { $null = Get-PbiExternalToolDevelopmentOwner $target }
    elseif ([IO.Path]::GetFileName([IO.Path]::GetDirectoryName($target)) -cne 'app') { throw '安装外部工具必须指向安装目录内的 app 入口' }
    if (!$IconData.StartsWith('image/svg+xml;base64,', [StringComparison]::Ordinal)) { throw '外部工具图标格式无效' }
    $bytes = [Convert]::FromBase64String($IconData.Substring('image/svg+xml;base64,'.Length))
    if ((ConvertTo-PbiExternalToolIcon $bytes) -cne $IconData) { throw '外部工具图标内容无效' }
    return [ordered]@{
        version = $Version
        name = $(if ($Kind -ceq 'Development') { 'pbi-utils (开发)' } else { 'pbi-utils' })
        description = 'Power BI Desktop model utilities by jiaopengzi.'
        path = $target
        arguments = '--server "%server%" --database "%database%"'
        iconData = $IconData
    }
}

<# .SYNOPSIS 将严格生成的描述编码为可跨原生参数边界传递的 UTF-8 base64, 参数为描述对象, 返回编码文本. #>
function ConvertTo-PbiExternalToolContent($Descriptor) {
    return [Convert]::ToBase64String((New-Object Text.UTF8Encoding($false)).GetBytes(($Descriptor | ConvertTo-Json -Depth 4)))
}

<# .SYNOPSIS 校验完整描述的字段、类型及生成语义, 参数为类别和 base64 字节; 不接受未知字段、重复字段或任意启动参数. #>
function Read-PbiExternalToolDescriptor([ValidateSet('Development', 'Installed')][string]$Kind, [string]$Content) {
    if (!$Content -or $Content.Length -gt 65536) { throw '外部工具登记内容无效或超限' }
    $bytes = [Convert]::FromBase64String($Content)
    $json = (New-Object Text.UTF8Encoding($false, $true)).GetString($bytes)
    $descriptor = $json | ConvertFrom-Json
    $expected = @('arguments', 'description', 'iconData', 'name', 'path', 'version')
    if ((@($descriptor.PSObject.Properties.Name | Sort-Object) -join ',') -cne ($expected -join ',')) { throw '同名外部工具不具有本产品的完整登记字段' }
    foreach ($name in $expected) {
        if ($descriptor.$name -isnot [string]) { throw '外部工具登记字段类型无效' }
        # 生成内容的值不存在结构性引号; 对字段名计数可拒绝 ConvertFrom-Json 会合并的重复字段.
        if ([Regex]::Matches($json, '"' + $name + '"\s*:', [Text.RegularExpressions.RegexOptions]::IgnoreCase).Count -ne 1) { throw '外部工具登记包含重复字段' }
    }
    $canonical = New-PbiExternalToolDescriptor $Kind $descriptor.path $descriptor.version $descriptor.iconData
    foreach ($name in $expected) { if ($canonical[$name] -cne $descriptor.$name) { throw '同名外部工具内容不属于本产品, 已保留' } }
    return $descriptor
}

<# .SYNOPSIS 核验两个完整描述属于同一安装或同一开发仓库, 图标必须仍是本产品生成值; 不匹配返回 false. #>
function Test-PbiExternalToolOwner([string]$Kind, $Existing, $Desired) {
    if ($Existing.iconData -cne $Desired.iconData) { return $false }
    if ($Kind -ceq 'Development') { return (Get-PbiExternalToolDevelopmentOwner $Existing.path) -ieq (Get-PbiExternalToolDevelopmentOwner $Desired.path) }
    return $Existing.path -ieq $Desired.path
}

Export-ModuleMember -Function Resolve-PbiExternalToolPath, Get-PbiExternalToolRegistrationPath, Get-PbiExternalToolDevelopmentOwner, ConvertTo-PbiExternalToolIcon, New-PbiExternalToolDescriptor, ConvertTo-PbiExternalToolContent, Read-PbiExternalToolDescriptor, Test-PbiExternalToolOwner
