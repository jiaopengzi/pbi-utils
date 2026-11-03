// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Discovery\NativeDiscovery.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 使用 Windows 进程快照和 TCP owner 表读取父进程与回环监听端口.

using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PbiUtils.Windows.Desktop.Discovery;

/// <summary>原生只读发现, 所有系统句柄和非托管缓冲区均在当前调用释放.</summary>
internal static partial class NativeDiscovery
{
    internal sealed record ProcessEntry(int Id, int ParentId, string Name);
    internal sealed record Listener(int ProcessId, int Port);

    /// <summary>获取一次父子进程快照; 系统调用失败时抛 Win32Exception.</summary>
    internal static unsafe IReadOnlyList<ProcessEntry> Processes()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
        List<ProcessEntry> result = [];
        if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            // 固定缓冲区与 PROCESSENTRY32W 的 WCHAR[260] 一致, 读取限于该数组.
            var name = new ReadOnlySpan<char>(entry.ExeFile, 260);
            var terminator = name.IndexOf('\0');
            result.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, new string(terminator < 0 ? name : name[..terminator])));
        }
        while (Process32NextW(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    /// <summary>读取 IPv4/IPv6 回环监听端口, 绝不扫描远程地址或接受任意服务器.</summary>
    internal static IReadOnlyList<Listener> Ports()
    {
        List<Listener> result = [];
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            var code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (code is not (0 or 122)) throw new Win32Exception((int)code);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    code = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (code == 122) continue;
                    if (code != 0) throw new Win32Exception((int)code);
                    var count = Marshal.ReadInt32(buffer);
                    var rowSize = family == 2 ? 24 : 56;
                    for (var index = 0; index < count; index++)
                    {
                        var row = IntPtr.Add(buffer, 4 + index * rowSize);
                        var address = new byte[family == 2 ? 4 : 16];
                        Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), address, 0, address.Length);
                        if (!IPAddress.IsLoopback(new IPAddress(address))) continue;
                        var portOffset = family == 2 ? 8 : 20;
                        var port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                        result.Add(new(Marshal.ReadInt32(row, family == 2 ? 20 : 52), port));
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (code != 0) throw new Win32Exception((int)code);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry32 entry);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry32 entry);
    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
