// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PrototypeAdjustmentTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证原地直接写入、历史文件保留、冲突拒绝边界.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;
namespace PbiUtils.UnitTests;

public sealed class PrototypeAdjustmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-adjustment-" + Guid.NewGuid().ToString("N"));

    /// <summary>文件夹具经过真实原地文件事务, 连续修改不创建历史备份, 旧基线拒绝外部修改且现有历史文件原样保留.</summary>
    [Fact]
    public async Task InPlaceUpdatesWithoutHistoryAndRejectsStaleSource()
    {
        Directory.CreateDirectory(root);
        using var changes = new ChangeService(new ChangeWorkspace(Path.Combine(root, "user")), TimeProvider.System);
        var fixture = PbipFixture.Create(Path.Combine(root, "project"));
        var created = new ChangeResult("fixture", "fixture", fixture, "", []);
        var projects = new ProjectService(new ProjectReader());
        var original = await projects.InspectAsync(created.ProjectPath, default);
        var measure = Assert.Single(original.Model.Measures);
        var request = new ChangeRequest(created.ProjectPath, Path.GetDirectoryName(created.ProjectPath)!, "measure.set", measure.Name, "description", "第一版", measure.Table, InPlace: true);
        var plan = await changes.PrepareAsync(request, default);
        Assert.Equal(original.BaselineId, (await projects.InspectAsync(created.ProjectPath, default)).BaselineId);
        var result = await changes.ApplyAsync(plan.Id, plan.Digest, default);
        Assert.Equal("files_updated", result.Stage);
        Assert.Equal("第一版", Assert.Single((await projects.InspectAsync(created.ProjectPath, default)).Model.Measures).Description);
        Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
        var second = await changes.PrepareAsync(request with { Value = "第二版" }, default);
        await changes.ApplyAsync(second.Id, second.Digest, default);
        Assert.Equal("第二版", Assert.Single((await projects.InspectAsync(created.ProjectPath, default)).Model.Measures).Description);
        Assert.False(Directory.Exists(Path.Combine(root, "user", "backup")));
        var stale = await changes.PrepareAsync(request with { Value = "不能覆盖" }, default);
        File.WriteAllText(Path.Combine(root, "project", "external.txt"), "另一进程");
        Assert.Equal("STALE_BASELINE", (await Assert.ThrowsAsync<AppException>(() => changes.ApplyAsync(stale.Id, stale.Digest, default))).Code);
        Assert.Equal("第二版", Assert.Single((await projects.InspectAsync(created.ProjectPath, default)).Model.Measures).Description);
        var cancelled = await changes.PrepareAsync(request, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changes.ApplyAsync(cancelled.Id, cancelled.Digest, new(true)));
        changes.Discard(cancelled.Id);
        Assert.Equal("第二版", Assert.Single((await projects.InspectAsync(created.ProjectPath, default)).Model.Measures).Description);
        var historical = Path.Combine(root, "user", "backup", "unreadable-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(historical)!);
        File.WriteAllText(historical, "not a valid historical manifest");
        var direct = await changes.PrepareAsync(request with { Value = "第三版" }, default);
        await changes.ApplyAsync(direct.Id, direct.Digest, default);
        Assert.Equal("第三版", Assert.Single((await projects.InspectAsync(created.ProjectPath, default)).Model.Measures).Description);
        Assert.Equal("另一进程", File.ReadAllText(Path.Combine(root, "project", "external.txt")));
        Assert.Equal("not a valid historical manifest", File.ReadAllText(historical));
        Assert.Equal([historical], Directory.GetFiles(Path.Combine(root, "user", "backup"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(root, ".pbi-utils-stage-*"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
