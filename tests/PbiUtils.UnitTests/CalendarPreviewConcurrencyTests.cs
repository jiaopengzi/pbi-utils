// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarPreviewConcurrencyTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证后台只读预览不占用模型提交门闩, 用户立即应用无需重试.

using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Diagnostics;
using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Application.Models.Configuration;
using PbiUtils.Application.Calendar;
using System.Collections.Concurrent;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class CalendarPreviewConcurrencyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>预览停在私有克隆的编译阶段时, 真正应用仍完成; 迟到预览不得回填旧基线.</summary>
    [Fact]
    public async Task ApplyingImmediatelyDoesNotFailBecauseOwnPreviewIsRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbi-preview-concurrency-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entry = PbipFixture.Create(Path.Combine(root, "project"));
            var reader = new ProjectReader();
            using var workspace = new ChangeWorkspace(Path.Combine(root, "user"));
            using var edits = new ModelEditService(reader, workspace);
            using var compiler = new ModelConfigurationCompiler();
            var blocking = new BlockingWorkspace(workspace);
            using var configuration = new ModelConfigurationBridge(reader, blocking, compiler, currentWorkspace: blocking);
            var snapshot = await reader.ReadAsync(entry, default);
            configuration.BindCreated(new("plan", "published", true, true, false, entry, snapshot.BaselineId, [], []));
            var messages = new ConcurrentQueue<string>();
            using var bridge = new BridgeSession(new OriginPolicy(false, null), new DoctorService(new RuntimeEnvironment()),
                messages.Enqueue, "test",
                projects: new ProjectService(edits), selectProject: _ => Task.FromResult<string?>(entry), edits: edits,
                configuration: configuration);
            Task Request(string id, string method, object parameters) => bridge.ReceiveAsync(OriginPolicy.Production,
                JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }, Json));
            await Request("context", "configuration.context", new { target = "created" });
            using var context = JsonDocument.Parse(messages.Last(text => text.Contains("\"id\":\"context\"", StringComparison.Ordinal)));
            var result = context.RootElement.GetProperty("result");
            var targetId = result.GetProperty("targetId").GetString()!;
            var baselineId = result.GetProperty("project").GetProperty("baselineId").GetString()!;
            var options = new ScaffoldCalendarOptions("simple", 2024, 2024, "Dates");
            var preview = Request("preview", "calendar.preview", new { context = new { kind = "model", targetId, baselineId }, calendar = options, locale = "zh-cn" });
            try
            {
                await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Request("apply", "configuration.apply", new { targetId, baselineId, section = "calendar", request = new ModelConfigurationRequest(Calendar: options) });
                using var applied = JsonDocument.Parse(messages.Last(text => text.Contains("\"id\":\"apply\"", StringComparison.Ordinal)));
                Assert.False(applied.RootElement.TryGetProperty("error", out var error), error.ToString());
                Assert.Contains(applied.RootElement.GetProperty("result").GetProperty("project").GetProperty("model").GetProperty("tables").EnumerateArray(), table => table.GetProperty("name").GetString() == "Dates");
            }
            finally { blocking.Release.TrySetResult(); await preview; }
            using var stalePreview = JsonDocument.Parse(messages.Last(text => text.Contains("\"id\":\"preview\"", StringComparison.Ordinal)));
            Assert.Equal("STALE_CONFIGURATION_TARGET", stalePreview.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BlockingWorkspace(IModelEditWorkspace inner) : IModelEditWorkspace
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IModelDraft> CreateDraftAsync(ProjectSnapshot baseline, CancellationToken token) =>
            new BlockingDraft(await inner.CreateDraftAsync(baseline, token), Started, Release);
    }

    private sealed class BlockingDraft(IModelDraft inner, TaskCompletionSource started, TaskCompletionSource release) : IModelDraft, ICalendarProgramDraft
    {
        public ProjectSnapshot Snapshot => inner.Snapshot;
        public Task ApplyAsync(ModelEditRequest request, CancellationToken token) => inner.ApplyAsync(request, token);
        public Task<ProjectSnapshot> SaveAsync(Action? committing, CancellationToken token) => inner.SaveAsync(committing, token);
        public async Task<ScaffoldCalendarProgram> PreviewCalendarAsync(ScaffoldCalendarOptions options, string locale, CancellationToken token)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return await ((ICalendarProgramDraft)inner).PreviewCalendarAsync(options, locale, token);
        }
        public void Dispose() => inner.Dispose();
    }
}
