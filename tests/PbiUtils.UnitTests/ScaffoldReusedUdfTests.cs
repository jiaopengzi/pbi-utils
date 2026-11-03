// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldReusedUdfTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证已装 UDF 工程重用为真实用户模板后, 新增函数仍保留原安装锁, 依赖及许可.

using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Udf;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>真实组合和目录加载均只访问独占临时树, 包提供者返回固定定义且不联网.</summary>
public sealed class ScaffoldReusedUdfTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-reused-udf-" + Guid.NewGuid().ToString("N"));

    /// <summary>生成工程再次作为用户模板新增另一包或本地函数时, 原精确闭包和逐包许可必须与函数一起保留.</summary>
    /// <param name="localSecond">第二批选择本地函数; false 时选择另一远端包.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedGeneratedTemplateKeepsInstalledFunctionsDependenciesAndLicenses(bool localSecond)
    {
        var userRoot = Path.Combine(root, "user");
        var catalog = new ScaffoldTemplateCatalog(userRoot);
        var builtin = await catalog.GetAsync("builtin:blank", default);
        var local = new LocalUdfStore(userRoot);
        var resolver = new UdfDependencyResolver(new FixedPackages(), local);
        var writer = new ScaffoldModelWriter(resolver);
        var source = Path.Combine(userRoot, "templates", "pbip", "GeneratedWithUdf");
        Directory.CreateDirectory(source);
        var first = await writer.ComposeAsync(builtin, Request(builtin, "Original") with
        {
            TemplateQueries = ["Hour24"],
            Udfs = [new("daxlib", "OldPkg", "1.0.0", ["OldPkg.Caller"])]
        }, source, source, null, default);
        using (var original = await ReadDatabaseAsync(source, first.EntryFile))
        {
            var installed = ModelConfigurationUdfLocks.FromModel(original.Model);
            Assert.Equal(["OldDep.Base", "OldPkg.Caller"], installed.Functions.Select(item => item.Function.Name).Order(StringComparer.Ordinal));
            Assert.Equal([new UdfPackageDependency("OldDep", "1.0.0")], installed.Packages.Single(item => item.Id == "OldPkg").Dependencies);
            Assert.True(installed.Functions.Single(item => item.Function.Name == "OldDep.Base").IsDependency);
        }
        var sourceBytes = SnapshotFiles(source);
        var templateInfo = Assert.Single(await catalog.ListAsync(default), item => item.Source == "user");
        var template = await catalog.GetAsync(templateInfo.Id, default);
        Assert.Equal(first.EntryFile, template.EntryFile);
        UdfSelection selection;
        if (localSecond)
        {
            var added = await local.CreateAsync(new(1, "Local.New", "1.0.0", "() => 3", "new local function", []), default);
            selection = new("local", added.Id, "1.0.0", ["Local.New"], Baseline: added.Baseline);
        }
        else selection = new("daxlib", "NewPkg", "1.0.0", ["NewPkg.Answer"]);
        var stage = Path.Combine(root, "derived");
        Directory.CreateDirectory(stage);
        var second = await writer.ComposeAsync(template, Request(template, "Derived") with { Udfs = [selection] },
            stage, Path.Combine(root, "final"), null, default);
        using var database = await ReadDatabaseAsync(stage, second.EntryFile);
        string[] names = ["OldDep.Base", "OldPkg.Caller", localSecond ? "Local.New" : "NewPkg.Answer"];
        Assert.Equal(names.Order(StringComparer.Ordinal), database.Model.Functions.Select(function => function.Name).Order(StringComparer.Ordinal));
        var merged = ModelConfigurationUdfLocks.FromModel(database.Model);
        Assert.Equal(names.Order(StringComparer.Ordinal), merged.Functions.Select(item => item.Function.Name).Order(StringComparer.Ordinal));
        Assert.Equal(localSecond ? 2 : 3, merged.Packages.Count);
        string[] packageIds = localSecond ? ["OldDep", "OldPkg"] : ["OldDep", "OldPkg", "NewPkg"];
        foreach (var id in packageIds)
        {
            var package = Assert.Single(merged.Packages, item => item.Id == id);
            Assert.Equal("daxlib", package.Source);
            Assert.Equal("1.0.0", package.Version);
            Assert.Equal(FixedPackages.PackageHash(id), package.Hash);
            Assert.Equal(FixedPackages.License(id), package.LicenseFiles["LICENSE.txt"]);
        }
        var caller = merged.Packages.Single(item => item.Id == "OldPkg");
        Assert.Equal([new UdfPackageDependency("OldDep", "1.0.0")], caller.Dependencies);
        Assert.Equal(["OldPkg.Caller"], caller.SelectedFunctions);
        Assert.Equal(["OldDep.Base"], merged.Packages.Single(item => item.Id == "OldDep").DependencyFunctions);
        ModelConfigurationUdfLocks.VerifyInstalled(database.Model, merged);
        Assert.False(File.Exists(Path.Combine(stage, "config", "udf-lock.json")));
        Assert.False(Directory.Exists(Path.Combine(stage, "config", "licenses")));
        var after = SnapshotFiles(source);
        Assert.Equal(sourceBytes.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var file in sourceBytes) Assert.Equal(file.Value, after[file.Key]);
        foreach (var file in template.Files) Assert.Equal(sourceBytes[file.RelativePath], file.Content);
    }

    /// <summary>模板已有本地函数的安装事实来自完整模型, 原本地定义删除后仍可保留并新增另一包.</summary>
    [Fact]
    public async Task ReusedTemplateKeepsInstalledLocalAfterSourceDefinitionDeleted()
    {
        var userRoot = Path.Combine(root, "user");
        var catalog = new ScaffoldTemplateCatalog(userRoot);
        var builtin = await catalog.GetAsync("builtin:blank", default);
        var local = new LocalUdfStore(userRoot);
        var installedLocal = await local.CreateAsync(new(1, "Local.Prior", "1.0.0", "() => 7", "installed local function", []), default);
        var writer = new ScaffoldModelWriter(new UdfDependencyResolver(new FixedPackages(), local));
        var source = Path.Combine(userRoot, "templates", "pbip", "GeneratedWithLocalUdf");
        Directory.CreateDirectory(source);
        _ = await writer.ComposeAsync(builtin, Request(builtin, "Original") with
        {
            TemplateQueries = ["Hour24"],
            Udfs = [new("local", installedLocal.Id, "1.0.0", ["Local.Prior"], Baseline: installedLocal.Baseline)]
        }, source, source, null, default);
        var sourceBytes = SnapshotFiles(source);
        await local.DeleteAsync(installedLocal.Id, installedLocal.Baseline, default);
        Assert.Empty(await local.ListAsync(default));
        var info = Assert.Single(await catalog.ListAsync(default), item => item.Source == "user");
        var template = await catalog.GetAsync(info.Id, default);
        var stage = Path.Combine(root, "derived");
        Directory.CreateDirectory(stage);
        var result = await writer.ComposeAsync(template, Request(template, "Derived") with
        {
            Udfs = [new("daxlib", "NewPkg", "1.0.0", ["NewPkg.Answer"])]
        }, stage, Path.Combine(root, "final"), null, default);
        using var database = await ReadDatabaseAsync(stage, result.EntryFile);
        Assert.Equal(["Local.Prior", "NewPkg.Answer"], database.Model.Functions.Select(function => function.Name).Order(StringComparer.Ordinal));
        var merged = ModelConfigurationUdfLocks.FromModel(database.Model);
        var preserved = Assert.Single(merged.Functions, item => item.Function.Name == "Local.Prior");
        Assert.Equal("local", preserved.Source);
        Assert.Equal(installedLocal.Id, preserved.PackageId);
        Assert.Equal(installedLocal.Baseline, preserved.Baseline);
        Assert.Equal("NewPkg.Answer", Assert.Single(result.UdfFunctions!).Name);
        Assert.Equal(FixedPackages.License("NewPkg"), Assert.Single(merged.Packages).LicenseFiles["LICENSE.txt"]);
        ModelConfigurationUdfLocks.VerifyInstalled(database.Model, merged);
        var after = SnapshotFiles(source);
        Assert.Equal(sourceBytes.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var file in sourceBytes) Assert.Equal(file.Value, after[file.Key]);
    }

    /// <summary>请求只绑定实际目录加载的模板身份和目标项目名, 可选配置默认关闭.</summary>
    /// <param name="template">内置或用户目录实际冻结的模板.</param>
    /// <param name="projectName">本轮输出名称.</param>
    /// <returns>可供真实 composer 校验的最小请求.</returns>
    private static ScaffoldRequest Request(ResolvedPbipTemplate template, string projectName) =>
        new(1, template.Info.Id, template.Info.Baseline, projectName, "zh-cn", null, null, null, null, null);

    /// <summary>按照真实 PBIP 快照的相对定义位置回读官方 TOM, 不假设模型目录名或路径层级.</summary>
    /// <param name="directory">测试拥有的完整工程根.</param>
    /// <param name="entryFile">composer 返回的实际相对 PBIP 入口.</param>
    /// <returns>由调用方释放的独立数据库定义; 无效绑定或定义保留实际读取错误.</returns>
    private static async Task<TOM.Database> ReadDatabaseAsync(string directory, string entryFile)
    {
        var entry = Path.Combine(directory, entryFile);
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        var definition = snapshot.Files.Single(file => Path.GetFileName(file.Path) == "definition.pbism");
        var modelRoot = Path.GetDirectoryName(Path.Combine(Path.GetDirectoryName(entry)!, definition.Path))!;
        return snapshot.ModelFormat == "TMDL" ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelRoot, "definition"))
            : TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(modelRoot, "model.bim")));
    }

    /// <summary>只读取独占测试树的全部原始字节, 用于证明组合没有修改用户模板源.</summary>
    /// <param name="directory">本测试创建的模板根.</param>
    /// <returns>使用正斜线相对路径索引的文件字节.</returns>
    private static Dictionary<string, byte[]> SnapshotFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>按既有 resolver 测试契约提供三个固定包, 许可使用同名文件以覆盖逐包隔离.</summary>
    private sealed class FixedPackages : IUdfPackageProvider
    {
        /// <summary>本回归不浏览目录, 空结果不能导致隐式网络请求.</summary>
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UdfPackageSummary>>([]);

        /// <summary>固定包仅有一个精确版本, 本回归选择明确 tag.</summary>
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["1.0.0"]);

        /// <summary>返回固定函数, 精确依赖和未经整理的原始许可; 非夹具身份直接失败.</summary>
        /// <param name="packageId">OldPkg, OldDep 或 NewPkg.</param>
        /// <param name="version">唯一受支持的 1.0.0.</param>
        /// <param name="cancellationToken">读取前检查的取消令牌.</param>
        /// <returns>不访问外部资源的固定包.</returns>
        /// <exception cref="InvalidOperationException">请求了夹具以外的身份或版本.</exception>
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (version != "1.0.0") throw new InvalidOperationException("Unexpected fixture package version");
            var function = packageId switch
            {
                "OldPkg" => new UdfFunction("OldPkg.Caller", "() => OldDep.Base()", "old selected function", new Dictionary<string, string>()),
                "OldDep" => new UdfFunction("OldDep.Base", "() => 1", "old dependency", new Dictionary<string, string>()),
                "NewPkg" => new UdfFunction("NewPkg.Answer", "() => 2", "new selected function", new Dictionary<string, string>()),
                _ => throw new InvalidOperationException("Unexpected fixture package identity")
            };
            return Task.FromResult(new UdfPackage(packageId, version, PackageHash(packageId), "fixture", "fixture",
                new Dictionary<string, string> { ["LICENSE.txt"] = License(packageId) },
                packageId == "OldPkg" ? [new("OldDep", "1.0.0")] : [], [function], DateTimeOffset.UnixEpoch));
        }

        /// <summary>固定合法包指纹, 用于确认旧锁并未被新批次替换或重新分配.</summary>
        internal static string PackageHash(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

        /// <summary>保留 CRLF, 中文及尾部空行的逐包许可原文, 不让断言只证明许可证名称存在.</summary>
        internal static string License(string id) => "Original license: " + id + "\r\nCopyright 原始作者\r\nPermission retained exactly.\r\n\r\n";
    }

    /// <summary>只清理本测试创建的唯一临时目录, 不触碰真实用户模板或函数库.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
