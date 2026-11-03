// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopManualSaveRecoveryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 真实 PBIP/TOM 与受控宿主状态验证用户手动保存后的无重放尾部确认.

using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopManualSaveRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-manual-save-" + Guid.NewGuid().ToString("N"));

    /// <summary>引擎提交后用户已正确保存且保存按钮禁用, 完整指纹与两次clean身份即可证明完成.</summary>
    [Fact]
    public async Task ConfirmsManuallySavedPbipWithoutInvokingDisabledSave()
    {
        var entry = CreateProject(); var target = new DesktopFileTarget(42, DateTime.UtcNow, entry, false);
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        var fingerprint = await ModelConfigurationReadback.FingerprintFileAsync(snapshot, default);
        var observations = 0;
        var confirmed = await DesktopFileSaveRecovery.TryConfirmAsync(target, fingerprint,
            _ => { observations++; return Task.FromResult(target); }, token => ReadFingerprintAsync(entry, token), TimeSpan.Zero, default);
        Assert.True(confirmed);
        Assert.Equal(2, observations);
    }

    /// <summary>仅clean、相同模型形状、相同路径或PBIX状态不能取代完整持久化与身份匹配.</summary>
    [Theory]
    [InlineData("dirty")]
    [InlineData("late-dirty")]
    [InlineData("pid")]
    [InlineData("restart")]
    [InlineData("path")]
    [InlineData("old-model")]
    [InlineData("late-model")]
    [InlineData("pbix")]
    public async Task RejectsIncompleteProof(string scenario)
    {
        var entry = CreateProject(); var target = new DesktopFileTarget(42, DateTime.UtcNow, scenario == "pbix" ? Path.ChangeExtension(entry, ".pbix") : entry, false);
        var fingerprint = await ReadFingerprintAsync(entry, default);
        if (scenario == "old-model")
        {
            using var expected = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(Path.Combine(root, "model", "model.bim")));
            ((TOM.MPartitionSource)expected.Model.Tables["T"].Partitions[0].Source).Expression = "#table({\"Value\"}, {{43}})";
            fingerprint = ModelConfigurationReadback.Fingerprint(expected);
        }
        var observations = 0;
        Task<DesktopFileTarget> Inspect(CancellationToken token)
        {
            observations++;
            return Task.FromResult(scenario switch
            {
                "dirty" => target with { HasUnsavedChanges = true },
                "late-dirty" when observations > 1 => target with { HasUnsavedChanges = true },
                "pid" => target with { ProcessId = 43 },
                "restart" => target with { Started = target.Started.AddSeconds(1) },
                "path" => target with { Path = entry + ".other.pbip" },
                _ => target
            });
        }
        var reads = 0;
        Assert.False(await DesktopFileSaveRecovery.TryConfirmAsync(target, fingerprint, Inspect,
            token => scenario == "late-model" && ++reads > 1 ? Task.FromResult(new string('0', 64)) : ReadFingerprintAsync(entry, token), TimeSpan.Zero, default));
    }

    /// <summary>确认等待仍可取消, 取消不会被提升为已保存完成.</summary>
    [Fact]
    public async Task CancelsWhileWaitingForStableCleanState()
    {
        var entry = CreateProject(); var target = new DesktopFileTarget(42, DateTime.UtcNow, entry, false);
        var fingerprint = await ReadFingerprintAsync(entry, default); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DesktopFileSaveRecovery.TryConfirmAsync(target, fingerprint,
            _ => Task.FromResult(target), async token => { var observed = await ReadFingerprintAsync(entry, token); cancellation.Cancel(); return observed; },
            TimeSpan.FromSeconds(2), cancellation.Token));
    }

    private static async Task<string> ReadFingerprintAsync(string entry, CancellationToken token) =>
        await ModelConfigurationReadback.FingerprintFileAsync(await new ProjectReader().ReadAsync(entry, token), token);

    private string CreateProject()
    {
        void Write(string file, string value) { var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        Write("owned.pbip", """{"version":"1.0","artifacts":[{"report":{"path":"report"}}]}""");
        Write("report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../model"}}}""");
        Write("report/definition/report.json", "{}"); Write("report/definition/pages/pages.json", """{"pageOrder":[]}""");
        Write("model/definition.pbism", """{"version":"1.0"}""");
        using var database = TOM.JsonSerializer.DeserializeDatabase("""{"name":"owned","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"T","columns":[{"name":"Value","dataType":"int64","sourceColumn":"Value"}],"partitions":[{"name":"T","mode":"import","source":{"type":"m","expression":"#table({\"Value\"}, {{42}})"}}]}]}}""");
        Write("model/model.bim", TOM.JsonSerializer.SerializeDatabase(database));
        return Path.Combine(root, "owned.pbip");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
