# FilePath    : pbi-utils\scripts\installer\ExternalToolFiles.psm1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 固定外部工具登记的独占句柄事务, 在同一文件租约内核验、写入、删除及失败恢复.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ExternalToolDescriptor.psm1')

<# .SYNOPSIS 初始化最小 Windows 文件句柄适配, 支持持有独占租约时删除当前文件, 不通过路径删除后来替换的文件. #>
function Initialize-PbiExternalToolNative {
    if ('PbiUtils.ExternalToolFiles' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace PbiUtils {
    // 固定登记模块已约束路径; 此适配只提供独占文件句柄和句柄自身的删除标记.
    public static class ExternalToolFiles {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref int information, uint size);
        // 只打开指定文件, 请求 DELETE 以便删除仍持有独占租约的同一个对象; 不共享读写或删除.
        public static FileStream Open(string path, bool create) {
            SafeFileHandle handle = CreateFile(path, 0xC0010000, 0, IntPtr.Zero, create ? 1u : 3u, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) {
                int code = Marshal.GetLastWin32Error(); handle.Dispose();
                if (code == 5) throw new UnauthorizedAccessException("外部工具登记目录没有写入权限.");
                throw new IOException("无法独占外部工具登记文件: " + new Win32Exception(code).Message);
            }
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        // 设置当前句柄所指文件的删除标记; 文件在租约关闭时删除, 失败保留句柄并抛错.
        public static void Delete(FileStream stream) {
            int remove = 1;
            if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref remove, 4)) throw new IOException("无法删除外部工具登记: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }
}
'@
}

<# .SYNOPSIS 读取最多 48 KiB 的登记字节, 参数为流, 返回 base64; 超限或读取异常拒绝, 不解析不可信文本. #>
function Read-PbiExternalToolStream([IO.Stream]$Stream) {
    if ($Stream.Length -gt 49152) { throw '外部工具登记内容超过限制, 未作更改' }
    $Stream.Position = 0
    $memory = New-Object IO.MemoryStream
    try { $Stream.CopyTo($memory); return [Convert]::ToBase64String($memory.ToArray()) }
    finally { $memory.Dispose(); $Stream.Position = 0 }
}

<# .SYNOPSIS 只读捕获固定登记文件, 不存在返回 absent; 目录、链接、占用或读取失败均拒绝. #>
function Get-PbiExternalToolContent([ValidateSet('Development', 'Installed')][string]$Kind) {
    $path = Get-PbiExternalToolRegistrationPath $Kind
    if ([IO.Directory]::Exists($path)) { throw '同名外部工具路径为目录, 已保留' }
    try { $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
    catch [IO.FileNotFoundException] { return 'absent' }
    catch [IO.DirectoryNotFoundException] { return 'absent' }
    try { return Read-PbiExternalToolStream $stream } finally { $stream.Dispose() }
}

<# .SYNOPSIS 在已持有的独占流中写入精确内容并持久刷新, 参数为流及 base64, 写后回读不一致或 I/O 失败抛错. #>
function Write-PbiExternalToolStream([IO.FileStream]$Stream, [string]$Content) {
    $bytes = [Convert]::FromBase64String($Content)
    $Stream.Position = 0
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.SetLength($bytes.Length)
    $Stream.Flush($true)
    if ((Read-PbiExternalToolStream $Stream) -cne $Content) { throw '外部工具登记写入后核验失败' }
}

<#
.SYNOPSIS 仅比较并替换固定的两种登记文件, 可由受限提权入口使用.
.PARAMETER Kind Development 或 Installed, 目标路径始终从系统已知目录重建.
.PARAMETER Expected 原始字节的 base64 或 absent, 当前内容不符时拒绝并保留并发修改.
.PARAMETER Desired 完整产品描述的 base64 或 absent, 字段、启动参数及前后归属必须一致.
.OUTPUTS 无; 占用、并发、权限或写入错误均抛错, 写入失败在同一独占句柄内恢复原始字节.
#>
function Invoke-PbiExternalToolFileChange([ValidateSet('Development', 'Installed')][string]$Kind, [string]$Expected, [string]$Desired) {
    $before = if ($Expected -cne 'absent') { Read-PbiExternalToolDescriptor $Kind $Expected } else { $null }
    $after = if ($Desired -cne 'absent') { Read-PbiExternalToolDescriptor $Kind $Desired } else { $null }
    if ($null -ne $before -and $null -ne $after -and !(Test-PbiExternalToolOwner $Kind $before $after)) { throw '外部工具登记归属不一致, 未覆盖其他安装或仓库' }
    $path = Get-PbiExternalToolRegistrationPath $Kind
    if ((Get-PbiExternalToolContent $Kind) -cne $Expected) { throw '外部工具登记在检查后发生变化, 已保留' }
    if ($Expected -ceq $Desired) { return }
    $directory = [IO.Path]::GetDirectoryName($path)
    $null = [IO.Directory]::CreateDirectory($directory)
    $null = Resolve-PbiExternalToolPath $path
    Initialize-PbiExternalToolNative
    $stream = [PbiUtils.ExternalToolFiles]::Open($path, ($Expected -ceq 'absent'))
    $mutated = $false
    try {
        $null = Resolve-PbiExternalToolPath $path
        if ($Expected -cne 'absent' -and (Read-PbiExternalToolStream $stream) -cne $Expected) { throw '外部工具登记在取得独占租约前发生变化, 已保留' }
        $mutated = $true
        if ($Desired -ceq 'absent') { [PbiUtils.ExternalToolFiles]::Delete($stream) }
        else { Write-PbiExternalToolStream $stream $Desired }
    } catch {
        $primary = $_.Exception.Message
        if ($mutated -or $Expected -ceq 'absent') {
            try {
                if ($Expected -ceq 'absent') { [PbiUtils.ExternalToolFiles]::Delete($stream) }
                else { Write-PbiExternalToolStream $stream $Expected }
            } catch { throw ($primary + '; 外部工具登记恢复失败: ' + $_.Exception.Message) }
        }
        throw
    } finally { $stream.Dispose() }
    if ((Get-PbiExternalToolContent $Kind) -cne $Desired) { throw '外部工具登记在完成后发生变化, 未覆盖用户更改' }
}

Export-ModuleMember -Function Get-PbiExternalToolContent, Invoke-PbiExternalToolFileChange
