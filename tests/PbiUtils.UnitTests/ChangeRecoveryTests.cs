// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ChangeRecoveryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用合成文件固定原地恢复的外部冲突保护与第 N 文件失败行为.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models.Configuration;
using System.Security.Cryptography;
using System.Text;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;
using PbiUtils.Application.Scaffolding;

namespace PbiUtils.UnitTests;

/// <summary>验证进程内失败恢复; 进程终止和断电的持久事务恢复仍未实现.</summary>
public sealed class ChangeRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-recovery-" + Guid.NewGuid().ToString("N"));

    /// <summary>模拟第 N 文件失败后另一进程更改已写文件, 恢复不能覆盖该新内容.</summary>
    [Fact]
    public void RecoveryPreservesExternalEditsAndRestoresOtherWrittenFiles()
    {
        Directory.CreateDirectory(root);
        var first = Encoding.UTF8.GetBytes("before first");
        var second = Encoding.UTF8.GetBytes("before second");
        File.WriteAllText(Path.Combine(root, "first.txt"), "external edit after commit");
        File.WriteAllText(Path.Combine(root, "second.txt"), "after second");
        FileChange[] changes = [new("first.txt", Hash(first), Hash(Encoding.UTF8.GetBytes("after first")), null, null), new("second.txt", Hash(second), Hash(Encoding.UTF8.GetBytes("after second")), null, null)];
        var originals = new Dictionary<string, byte[]?> { ["first.txt"] = first, ["second.txt"] = second };
        Assert.False(ChangeWorkspace.RestoreWrittenFiles(root, changes, originals, ["first.txt", "second.txt"]));
        Assert.Equal("external edit after commit", File.ReadAllText(Path.Combine(root, "first.txt")));
        Assert.Equal(second, File.ReadAllBytes(Path.Combine(root, "second.txt")));
    }

    /// <summary>真实 TMDL 两个表提交时锁住第二文件; 第一文件必须恢复且原有锁定文件不变.</summary>
    [Fact]
    public async Task SecondFileWriteFailureRestoresFirstFile()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source");
        var model = Path.Combine(source, "model");
        Directory.CreateDirectory(model);
        var entry = Path.Combine(source, "sample.pbip");
        File.WriteAllText(entry, """{"version":"1.0","artifacts":[{"report":{"path":"report"}}]}""");
        Directory.CreateDirectory(Path.Combine(source, "report/definition/pages"));
        File.WriteAllText(Path.Combine(source, "report/definition.pbir"), """{"version":"4.0","datasetReference":{"byPath":{"path":"../model"}}}""");
        File.WriteAllText(Path.Combine(source, "report/definition/report.json"), "{}");
        File.WriteAllText(Path.Combine(source, "report/definition/pages/pages.json"), """{"pageOrder":[]}""");
        File.WriteAllText(Path.Combine(model, "definition.pbism"), """{"version":"4.2"}""");
        using (var database = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase("""{"name":"synthetic","compatibilityLevel":1600,"model":{"culture":"en-US","tables":[{"name":"A","measures":[{"name":"First","expression":"1"}]},{"name":"B","measures":[{"name":"Second","expression":"2"}]}]}}"""))
            Microsoft.AnalysisServices.Tabular.TmdlSerializer.SerializeDatabaseToFolder(database, Path.Combine(model, "definition"));
        var originals = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllBytes);
        var reader = new ProjectReader();
        using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        using var draft = await workspace.CreateDraftAsync(await reader.ReadAsync(entry, default), default);
        await draft.ApplyAsync(new("measure.set", "First", "description", "after A", "A"), default);
        await draft.ApplyAsync(new("measure.set", "Second", "description", "after B", "B"), default);
        FileStream? blocked = null;
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => draft.SaveAsync(() =>
            {
                blocked = File.Open(Path.Combine(model, "definition/tables/B.tmdl"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }, default));
            Assert.Equal("CHANGE_ROLLED_BACK", error.Code);
        }
        finally { blocked?.Dispose(); }
        foreach (var original in originals) Assert.Equal(original.Value, File.ReadAllBytes(original.Key));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*", SearchOption.AllDirectories));
    }

    /// <summary>平铺或嵌套 PBIP 的资产连续更新, 第 N 文件失败恢复本轮前内容, 不创建长期历史备份.</summary>
    /// <param name="nested">是否采用项目根下的 pbip 子目录布局.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetOnlyFailureRestoresLatestRoundBeforeBytes(bool nested)
    {
        var source = Path.Combine(root, "source"); var entry = PbipFixture.Create(nested ? Path.Combine(source, "pbip") : source);
        var modelFile = Path.Combine(Path.GetDirectoryName(entry)!, "model", "model.bim");
        using (var db = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(File.ReadAllText(modelFile)))
            File.WriteAllText(modelFile, Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(db));
        Directory.CreateDirectory(Path.Combine(source, "data"));
        var first = Path.Combine(source, "data", "a.csv"); var second = Path.Combine(source, "data", "b.csv");
        File.WriteAllText(first, "a0"); File.WriteAllText(second, "b0");
        var reader = new ProjectReader(); using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
        async Task<ChangePlan> Prepare(string a, string b)
        {
            var snapshot = await reader.ReadAsync(entry, default);
            var assets = new List<ModelConfigurationAsset>();
            foreach (var (target, content) in new[] { (first, a), (second, b) })
            {
                var prepared = Path.Combine(root, Guid.NewGuid().ToString("N") + ".csv"); File.WriteAllText(prepared, content);
                assets.Add(new(Path.GetRelativePath(source, target).Replace('\\', '/'), prepared, Hash(File.ReadAllBytes(target)), Hash(File.ReadAllBytes(prepared)), new FileInfo(prepared).Length));
            }
            var configuration = new ModelConfigurationPlan(snapshot.BaselineId, "fixture", new(), new([], []), assets, source);
            return await workspace.PrepareAsync(new(entry, source, "configuration.apply", "configuration", "config", "fixture", InPlace: true, Configuration: configuration), default);
        }
        var historical = Path.Combine(root, "user", "backup", "existing.json");
        Directory.CreateDirectory(Path.GetDirectoryName(historical)!);
        File.WriteAllText(historical, "preserved historical backup");
        var firstPlan = await Prepare("a1", "b1"); await workspace.PublishAsync(firstPlan, default);
        Assert.Equal([historical], Directory.GetFiles(Path.Combine(root, "user", "backup"), "*", SearchOption.AllDirectories));
        Assert.Equal("preserved historical backup", File.ReadAllText(historical));
        var baseline = (await reader.ReadAsync(entry, default)).BaselineId;
        var next = await Prepare("a2", "b2"); FileStream? blocked = null;
        try
        {
            var error = await Assert.ThrowsAsync<AppException>(() => workspace.PublishAsync(next, default, () => blocked = File.Open(second, FileMode.Open, FileAccess.Read, FileShare.Read)));
            Assert.Equal("CHANGE_ROLLED_BACK", error.Code);
        }
        finally { blocked?.Dispose(); }
        Assert.Equal("a1", File.ReadAllText(first)); Assert.Equal("b1", File.ReadAllText(second));
        Assert.Equal(baseline, (await reader.ReadAsync(entry, default)).BaselineId);
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*", SearchOption.AllDirectories));
    }

    /// <summary>文件指纹与产品恢复判断使用相同标准算法.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>只移除本测试创建的唯一临时夹具.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
