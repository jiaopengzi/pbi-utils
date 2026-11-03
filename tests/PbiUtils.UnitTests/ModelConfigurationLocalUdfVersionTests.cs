// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ModelConfigurationLocalUdfVersionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证同逻辑本地函数跨精确 tag/GUID 的私有候选替换及依赖、归属和来源保护.

using PbiUtils.Infrastructure.Udf.Model;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Udf;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.UnitTests;

/// <summary>只修改私有 TOM 与隔离本地函数文件, 不连接真实 Desktop 或提交用户模型.</summary>
public sealed class ModelConfigurationLocalUdfVersionTests
{
    /// <summary>本地逻辑名称忽略大小写, 显式升降版更新 GUID/基线并保留原模型及生成血缘.</summary>
    [Theory]
    [InlineData("Local.Answer")]
    [InlineData("LOCAL.answer")]
    public void OwnedCandidateCanSwitchLocalTagAcrossStableIds(string nextName)
    {
        using var original = Database();
        var old = Function("Local.Answer", "1.0.0", new string('a', 32), "() => 1");
        Apply(original, new([old], []));
        var fingerprint = ModelConfigurationReadback.Fingerprint(original);
        var lineage = original.Model.Functions["Local.Answer"].LineageTag;
        using var candidate = original.Clone();
        var next = Function(nextName, "2.0.0", new string('b', 32), "() => 2");

        Apply(candidate, new([next], []));

        Assert.Equal("() => 2", Assert.Single(candidate.Model.Functions).Expression);
        Assert.Equal(lineage, candidate.Model.Functions[0].LineageTag);
        Assert.Equivalent(next, Assert.Single(ModelConfigurationUdfLocks.FromModel(candidate.Model).Functions));
        Assert.Equal(fingerprint, ModelConfigurationReadback.Fingerprint(original));
        Apply(candidate, new([old], []));
        Assert.Equal("() => 1", Assert.Single(candidate.Model.Functions).Expression);
        Assert.Equal(old.PackageId, Assert.Single(ModelConfigurationUdfLocks.FromModel(candidate.Model).Functions).PackageId);
    }

    /// <summary>同 tag 换 GUID、无效内部身份或跨来源同名覆盖均不得成为本地版本切换.</summary>
    [Theory]
    [InlineData("local", "1.0.0", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("local", "2.0.0", "arbitrary-name")]
    [InlineData("daxlib", "2.0.0", "Remote.Package")]
    public void LocalReplacementRejectsUnrelatedIdentity(string source, string version, string id)
    {
        var old = Function("Local.Answer", "1.0.0", new string('a', 32), "() => 1");
        var next = Function("Local.Answer", version, id, "() => 2") with { Source = source };
        var error = Assert.Throws<AppException>(() => ModelConfigurationUdfLocks.Merge(new([old], []), new([next], [])));
        Assert.Equal("UDF_NAME_CONFLICT", error.Code);
    }

    /// <summary>远端安装身份不能通过同名本地 tag 接管, 原来源规则保持.</summary>
    [Fact]
    public void LocalTagCannotReplaceRemoteInstalledFunction()
    {
        var old = Function("Shared.Answer", "1.0.0", "Remote.Package", "() => 1") with { Source = "daxlib" };
        var next = Function("Shared.Answer", "2.0.0", new string('b', 32), "() => 2");
        Assert.Equal("UDF_NAME_CONFLICT", Assert.Throws<AppException>(() => ModelConfigurationUdfLocks.Merge(new([old], []), new([next], []))).Code);
    }

    /// <summary>人工改写旧函数后即使同逻辑包换 tag 也拒绝, 不以名称或旧锁补授覆盖权限.</summary>
    [Fact]
    public void ChangedOwnershipPreventsLocalTagReplacement()
    {
        using var original = Database();
        Apply(original, new([Function("Local.Answer", "1.0.0", new string('a', 32), "() => 1")], []));
        original.Model.Functions[0].Expression = "() => 99";
        var before = ModelConfigurationReadback.Fingerprint(original);
        var error = Assert.Throws<AppException>(() => Apply(original, new([Function("Local.Answer", "2.0.0", new string('b', 32), "() => 2")], [])));
        Assert.Equal("CONFIGURATION_OBJECT_CONFLICT", error.Code);
        Assert.Equal(before, ModelConfigurationReadback.Fingerprint(original));
    }

    /// <summary>安装锁未保留本地声明闭包时, 本地换版同样要求全部存活本地函数本轮重解析.</summary>
    [Fact]
    public void LocalTagSwitchRequiresReparsedInstalledLocalFunctions()
    {
        var old = Function("Local.Answer", "1.0.0", new string('a', 32), "() => 1");
        var other = Function("Local.Other", "1.0.0", new string('c', 32), "() => 3");
        var next = Function("Local.Answer", "2.0.0", new string('b', 32), "() => 2");
        Assert.Equal("UDF_VERSION_CONFLICT", Assert.Throws<AppException>(() => ModelConfigurationUdfLocks.Merge(new([old, other], []), new([next], []))).Code);
        var merged = ModelConfigurationUdfLocks.Merge(new([old, other], []), new([next, other], []));
        Assert.Equal(2, merged.Functions.Count);
        Assert.Contains(merged.Functions, item => item.PackageId == next.PackageId);
    }

    /// <summary>声明旧 tag 的本地调用方不能静默重绑, 显式改依赖并同批重解析后才允许候选换版.</summary>
    [Fact]
    public async Task LocalDependentMustBeReparsedAgainstExactNewTag()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-local-tag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var old = await store.CreateAsync(new(1, "Local.Answer", "1.0.0", "() => 1", "", []), default);
            var next = await store.CreateAsync(old.Definition with { Version = "2.0.0", Expression = "() => 2" }, default);
            var caller = await store.CreateAsync(new(1, "Local.Caller", "1.0.0", "() => Local.Answer()", "",
                [new("local", old.Id, old.Definition.Version, old.Definition.Name, old.Baseline)]), default);
            var resolver = new UdfDependencyResolver(new NoPackages(), store);
            var installed = await resolver.ResolveAsync([Select(caller)], default);
            using var model = Database();
            Apply(model, installed);
            var before = ModelConfigurationReadback.Fingerprint(model);
            var nextOnly = await resolver.ResolveAsync([Select(next)], default);
            Assert.Equal("UDF_VERSION_CONFLICT", Assert.Throws<AppException>(() => Apply(model, nextOnly)).Code);
            Assert.Equal(before, ModelConfigurationReadback.Fingerprint(model));
            await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync([Select(next), Select(caller)], default));

