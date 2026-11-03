// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Export\ModelTableExportPublication.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 使用受锁文件句柄发布导出, 防止指纹核验与最终替换之间覆盖并发文件修改.

using PbiUtils.Infrastructure.Storage;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Models.Export;

/// <summary>Windows 文件发布适配, 原目标的写入和删除权限锁贯穿最终指纹、备份和发布.</summary>
internal static partial class ModelTableExportPublication
{
    /// <summary>不存在目标时只创建, 已存在时锁定原文件后使用同目录备份完成可恢复发布.</summary>
    /// <param name="temporary">本任务已经完整写成的临时文件.</param>
    /// <param name="destination">系统选择器授权的目标.</param>
    /// <param name="original">准备阶段记录的原目标指纹, null 表示原先不存在.</param>
    /// <param name="fingerprint">只读取受锁句柄的完整指纹计算.</param>
    /// <param name="published">完整文件发布后立即记录事实, 清理错误不能让调用方误以为尚未发布.</param>
    /// <exception cref="AppException">并发变化、发布或恢复/清理失败, 不覆盖新的用户文件.</exception>
    public static void Publish(string temporary, string destination, string? original, Func<FileStream, string> fingerprint, Action published)
    {
        UserDataStore.Check(destination);
        if (original is null)
        {
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { throw Changed(); }
            published();
            return;
        }
        var backup = Path.Combine(Path.GetDirectoryName(destination)!, ".pbi-utils-export-original-" + Guid.NewGuid().ToString("N") + ".tmp");
        // DELETE 是本句柄自身的重命名能力; FileShare.Read 拒绝其他句柄写入或重命名, 但允许普通读取.
        using (var handle = CreateFileW(destination, 0x80000000 | 0x00010000, 1, 0, 3, 0x00200000, 0))
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) throw Changed();
                throw new Win32Exception(error);
            }
            using var stream = new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
            if (fingerprint(stream) != original) throw Changed();
            // 两步文件名切换并非崩溃原子交换; 此处到最终 Move 之间若进程中止, 原文件仍保留为备份.
            Rename(handle, backup);
            try
            {
                // 原目标已被同一句柄保留为备份; 如有人在空隙中创建新文件, 禁止覆盖.
                File.Move(temporary, destination, overwrite: false);
                published();
            }
            catch (Exception failure)
            {
                try { Rename(handle, destination); }
                catch (Win32Exception)
                {
                    throw new AppException("EXPORT_RESTORE_CONFLICT", "导出未发布; 目标已被其他操作占用, 原文件保留为目标目录中的导出备份", 4);
                }
                if (failure is IOException && File.Exists(destination))
                    throw new AppException("EXPORT_FILE_FAILED", "导出发布失败, 原目标文件已恢复", 4);
                throw;
            }
        }
        try { File.Delete(backup); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AppException("EXPORT_CLEANUP_FAILED", "文件已成功导出, 但原目标备份清理失败; 请检查目标目录", 4); }
    }

    /// <summary>通过拥有 DELETE 权限的原文件句柄重命名, 不释放排他保护且不替换目标.</summary>
    private static void Rename(SafeFileHandle handle, string destination)
    {
        var name = Path.GetFullPath(destination).ToCharArray();
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bytes = checked(name.Length * sizeof(char));
        var buffer = Marshal.AllocHGlobal(nameOffset + bytes + sizeof(char));
        try
        {
            Marshal.WriteInt32(buffer, 0, 0); // ReplaceIfExists=false.
            Marshal.WriteIntPtr(buffer, rootOffset, 0);
            Marshal.WriteInt32(buffer, lengthOffset, bytes);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            Marshal.WriteInt16(buffer, nameOffset + bytes, 0);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)(nameOffset + bytes + sizeof(char)))) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>稳定并发冲突, 不包含目标路径.</summary>
    private static AppException Changed() => new("EXPORT_DESTINATION_CHANGED", "目标文件已被其他操作修改, 未覆盖; 请重新导出", 3);

    /// <summary>打开拥有本句柄重命名能力的原目标, 不共享外部写入和删除权限.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    /// <summary>使用 FileRenameInfo 在持锁句柄上执行不覆盖目标的重命名.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, nint information, uint bufferSize);
}
