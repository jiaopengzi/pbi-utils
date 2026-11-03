// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Storage\UserDataResetTree.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 以原始 Windows 句柄固定重置目录和文件身份, 在整体验证后按句柄删除且不跟随链接.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;

using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Infrastructure.Storage;

/// <summary>所有目录祖先拒绝重命名, 内容句柄拒绝外部写入/删除; 重置只删除这些已经核验的对象.</summary>
internal sealed partial class UserDataResetTree : IDisposable
{
    private readonly List<SafeFileHandle> ancestors = [];
    private readonly List<Entry> entries = [];
    private readonly string root;

    /// <summary>固定根与祖先后逐层锁定全部内容, 任何枚举、占用或链接错误均发生在首次删除之前.</summary>
    internal UserDataResetTree(string root, CancellationToken token)
    {
        this.root = root;
        try
        {
            var parents = new Stack<string>();
            for (var path = root; path is not null; path = Path.GetDirectoryName(path)) parents.Push(path);
            while (parents.TryPop(out var path))
            {
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(path)) ancestors.Add(Open(path, directory: true, delete: false));
            }
            if (Directory.Exists(root)) Visit(root, token);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>读取持锁的全部工作区账本, 不在校验与删除间重新按路径打开文件.</summary>
    internal void ValidateReceipts(CancellationToken token)
    {
        var plans = Path.Combine(root, "scaffold", "plans");
        var tools = Path.Combine(root, "mcp", "executions");
        foreach (var entry in entries.Where(entry => !entry.Directory))
        {
            token.ThrowIfCancellationRequested();
            var isPlan = string.Equals(Path.GetDirectoryName(entry.Path), plans, StringComparison.OrdinalIgnoreCase)
                && entry.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var isMcp = string.Equals(Path.GetDirectoryName(entry.Path), tools, StringComparison.OrdinalIgnoreCase)
                && entry.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (!isPlan && !isMcp) continue;
            var length = RandomAccess.GetLength(entry.Handle);
            if (length > 32 * 1024 * 1024) throw new AppException("RECEIPT_LIMIT", "执行回执超过限制, 不允许重置", 3);
            var bytes = new byte[checked((int)length)];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = RandomAccess.Read(entry.Handle, bytes.AsSpan(read), read);
                if (count == 0) throw new AppException("INVALID_RECEIPT", "执行回执读取不完整, 不允许重置", 3);
                read += count;
            }
            if (isMcp) ExecutionJournal.ValidateResetReceipt(bytes, Path.GetFileNameWithoutExtension(entry.Path));
            else ScaffoldPlanStore.ValidateResetReceipt(ScaffoldPlanStore.Parse(bytes, Path.GetFileNameWithoutExtension(entry.Path)));
        }
    }

    /// <summary>第一次删除前所有句柄已取得且账本已核验; 子项按原句柄删除后才删除父目录, 不递归解析路径.</summary>
    internal void DeleteContents()
    {
        foreach (var entry in Enumerable.Reverse(entries))
        {
            var disposition = (byte)1;
            if (!SetFileInformationByHandle(entry.Handle, 4, in disposition, 1))
                throw new AppException("USER_DATA_RESET_INCOMPLETE", "用户数据重置未完成, 部分内容可能已删除; 请关闭其他程序后重试", 4);
            entry.Handle.Dispose();
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new AppException("USER_DATA_RESET_INCOMPLETE", "重置期间出现新内容, 用户数据重置未完成", 4);
    }

    /// <summary>父目录已固定时只枚举一层, 取得子项句柄并核对实际属性后才进入下层.</summary>
    private void Visit(string directory, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            UserDataStore.Check(path);
            // 共享后台维护租约位于用户数据树之外; 树内所有普通内容均参与同一身份核验和删除.
            var isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            var handle = Open(path, isDirectory, delete: true);
            entries.Add(new(path, isDirectory, handle));
            if (isDirectory) Visit(path, token);
        }
    }

    /// <summary>OPEN_REPARSE_POINT 不跟随最终链接, 真实属性再次核验; 只共享读取, 祖先允许写入但不允许重命名.</summary>
    private static SafeFileHandle Open(string path, bool directory, bool delete)
    {
        UserDataStore.Check(path);
        var access = (directory ? 0x80u : 0x80000000u) | (delete ? 0x00010000u : 0);
        var handle = CreateFileW(path, access, delete ? 1u : 3u, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new AppException("USER_DATA_BUSY", "用户数据仍被其他程序占用或不可删除, 本次没有删除任何内容", 3); }
        if (!GetFileInformationByHandle(handle, out var information)
            || (information.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            || ((information.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
        {
            handle.Dispose();
            throw new AppException("UNSAFE_USER_DATA_PATH", "用户数据含链接或身份已变化, 本次没有删除任何内容", 3);
        }
        if (delete && !directory && (information.Attributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            handle.Dispose();
            throw new AppException("USER_DATA_BUSY", "用户数据仍被其他程序占用或不可删除, 本次没有删除任何内容", 3);
        }
        return handle;
    }

    /// <summary>失败或完成时关闭全部自己持有的句柄, 不处理其他应用的进程或句柄.</summary>
    public void Dispose()
    {
        foreach (var entry in entries) entry.Handle.Dispose();
        foreach (var handle in Enumerable.Reverse(ancestors)) handle.Dispose();
    }

    private sealed record Entry(string Path, bool Directory, SafeFileHandle Handle);

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct FileInformation
    {
        [FieldOffset(0)] public uint Attributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, in byte information, uint bufferSize);
}
