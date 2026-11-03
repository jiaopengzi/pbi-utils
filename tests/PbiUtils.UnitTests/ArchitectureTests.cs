// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ArchitectureTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用实际项目引用和程序集元数据固定共享层的禁止依赖边界.

using PbiUtils.Application.Models.Editing;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;
using PbiUtils.Application;
using PbiUtils.Core;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>无需新增架构框架的依赖检查; 同时检查项目声明与实际编译引用.</summary>
public sealed class ArchitectureTests
{
    /// <summary>定位真正项目文件, 避免从测试输出副本猜测当前依赖.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pbi-utils.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库");
    }

    /// <summary>每层只允许已使用的向下项目依赖, CLI 不能依赖 GUI/Windows 适配.</summary>
    [Theory]
    [InlineData("Core", "")]
    [InlineData("Application", "Core")]
    [InlineData("Infrastructure", "Application")]
    [InlineData("Windows", "Application,Infrastructure")]
    [InlineData("Cli", "Infrastructure")]
    [InlineData("Host", "Windows,Infrastructure")]
    [InlineData("Desktop", "Windows,Infrastructure")]
    public void ProjectReferencesRemainWithinLayer(string project, string allowed)
    {
        var definition = XDocument.Load(Path.Combine(Root(), "src", "PbiUtils." + project, "PbiUtils." + project + ".csproj"));
        Assert.Empty(InvalidReferences(definition, allowed));
    }

    /// <summary>故意加入反向项目引用的夹具必须被同一个检查器发现.</summary>
    [Fact]
    public void ReverseReferenceFixtureIsRejected()
    {
        var definition = XDocument.Parse("<Project><ItemGroup><ProjectReference Include='../PbiUtils.Core/PbiUtils.Core.csproj'/><ProjectReference Include='../PbiUtils.Windows/PbiUtils.Windows.csproj'/></ItemGroup></Project>");
        Assert.Equal(["PbiUtils.Windows"], InvalidReferences(definition, "Core"));
    }

    /// <summary>专用后台只组合业务与 Windows 适配, 不启用 UI 框架或打包网页资源.</summary>
    [Fact]
    public void BusinessHostDoesNotAcquireGuiResources()
    {
        var definition = XDocument.Load(Path.Combine(Root(), "src/PbiUtils.Host/PbiUtils.Host.csproj"));
        Assert.Equal("WinExe", Assert.Single(definition.Descendants("OutputType")).Value);
        Assert.DoesNotContain(definition.Descendants(), element => element.Name.LocalName is "UseWindowsForms" or "UseWPF");
        Assert.DoesNotContain(definition.Descendants("PackageReference"), element => element.Attribute("Include")!.Value.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal));
        Assert.DoesNotContain(definition.Descendants("Content"), element => element.Attribute("Include")!.Value.Contains("web/", StringComparison.Ordinal));
    }

    /// <summary>返回未获本层允许的实际引用, 缺少既有引用也阻止隐性架构漂移.</summary>
    private static string[] InvalidReferences(XDocument definition, string allowed)
    {
        var references = definition.Descendants("ProjectReference").Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value)).ToHashSet(StringComparer.Ordinal);
        var expected = allowed.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => "PbiUtils." + name).ToHashSet(StringComparer.Ordinal);
        return [.. references.Except(expected).Concat(expected.Except(references)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Core 不可借中央包或源码直引用引入平台、存储和模型能力.</summary>
    [Fact]
    public void CoreDoesNotAcquirePackagesOrPlatformTypes()
    {
        var definition = XDocument.Load(Path.Combine(Root(), "src/PbiUtils.Core/PbiUtils.Core.csproj"));
        Assert.Empty(definition.Descendants("PackageReference"));
        AssertNoForbiddenReferences(typeof(AppException).Assembly, true);
    }

    /// <summary>Application 只经应用契约请求写入, 不加载平台、模型 SDK 或直接文件适配.</summary>
    [Fact]
    public void SharedExecutionDoesNotAcquirePlatformAdapters()
    {
        AssertNoForbiddenReferences(typeof(ModelEditService).Assembly, false);
    }

    /// <summary>独立 CLI、后台和桌面汇入同一发布目录, 共同包必须解析为相同版本和内容, 不能靠合并替换掩盖依赖漂移.</summary>
    /// <param name="first">第一个独立入口项目名.</param>
    /// <param name="second">第二个独立入口项目名.</param>
    [Theory]
    [InlineData("Cli", "Host")]
    [InlineData("Cli", "Desktop")]
    [InlineData("Host", "Desktop")]
    public void SharedPublishUsesIdenticalLockedPackageVersionsAndContents(string first, string second)
    {
        var cli = LockedPackages(first);
        var desktop = LockedPackages(second);
        var shared = cli.Keys.Intersect(desktop.Keys).ToArray();
        Assert.NotEmpty(shared);
        Assert.Contains(shared, key => key.Package == "Microsoft.Extensions.Logging.Abstractions");
        foreach (var key in shared)
            Assert.True(cli[key] == desktop[key], $"共享发布依赖冲突: {key.Framework}/{key.Package}; {first}={cli[key].Version}, {second}={desktop[key].Version}");
    }

    /// <summary>读取实际锁文件的各框架包闭包, 项目引用没有包内容哈希且不参与此包一致性比较.</summary>
    /// <param name="project">独立产品入口名称.</param>
    /// <returns>按框架与包名索引的实际版本及 NuGet 内容哈希.</returns>
    private static Dictionary<(string Framework, string Package), (string Version, string Hash)> LockedPackages(string project)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src", "PbiUtils." + project, "packages.lock.json")));
        Dictionary<(string, string), (string, string)> packages = [];
        foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
            foreach (var package in framework.Value.EnumerateObject())
                if (package.Value.GetProperty("type").GetString() != "Project")
                    packages.Add((framework.Name, package.Name), (package.Value.GetProperty("resolved").GetString()!, package.Value.GetProperty("contentHash").GetString()!));
        return packages;
    }

    /// <summary>检查真实 PE 的引用类型, 能发现完全限定名称调用而非只搜索 using.</summary>
    private static void AssertNoForbiddenReferences(Assembly assembly, bool core)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var references = metadata.TypeReferences.Select(handle => metadata.GetTypeReference(handle))
            .Select(type => metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name));
        var forbidden = references.Where(name => name.StartsWith("Microsoft.AnalysisServices", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal)
            || name.StartsWith("System.Windows", StringComparison.Ordinal)
            || name.StartsWith("PbiUtils.Infrastructure", StringComparison.Ordinal)
            || name.StartsWith("PbiUtils.Windows", StringComparison.Ordinal)
            || name.StartsWith("PbiUtils.Desktop", StringComparison.Ordinal)
            || name.StartsWith("PbiUtils.Host", StringComparison.Ordinal)
            || (core && name.StartsWith("System.IO.", StringComparison.Ordinal))
            || name is "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileStream" or "System.IO.DriveInfo").ToArray();
        Assert.True(forbidden.Length == 0, $"{assembly.GetName().Name} 引入禁止类型: {string.Join(", ", forbidden)}");
    }
}
