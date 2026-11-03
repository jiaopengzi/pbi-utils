// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopInstallation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从 Windows 官方应用注册和当前用户 Store 包解析可验证的 Power BI Desktop 安装.

using PbiUtils.Infrastructure.Storage;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>后端核验的启动目标, Store 使用官方 FullTrust 包清单的应用身份.</summary>
public sealed record DesktopLaunchTarget(string Executable, string? AppUserModelId = null);

/// <summary>只解析固定产品身份, 不搜索 PATH、当前目录或执行注册表中的命令字符串.</summary>
public static partial class DesktopInstallation
{
    private const string PackageFamily = "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe";

    /// <summary>优先解析 App Paths 与官方 Store 包, 然后核验传统安装路径.</summary>
    /// <returns>首个可验证的 Desktop EXE; 未安装时抛出友好应用错误.</returns>
    public static string Resolve()
    {
        foreach (var path in RegisteredCandidates().Concat(StoreCandidates().Select(target => target.Executable)).Concat(TraditionalCandidates()).Distinct(StringComparer.OrdinalIgnoreCase))
            if (IsDesktopExecutable(path)) return Path.GetFullPath(path);
        throw new AppException("DESKTOP_NOT_INSTALLED", "未找到可用的 Power BI Desktop, 请安装 Microsoft 官方版本后重试", 3);
    }

    /// <summary>将已核验的 EXE 与真实 Store 包清单匹配, 传统安装返回纯 EXE 启动目标.</summary>
    /// <param name="executable">安装解析返回的官方 Desktop EXE 路径.</param>
    /// <returns>与此 EXE 严格匹配的包应用身份, 或传统安装目标.</returns>
    public static DesktopLaunchTarget ResolveLaunchTarget(string executable)
    {
        if (!IsDesktopExecutable(executable)) throw new AppException("DESKTOP_NOT_INSTALLED", "未找到可用的 Power BI Desktop, 请安装 Microsoft 官方版本后重试", 3);
        var full = Path.GetFullPath(executable);
        var store = StoreCandidates().Where(target => target.Executable.Equals(full, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (store.Length > 1) throw new AppException("DESKTOP_LAUNCH_FAILED", "无法启动 Power BI Desktop, 请尝试手工打开文件后连接", 3);
        return store.SingleOrDefault() ?? new(full);
    }

    /// <summary>核实普通本机 EXE 的原始文件名、Microsoft 公司与产品标识, 不调用此程序.</summary>
    /// <param name="path">安装来源返回的完整文件路径.</param>
    /// <returns>是否具有预期官方 Desktop 文件身份.</returns>
    public static bool IsDesktopExecutable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !File.Exists(path)
                || !Path.GetFileName(path).Equals("PBIDesktop.exe", StringComparison.OrdinalIgnoreCase)) return false;
            UserDataStore.Check(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            return version.OriginalFilename?.Equals("PBIDesktop.exe", StringComparison.OrdinalIgnoreCase) == true
                && version.CompanyName == "Microsoft Corporation" && version.ProductName == "Microsoft Power BI Desktop";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or AppException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    /// <summary>读取 Windows 推荐的固定 App Paths 键, 不解释参数或文件关联命令.</summary>
    /// <returns>存在的完整路径候选.</returns>
    private static List<string> RegisteredCandidates()
    {
        List<string> paths = [];
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PBIDesktop.exe");
                    if (key?.GetValue("") is string path) paths.Add(path.Trim('"'));
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return paths;
    }

    /// <summary>通过官方 AppModel API 获取当前用户包路径, 按包清单中的真实可执行文件定位.</summary>
    /// <returns>官方 Power BI Desktop 包提供的可执行路径.</returns>
    private static List<DesktopLaunchTarget> StoreCandidates()
    {
        List<DesktopLaunchTarget> paths = [];
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(PackageFamily, ref count, IntPtr.Zero, ref length, IntPtr.Zero) != 122 || count is 0 or > 64 || length is 0 or > 65536) return paths;
        var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
        try
        {
            if (GetPackagesByPackageFamily(PackageFamily, ref count, names, ref length, buffer) != 0) return paths;
            for (var index = 0; index < count; index++)
            {
                var fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
                if (fullName is null) continue;
                uint pathLength = 0;
                if (GetPackagePathByFullName(fullName, ref pathLength, IntPtr.Zero) != 122 || pathLength is 0 or > 32768) continue;
                var pathBuffer = Marshal.AllocHGlobal(checked((int)pathLength * sizeof(char)));
                try
                {
                    if (GetPackagePathByFullName(fullName, ref pathLength, pathBuffer) != 0) continue;
                    var package = Marshal.PtrToStringUni(pathBuffer);
                    if (package is not null) paths.AddRange(ManifestExecutables(package));
                }
                finally { Marshal.FreeHGlobal(pathBuffer); }
            }
        }
        finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
        return paths;
    }

    /// <summary>解析受限 XML 包清单, 拒绝外部实体、越界和非 Desktop 可执行绑定.</summary>
    /// <param name="package">AppModel 返回的真实包根目录.</param>
    /// <returns>清单明确绑定的本机 Desktop EXE 路径.</returns>
    private static DesktopLaunchTarget[] ManifestExecutables(string package)
    {
        try
        {
            var root = Path.GetFullPath(package);
            UserDataStore.Check(root);
            var manifest = Path.Combine(root, "AppxManifest.xml");
            UserDataStore.Check(manifest);
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var document = XDocument.Load(reader);
            return [.. document.Descendants().Where(element => element.Name.LocalName == "Application" && element.Attribute("EntryPoint")?.Value == "Windows.FullTrustApplication")
                .Select(element => (Relative: element.Attribute("Executable")?.Value, Id: element.Attribute("Id")?.Value))
                .Where(application => !string.IsNullOrEmpty(application.Relative) && !Path.IsPathRooted(application.Relative) && !application.Relative.Contains(':')
                    && application.Id is { Length: > 0 and <= 128 } && application.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
                .Select(application => new DesktopLaunchTarget(Path.GetFullPath(Path.Combine(root, application.Relative!)), PackageFamily + "!" + application.Id))
                .Where(target => target.Executable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(target.Executable).Equals("PBIDesktop.exe", StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException or ArgumentException or AppException) { return []; }
    }

    /// <summary>兼容传统 EXE 安装器, 保留版本身份检查并拒绝 Report Server 产品.</summary>
    /// <returns>安装注册及标准目录下的明确 Desktop EXE 候选.</returns>
    private static List<string> TraditionalCandidates()
    {
        List<string> paths = [];
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                    {
                        using var key = uninstall!.OpenSubKey(name);
                        if (key?.GetValue("DisplayName") is not string display || !display.Equals("Microsoft Power BI Desktop", StringComparison.OrdinalIgnoreCase)
                            || key.GetValue("Publisher") is not string publisher || !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                            || key.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location)) continue;
                        paths.Add(Path.Combine(location, "bin", "PBIDesktop.exe"));
                    }
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            paths.Add(Path.Combine(Environment.GetFolderPath(folder), "Microsoft Power BI Desktop", "bin", "PBIDesktop.exe"));
        return paths;
    }

    /// <summary>获取当前用户固定官方包家族的完整包名, 输出内存由调用方持有.</summary>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr names, ref uint bufferLength, IntPtr buffer);

    /// <summary>按完整包名获取实际安装目录, 不猜测 WindowsApps 版本目录.</summary>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagePathByFullName(string packageFullName, ref uint length, IntPtr path);
}
