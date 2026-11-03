// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopModelDraftTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 原 TOM 会话直接暂存的对象身份、事务、失败取消清理及提交未知行为测试.

using PbiUtils.Windows.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Models;
using PbiUtils.Application.Calendar;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Windows;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopModelDraftTests
{
    /// <summary>原对象校验保留模型/表/列引用和 SDK 推导类型, 保存委托收到的就是原会话实际定义.</summary>
    [Fact]
    public async Task ApplyAndSubmitKeepOriginalObjectsAndInferredTypes()
    {
        using var fixture = new Fixture();
        var model = fixture.Database.Model;
        var table = model.Tables["Dates"];
        var column = table.Columns["Date"];
        column.IsDataTypeInferred = true;
        table.LineageTag = "6042f8a9-9d14-40f4-a458-fbe7e3527af1";
        model.Annotations.Add(new TOM.Annotation { Name = "Keep.Business", Value = "approved" });
        await using var operation = fixture.Create();

        await operation.ApplyAsync(new("table.set", "Dates", "description", "direct"), default);
        Assert.Same(model, fixture.Database.Model);
        Assert.Same(table, model.Tables["Dates"]);
        Assert.Same(column, table.Columns["Date"]);
        Assert.Equal(TOM.DataType.DateTime, column.DataType);
        Assert.True(column.IsDataTypeInferred);
        Assert.Equal("6042f8a9-9d14-40f4-a458-fbe7e3527af1", table.LineageTag);
        Assert.Equal("approved", model.Annotations["Keep.Business"].Value);
        Assert.Equal("direct", operation.Snapshot.Model.Tables.Single().Description);
        Assert.Equal(["begin"], fixture.Events);

        var saved = await operation.SaveAsync(() => fixture.Events.Add("committing"), default);
        Assert.Equal("direct", saved.Model.Tables.Single().Description);
        Assert.Equal(ModelConfigurationReadback.Fingerprint(fixture.Database), fixture.SubmittedFingerprint);
        await operation.DisposeAsync();
        Assert.Equal(["begin", "committing", "submit", "commit", "dispose"], fixture.Events);
    }

    /// <summary>启动事务失败不改原对象, 关闭本次连接而不假定失败的 Begin 已成功.</summary>
    [Fact]
    public async Task FailedTransactionStartClosesSessionWithoutMutation()
    {
        using var fixture = new Fixture { FailBegin = true };
        await using var operation = fixture.Create();
        var before = ModelConfigurationReadback.Fingerprint(fixture.Database);
        var error = await Assert.ThrowsAsync<IOException>(() => operation.ApplyAsync(new("table.set", "Dates", "description", "blocked"), default));
        Assert.Equal("begin failed", error.Message);
        Assert.Equal(true, error.Data["DesktopTransactionStartUnconfirmed"]);
        Assert.Equal(before, ModelConfigurationReadback.Fingerprint(fixture.Database));
        Assert.Equal(["begin", "dispose"], fixture.Events);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation.ApplyAsync(new("table.set", "Dates", "description", "again"), default));
    }

    /// <summary>SDK/对象校验失败结束事务并关闭会话, 不调用保存入口.</summary>
    [Fact]
    public async Task InvalidOriginalObjectRollsBackAndClosesSession()
    {
        using var fixture = new Fixture();
        await using var operation = fixture.Create();
        Assert.Equal("OBJECT_NOT_FOUND", (await Assert.ThrowsAsync<AppException>(() => operation.ApplyAsync(new("table.set", "Missing", "description", "bad"), default))).Code);
        Assert.Equal(["begin", "rollback", "dispose"], fixture.Events);
    }

    /// <summary>取消在事务启动后被观察时, 原对象尚未修改且本次事务和连接已结束.</summary>
    [Fact]
    public async Task CancellationAfterBeginDoesNotMutateOriginal()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture { OnBegin = cancellation.Cancel };
        await using var operation = fixture.Create();
        var before = ModelConfigurationReadback.Fingerprint(fixture.Database);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ApplyAsync(new("table.set", "Dates", "description", "bad"), cancellation.Token));
        Assert.Equal(before, ModelConfigurationReadback.Fingerprint(fixture.Database));
        Assert.Equal(["begin", "rollback", "dispose"], fixture.Events);
    }

    /// <summary>第二次 Apply 调度前已取消也必须结束第一轮 dirty 原会话, 不能因 Task.Run 跳过委托而泄漏事务.</summary>
    [Fact]
    public async Task AlreadyCancelledSecondApplyDisposesExistingTransaction()
    {
        using var fixture = new Fixture();
        await using var operation = fixture.Create();
        await operation.ApplyAsync(new("table.set", "Dates", "description", "first"), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ApplyAsync(new("table.set", "Dates", "description", "second"), new(true)));
        Assert.Equal(["begin", "rollback", "dispose"], fixture.Events);
        Assert.Equal("first", fixture.Database.Model.Tables["Dates"].Description);
    }

    /// <summary>用户在确认阶段取消只释放原会话和事务, 不提交任何模型修改.</summary>
    [Fact]
    public async Task DisposingPreparedOperationRollsBackExactlyOnce()
    {
        using var fixture = new Fixture();
        await using var operation = fixture.Create();
        await operation.ApplyAsync(new("table.set", "Dates", "description", "prepared"), default);
        await operation.DisposeAsync();
        await operation.DisposeAsync();
        Assert.Equal(["begin", "rollback", "dispose"], fixture.Events);
        Assert.Null(fixture.SubmittedFingerprint);
    }

    /// <summary>SaveChanges 或 Commit 结果未知只尝试一次, 保留原错误并结束本次事务和会话.</summary>
    /// <param name="failCommit">true 在 Commit 失败, false 在实际提交失败.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSubmissionIsNeverReplayed(bool failCommit)
    {
        using var fixture = new Fixture { FailSubmit = !failCommit, FailCommit = failCommit };
        await using var operation = fixture.Create();
        await operation.ApplyAsync(new("table.set", "Dates", "description", "unknown"), default);
        await Assert.ThrowsAsync<IOException>(() => operation.SaveAsync(() => fixture.Events.Add("committing"), default));
        Assert.Equal(1, fixture.Events.Count(value => value == "submit"));
        Assert.Equal(1, fixture.Events.Count(value => value == "rollback"));
        Assert.Equal(1, fixture.Events.Count(value => value == "dispose"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation.SaveAsync(null, default));
    }

    /// <summary>Commit 已确认而完整回读失败时不得回滚或重复提交已经完成的事务.</summary>
    [Fact]
    public async Task FailedReadbackAfterConfirmedCommitDoesNotRollback()
    {
        using var fixture = new Fixture { FailReadback = true };
        await using var operation = fixture.Create();
        await operation.ApplyAsync(new("table.set", "Dates", "description", "saved"), default);
        Assert.Equal("EXECUTION_NEEDS_VERIFICATION", (await Assert.ThrowsAsync<AppException>(() => operation.SaveAsync(null, default))).Code);
        Assert.Equal(["begin", "submit", "commit", "dispose"], fixture.Events);
    }

    /// <summary>回滚异常不掩盖原始提交失败, 但留下可审计的安全事实并仍关闭连接.</summary>
    [Fact]
    public async Task FailedRollbackPreservesPrimarySubmissionError()
    {
        using var fixture = new Fixture { FailSubmit = true, FailRollback = true };
        await using var operation = fixture.Create();
        await operation.ApplyAsync(new("table.set", "Dates", "description", "unknown"), default);
        var error = await Assert.ThrowsAsync<IOException>(() => operation.SaveAsync(null, default));
        Assert.Equal("submit failed", error.Message);
        Assert.Equal(true, error.Data["DesktopTransactionRollbackFailed"]);
        Assert.Equal(["begin", "submit", "rollback", "dispose"], fixture.Events);
    }

    /// <summary>确认取消后的回滚失败必须明确报告, 仍释放连接且不提交模型.</summary>
    [Fact]
    public async Task FailedAbandonReportsCleanupFailureAndClosesConnection()
    {
        using var fixture = new Fixture { FailRollback = true };
        var operation = fixture.Create();
        await operation.ApplyAsync(new("table.set", "Dates", "description", "prepared"), default);
        Assert.Equal("DESKTOP_SESSION_CLEANUP_FAILED", (await Assert.ThrowsAsync<AppException>(() => operation.DisposeAsync().AsTask())).Code);
        Assert.Equal(["begin", "rollback", "dispose"], fixture.Events);
        Assert.Null(fixture.SubmittedFingerprint);
    }

    /// <summary>同步 Dispose 只发起线程池清理, DisposeAsync 等待其结束, 不把阻塞 SDK 放在调用线程.</summary>
    [Fact]
    public async Task SessionCleanupRunsOffCallerAndIsAwaited()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture
        {
            OnRollback = () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("test cleanup was not released");
        }
        };
        var operation = fixture.Create();
        await operation.ApplyAsync(new("table.set", "Dates", "description", "prepared"), default);
        try
        {
            operation.Dispose();
            var completion = operation.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(completion.IsCompleted);
            release.Set();
            await completion;
            Assert.Equal(["begin", "rollback", "dispose"], fixture.Events);
        }
        finally { release.Set(); await operation.DisposeAsync(); }
    }

    /// <summary>只读日期规划与函数分析读取原对象但不开始写事务, 前后完整定义保持一致.</summary>
    [Fact]
    public async Task ReadOnlyPreparationDoesNotStartTransactionOrChangeOriginal()
    {
        using var fixture = new Fixture();
        await using var operation = fixture.Create();
        var before = ModelConfigurationReadback.Fingerprint(fixture.Database);
        var program = await operation.PreviewCalendarAsync(new("simple", 2026, 2026, "NewDates"), "zh-cn", default);
        Assert.Equal(ScaffoldCalendarM.FunctionName, program.FunctionName);
        await operation.AnalyzeUdfsAsync(default);
        Assert.Equal(before, ModelConfigurationReadback.Fingerprint(fixture.Database));
        Assert.Empty(fixture.Events);
    }

    /// <summary>只用真实 TOM 对象模拟一个客户端, 服务器副作用边界由显式事件记录而非真实引擎替身成功.</summary>
    private sealed class Fixture : IDisposable
    {
        public TOM.Database Database { get; } = TOM.JsonSerializer.DeserializeDatabase("""{"id":"original","name":"original","compatibilityLevel":1702,"model":{"culture":"en-US","tables":[{"name":"Dates","columns":[{"name":"Date","sourceColumn":"Date","dataType":"dateTime"}],"partitions":[{"name":"Dates","mode":"import","source":{"type":"m","expression":"#table({\"Date\"}, {{#date(2026, 1, 1)}})"}}]}]}}""");
        public List<string> Events { get; } = [];
        public bool FailBegin { get; init; }
        public bool FailSubmit { get; init; }
        public bool FailCommit { get; init; }
        public bool FailReadback { get; init; }
        public bool FailRollback { get; init; }
        public Action? OnBegin { get; init; }
        public Action? OnRollback { get; init; }
        public string? SubmittedFingerprint { get; private set; }

        /// <summary>创建唯一原对象操作上下文, 保存入口只观察同一 Database 定义和一次提交事件.</summary>
        public DesktopModelDraft Create()
        {
            var baseline = new ProjectSnapshot("model://original", "identity", ModelConfigurationReadback.Fingerprint(Database), "online", "", "", null,
                ModelSnapshotMapper.Map(Database), [], [], [], new("desktop", "Desktop"));
            return new(baseline, Database, new Session(Events), () =>
            {
                Events.Add("begin");
                if (FailBegin) throw new IOException("begin failed");
                OnBegin?.Invoke();
            }, () =>
            {
                Events.Add("rollback");
                OnRollback?.Invoke();
                if (FailRollback) throw new IOException("rollback failed");
            }, (_, committing, committed, _) =>
            {
                committing?.Invoke(); Events.Add("submit");
                if (FailSubmit) throw new IOException("submit failed");
                SubmittedFingerprint = ModelConfigurationReadback.Fingerprint(Database);
                Events.Add("commit");
                if (FailCommit) throw new IOException("commit failed");
                committed();
                if (FailReadback) throw new AppException("EXECUTION_NEEDS_VERIFICATION", "readback", 4);
                return Task.FromResult(baseline with { Model = ModelSnapshotMapper.Map(Database), BaselineId = SubmittedFingerprint, HasPendingChanges = false });
            });
        }

        public void Dispose() => Database.Dispose();
        private sealed class Session(List<string> events) : IDisposable
        {
            public void Dispose() => events.Add("dispose");
        }
    }
}
