// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Projects\ProjectFileIdentity.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以本机文件系统身份识别已创建入口, 不以路径或同名文件替代真实目标.

using PbiUtils.Infrastructure.Storage;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Projects;

/// <summary>短暂打开句柄获取卷与文件 ID, 不持有阻止用户保存或删除的长期句柄.</summary>
public static partial class ProjectFileIdentity
{
    /// <summary>读取真实入口身份, 不存在或内核无法核验时拒绝授予写入目标.</summary>
    public static string Read(string path)
    {
        UserDataStore.Check(path);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!OperatingSystem.IsWindows()) return File.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!GetFileInformationByHandle(handle, out var information)) throw new AppException("PROJECT_IDENTITY_UNAVAILABLE", "无法核实工程文件身份", 3);
        return $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct FileInformation
    {
        [FieldOffset(28)]
        public uint VolumeSerialNumber;
        [FieldOffset(44)]
        public uint FileIndexHigh;
        [FieldOffset(48)]
        public uint FileIndexLow;
    }

    // 此结构直接匹配 Win32 固定布局, 不向共享应用层暴露平台句柄.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
