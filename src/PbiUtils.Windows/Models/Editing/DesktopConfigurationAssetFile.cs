// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Editing\DesktopConfigurationAssetFile.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 固定配置资产的同目录输出槽、原文件保留槽和不覆盖恢复原语, 不承担其他用户存储.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Projects;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

[assembly: InternalsVisibleTo("PbiUtils.Desktop.Tests")]

namespace PbiUtils.Windows.Models.Editing;

/// <summary>同一次只读句柄内取得的精确原字节与卷/文件身份.</summary>
internal sealed record DesktopConfigurationAssetState(string Hash, string Identity);

/// <summary>一个固定资产的同目录文件操作; 调用方负责先持久恢复责任, 本类不接收任意槽路径.</summary>
internal sealed partial class DesktopConfigurationAssetFile
{
    internal string Target { get; }
    internal string StagePath { get; }
    internal string HeldPath { get; }

    /// <summary>从允许目标和本轮不可复用GUID派生两个同目录槽, 拒绝路径注入与链接.</summary>
    internal DesktopConfigurationAssetFile(string target, string slot)
    {
        if (!Guid.TryParseExact(slot, "N", out _)) throw Conflict();
        Target = Path.GetFullPath(target);
        StagePath = Target + ".pbi-utils-" + slot + ".stage";
        HeldPath = Target + ".pbi-utils-" + slot + ".held";
        CheckPaths();
    }

    /// <summary>在已持久责任的唯一槽内排他创建并刷盘输出, 返回rename前的精确身份.</summary>
    internal DesktopConfigurationAssetState Stage(byte[] bytes)
    {
        CheckPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        CheckPaths();
        using (var stream = new FileStream(StagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        return Read(StagePath) ?? throw Conflict();
    }

    /// <summary>将真实当前目标移入唯一空槽, 返回实际被移出的身份, 调用方持久后再核对冻结原值.</summary>
    internal DesktopConfigurationAssetState Retain()
    {
        CheckPaths(); File.Move(Target, HeldPath, false);
        return Read(HeldPath) ?? throw Conflict();
    }

    /// <summary>核实输出槽的精确身份并发布到固定目标, 返回后仍需事务整体回读.</summary>
    internal void Publish(DesktopConfigurationAssetState output)
    {
        CheckPaths(); Require(StagePath, output);
        File.Move(StagePath, Target, false);
        Require(Target, output);
    }

    /// <summary>回滚先移出真实目标再检查归属, 不按检查前的旧结果删除固定目标.</summary>
    internal DesktopConfigurationAssetState RetireOutput()
    {
        CheckPaths(); File.Move(Target, StagePath, false);
        return Read(StagePath) ?? throw Conflict();
    }

    /// <summary>只向空目标归还保留槽, 不覆盖外部新增文件.</summary>
    internal void RestoreHeld(DesktopConfigurationAssetState held)
    {
        CheckPaths(); Require(HeldPath, held);
        File.Move(HeldPath, Target, false); Require(Target, held);
    }

    /// <summary>实际移出的是外部新文件时只归还到空目标, 失败保留槽中全部原字节.</summary>
    internal void ReturnStage(DesktopConfigurationAssetState captured)
    {
        CheckPaths(); Require(StagePath, captured);
        File.Move(StagePath, Target, false); Require(Target, captured);
    }

    /// <summary>只删除仍具备本轮输出身份的私有槽, 不能触碰固定目标或身份变化后的文件.</summary>
    internal void DeleteStage(DesktopConfigurationAssetState output) => DeleteSlot(StagePath, output);
    /// <summary>已确认模型提交后只删除本轮精确保留原文件; 调用方先持久completed事实.</summary>
    internal void DeleteHeld(DesktopConfigurationAssetState held) => DeleteSlot(HeldPath, held);

    /// <summary>保持读取句柄到身份及哈希均取得, 拒绝目录、祖先普通文件与链接; 仅实际缺失返回null.</summary>
    internal static DesktopConfigurationAssetState? Read(string path)
    {
        UserDataStore.Check(path);
        var full = Path.GetFullPath(path);
        var exists = false;
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (current.Equals(full, StringComparison.OrdinalIgnoreCase)) { if (directory) throw Conflict(); exists = true; }
            else if (!directory) throw Conflict();
        }
        if (!exists) return null;
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(Convert.ToHexStringLower(SHA256.HashData(stream)), ProjectFileIdentity.Read(full));
    }

    /// <summary>私有槽删除前核验本轮身份, 缺失视为已经完成; 不删除外部替换内容.</summary>
    private void DeleteSlot(string path, DesktopConfigurationAssetState expected)
    {
        CheckPaths();
        if (Read(path) is null) return;
        // 读/删除权威绑定同一句柄且不共享写入/删除, 校验后不用按路径删除, 封闭槽清理的同内容ABA窗口.
        using var handle = OpenForDeletion(path, 0x80010000, 1, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        UserDataStore.Check(path);
        using var stream = new FileStream(handle, FileAccess.Read);
        var current = new DesktopConfigurationAssetState(Convert.ToHexStringLower(SHA256.HashData(stream)), ProjectFileIdentity.Read(path));
        if (current != expected) throw Conflict();
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetDisposition(handle, 4, ref disposition, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>每个系统操作前检查三条固定路径的完整祖先链, 不跟随重定向.</summary>
    private void CheckPaths() { UserDataStore.Check(Target); UserDataStore.Check(StagePath); UserDataStore.Check(HeldPath); }
    /// <summary>身份或原字节不符时保留现场, 不凭同内容认领另一文件.</summary>
    private static void Require(string path, DesktopConfigurationAssetState expected) { if (Read(path) != expected) throw Conflict(); }
    /// <summary>固定错误不包含文件路径、配置正文或原始系统异常.</summary>
    private static AppException Conflict() => new("STALE_BASELINE", "配置资产或本轮保留槽发生变化, 未覆盖外部文件", 3);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { internal int DeleteFile; }
    // 持有精确槽句柄至核验后标记删除, 不按路径操作已被替换的其他文件.
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle OpenForDeletion(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDisposition(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
}
