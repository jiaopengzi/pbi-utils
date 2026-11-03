// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopPendingQueriesTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用独立真实 PBIP/PBIR 文件验证待应用查询保护、精确绑定及部分保存事实保留.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using System.Diagnostics;
using System.Reflection;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>只读自有临时工程; 不连接实际 Desktop, 不处理或改写待应用查询.</summary>
public sealed class DesktopPendingQueriesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-desktop-pending-" + Guid.NewGuid().ToString("N"));

    /// <summary>真实绑定模型有待应用文件时即拒绝, 空壳/损坏正文和 clean 标志都不能放行.</summary>
    [Theory]
    [InlineData(false, "{}")]
    [InlineData(false, "broken")]
    [InlineData(false, "")]
    [InlineData(true, "{\"queries\":[{\"name\":\"owned\",\"text\":[\"1\"]}]}")]
    public async Task ExistingPendingFileRejectsWithoutReadingOrChangingItsBody(bool pbir, string content)
    {
        var entry = Project("owned", "Report", "Model");
        var pending = Write("Model/.pbi/unappliedChanges.json", content);
        var original = File.ReadAllBytes(pending);
        // 独占内容句柄证明检查不需要打开正文, FileShare.None 不妨碍读取文件属性.
        using (var lease = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<AppException>(() => DesktopPendingQueries.RequireNoneAsync(Target(pbir ? Path.Combine(root, "Report/definition.pbir") : entry), default));
            Assert.Equal("DESKTOP_QUERY_CHANGES_PENDING", error.Code);
        }
        Assert.Equal(original, File.ReadAllBytes(pending));
    }

    /// <summary>没有挂起文件时不误查其他模型或未绑定报告的同名文件, PBIR 使用实际报告绑定.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChecksOnlyExactBoundModelAmongSiblingProjects(bool pbir)
    {
        var entry = Project("owned", "Report", "Model");
        Project("other", "OtherReport", "OtherModel");
        Write("OtherModel/.pbi/unappliedChanges.json", "preserve unrelated pending");
        Write("Report/.pbi/unappliedChanges.json", "not semantic model pending");
        await DesktopPendingQueries.RequireNoneAsync(Target(pbir ? Path.Combine(root, "Report/definition.pbir") : entry), default);
    }

    /// <summary>PBIR 同目录存在唯一却不匹配的 PBIP 时必须拒绝, 不使用兄弟工程的清洁状态.</summary>
    [Fact]
    public async Task PbirNeverGuessesUnrelatedUniqueProject()
    {
        Project("other", "OtherReport", "OtherModel");
        var unbound = Write("UnboundReport/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../UnboundModel"}}}""");
        var error = await Assert.ThrowsAsync<AppException>(() => DesktopPendingQueries.RequireNoneAsync(Target(unbound), default));
        Assert.Equal("DESKTOP_FILE_AMBIGUOUS", error.Code);
    }

    /// <summary>重复项目映射到同一 PBIR 时拒绝歧义, 不按遍历顺序挑一个入口.</summary>
    [Fact]
    public async Task PbirRejectsMultipleMatchingProjectBindings()
    {
        Project("first", "Report", "Model");
        Write("second.pbip", """{"version":"1.0","artifacts":[{"report":{"path":"Report"}}]}""");
        var error = await Assert.ThrowsAsync<AppException>(() => DesktopPendingQueries.RequireNoneAsync(Target(Path.Combine(root, "Report/definition.pbir")), default));
        Assert.Equal("DESKTOP_FILE_AMBIGUOUS", error.Code);
    }

    /// <summary>绑定越出项目的未知模型拒绝, 取消在解析前传播, 均不触及候选正文.</summary>
    [Fact]
    public async Task UnsafeModelBindingAndCancellationCannotBecomeClean()
    {
        var entry = Project("owned", "Report", "Model");
        Write("Report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../../outside"}}}""");
        Assert.Equal("UNSAFE_PROJECT_PATH", (await Assert.ThrowsAsync<AppException>(() => DesktopPendingQueries.RequireNoneAsync(Target(entry), default))).Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DesktopPendingQueries.RequireNoneAsync(Target(entry), new CancellationToken(true)));
    }

    /// <summary>PBIX 不解析二进制或猜测旁边模型目录; 通过只表示此项检测不适用.</summary>
    [Fact]
    public async Task PbixDoesNotInspectBinaryOrGuessPendingFolder()
    {
        var binary = Write("owned.pbix", "not a PBIX parser fixture");
        Write(".pbi/unappliedChanges.json", "unrelated");
        await DesktopPendingQueries.RequireNoneAsync(Target(binary), default);
    }

    /// <summary>尾部失败及重试保留已经证明的文件阶段, 重试只补同步而不创建或提交模型草稿.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task TailFailureAndRetryPreserveProvenStagesWithoutReplayingEdits(bool reload, bool fileSaved, bool reloaded)
    {
        var entry = Project("owned", "Report", "Model");
        var snapshot = await new ProjectReader().ReadAsync(entry, default);
        var workspace = new ForbiddenEdits();
        var reader = new ConnectedModelReader(new ProjectReader(), fileEdits: workspace);
        using var self = Process.GetCurrentProcess();
        // 本测试进程不是 Desktop, 身份验证在发出 Bridge 请求前确定失败, 不操作任何真实窗口.
        var ticket = new DesktopSyncTicket(new(self.Id, self.StartTime.ToUniversalTime(), entry, false), reload) { FileSaved = fileSaved, Reloaded = reloaded };
        var finish = typeof(ConnectedModelReader).GetMethod("FinishFileSaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var saved = await (Task<ProjectSnapshot>)finish.Invoke(reader, [snapshot, ticket])!;
        AssertStages(saved);
        var retried = await reader.CompleteSaveAsync(saved, null, default);
        AssertStages(retried);
        Assert.Equal(0, workspace.Created);

        void AssertStages(ProjectSnapshot actual)
        {
            Assert.True(actual.SaveResult!.ModelSaved);
            Assert.Equal(fileSaved, actual.SaveResult.FileSaved);
            Assert.Equal(reloaded, actual.SaveResult.DesktopSynchronized);
            Assert.Equal("DESKTOP_INSTANCE_CHANGED", actual.SaveResult.ErrorCode);
        }
    }

    /// <summary>生成只供文件检查的固定 PBIP/BIM 工程, 不执行 M 或数据源.</summary>
    private string Project(string name, string report, string model)
    {
        var entry = Write(name + ".pbip", "{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"" + report + "\"}}]}");
        Write(report + "/definition.pbir", "{\"version\":\"4.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../" + model + "\"}}}");
        Write(report + "/definition/report.json", "{}");
        Write(report + "/definition/pages/pages.json", """{"pageOrder":[]}""");
        Write(model + "/definition.pbism", """{"version":"1.0"}""");
        Write(model + "/model.bim", """{"name":"owned","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[]}}""");
        return entry;
    }

    /// <summary>只写入本测试 Guid 根目录下的固定文件, 返回真实完整路径.</summary>
    private string Write(string relative, string content)
    {
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
    }

    /// <summary>构造不用于进程动作的受控文件检查参数; helper 只检查其中实际文件绑定.</summary>
    private static DesktopFileTarget Target(string path) => new(42, DateTime.UnixEpoch, path, false);

    /// <summary>检测只补保存路径意外重建模型草稿, 一旦调用就立即失败.</summary>
    private sealed class ForbiddenEdits : IModelEditWorkspace
    {
        internal int Created { get; private set; }
        public Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token)
        { Created++; throw new InvalidOperationException("尾部重试不可重建模型草稿"); }
    }

    /// <summary>仅清理本测试拥有的临时 Guid 目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