            caller = await store.UpdateAsync(caller.Id, caller.Baseline, caller.Definition with
            {
                Dependencies = [new("local", next.Id, next.Definition.Version, next.Definition.Name, next.Baseline)]
            }, default);
            var reparsed = await resolver.ResolveAsync([Select(caller)], default);
            Apply(model, reparsed);
            Assert.Equal(2, model.Model.Functions.Count);
            Assert.Equal("() => 2", model.Model.Functions["Local.Answer"].Expression);
            Assert.Contains(ModelConfigurationUdfLocks.FromModel(model.Model).Functions, item => item.PackageId == next.Id);
            Assert.Equivalent(old, await store.GetAsync(old.Id, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>同 GUID 同 tag 的定义基线变化也须重解析调用方, 显式声明旧基线不能因版本未变而静默失效.</summary>
    [Fact]
    public async Task LocalDefinitionUpdateRequiresReparsedBaselineDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-local-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalUdfStore(root);
            var answer = await store.CreateAsync(new(1, "Local.Answer", "1.0.0", "() => 1", "", []), default);
            var caller = await store.CreateAsync(new(1, "Local.Caller", "1.0.0", "() => 42", "",
                [new("local", answer.Id, answer.Definition.Version, answer.Definition.Name, answer.Baseline)]), default);
            var resolver = new UdfDependencyResolver(new NoPackages(), store);
            using var model = Database();
            Apply(model, await resolver.ResolveAsync([Select(caller)], default));
            var before = ModelConfigurationReadback.Fingerprint(model);
            answer = await store.UpdateAsync(answer.Id, answer.Baseline, answer.Definition with { Expression = "() => 2" }, default);
            var nextOnly = await resolver.ResolveAsync([Select(answer)], default);

            Assert.Equal("UDF_VERSION_CONFLICT", Assert.Throws<AppException>(() => Apply(model, nextOnly)).Code);
            Assert.Equal(before, ModelConfigurationReadback.Fingerprint(model));
            Assert.Equal("UDF_BASELINE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => resolver.ResolveAsync([Select(caller)], default))).Code);

            caller = await store.UpdateAsync(caller.Id, caller.Baseline, caller.Definition with
            {
                Dependencies = [new("local", answer.Id, answer.Definition.Version, answer.Definition.Name, answer.Baseline)]
            }, default);
            Apply(model, await resolver.ResolveAsync([Select(caller)], default));
            Assert.Equal(2, model.Model.Functions.Count);
            Assert.Equal("() => 2", model.Model.Functions["Local.Answer"].Expression);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>构造精确本地解析事实, GUID 与内容基线彼此独立, 不赋予模型所有权.</summary>
    private static ResolvedUdfFunction Function(string name, string version, string id, string expression) =>
        new(new(name, expression, "", new Dictionary<string, string>()), "local", id, version, new string('d', 64), false);

    /// <summary>按当前真实条目冻结选择, 不以逻辑包名替换内部 CRUD 身份.</summary>
    private static UdfSelection Select(LocalUdfEntry item) => new("local", item.Id, item.Definition.Version, [item.Definition.Name], Baseline: item.Baseline);

    /// <summary>建立只存在于本测试内存的可写模型, 不使用 Desktop/网络或用户路径.</summary>
    private static TOM.Database Database()
    {
        var database = new TOM.Database { Name = "Fixture", ID = "Fixture", CompatibilityLevel = 1702, Model = new TOM.Model() };
        database.Model.Tables.Add(new TOM.Table { Name = "Data" });
        return database;
    }

    /// <summary>仅执行共享私有候选变换, 所有权复核仍由产品入口完成.</summary>
    private static void Apply(TOM.Database model, UdfResolution resolution)
    {
        using var plan = new ModelConfigurationPlan("baseline", "digest", new(), resolution, [], null);
        ModelConfigurationMutation.Apply(model, plan);
    }

    /// <summary>本地闭包测试禁止读取远端来源, 意外调用直接失败.</summary>
    private sealed class NoPackages : IUdfPackageProvider
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<UdfPackageSummary>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) => throw new InvalidOperationException();
        /// <inheritdoc />
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) => throw new InvalidOperationException();
        /// <inheritdoc />
        public Task<UdfPackage> GetAsync(string packageId, string version, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
