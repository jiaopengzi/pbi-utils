// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopStoreActivator.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 使用官方 Store FullTrust Launch 契约打开唯一模型文件, 不执行任意 Shell 命令.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>封装已核验官方 FullTrust 包激活, 不直接启动 WindowsApps 中受保护的 EXE.</summary>
internal static partial class DesktopStoreActivator
{
    /// <summary>使用 Launch 契约将单个文件参数传给明确的官方 FullTrust 应用; 原异常由上层安全日志处理.</summary>
    /// <param name="appUserModelId">包家族与真实清单 Application Id 组成的官方应用身份.</param>
    /// <param name="fileArgument">已核验并引用的唯一 PBIP/PBIX 文件参数.</param>
    public static void Open(string appUserModelId, string fileArgument)
    {
        IntPtr pointer = IntPtr.Zero;
        ComObject? manager = null;
        var initialized = false;
        try
        {
            var apartment = CoInitializeEx(IntPtr.Zero, 0);
            initialized = apartment >= 0;
            // 宿主 UI 可能已在 STA 初始化 COM, 保留该公寓并避免对称释放他人的初始化.
            if (apartment != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(apartment);
            var classId = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
            var interfaceId = new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in classId, IntPtr.Zero, 4, in interfaceId, out pointer));
            var wrappers = new StrategyBasedComWrappers();
            var wrapper = wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            manager = (ComObject)wrapper;
            var activation = (IApplicationActivationManager)wrapper;
            Marshal.ThrowExceptionForHR(activation.ActivateApplication(appUserModelId, fileArgument, 0, out _));
        }
        finally
        {
            manager?.FinalRelease();
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            if (initialized) CoUninitialize();
        }
    }

    /// <summary>声明官方应用激活接口的第一个 Launch 槽位, 不提供文件或协议操作入口.</summary>
    [GeneratedComInterface, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IApplicationActivationManager
    {
        /// <summary>调用已核验的官方 FullTrust 应用 Launch 契约.</summary>
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);

    }

    /// <summary>为当前线程建立 MTA COM 公寓, 宿主已有 STA 时仅保留当前公寓.</summary>
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint flags);

    /// <summary>只对本次成功的 COM 初始化执行对称释放.</summary>
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    /// <summary>创建官方应用激活管理器的独立本地服务器引用, 供源生成封送器调用.</summary>
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr result);
}
