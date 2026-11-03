// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopModelFileOpenerTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证本地文件通过 Desktop 打开、精确复用与取消边界, 不启动用户窗口.

using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Application.Connections;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>以独立临时入口和可控 Desktop 状态验证实际启动策略.</summary>
public sealed class DesktopModelFileOpenerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-utils-desktop-open-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Started = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>明确仅含远端绑定的 PBIP 不启动 Desktop 或等待本地模型.</summary>
    [Fact]
    public async Task RejectsRemoteReportBeforeLaunching()
    {
        var path = Project("remote.pbip", "Remote.Report");
        FilePath(Path.Combine("Remote.Report", "definition.pbir"), """{"version":"4.0","datasetReference":{"byConnection":{"connectionString":"semanticmodelid=example"}}}""");
        var runtime = new FakeRuntime();
        var error = await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default));
        Assert.Equal("DESKTOP_REMOTE_REPORT", error.Code);
        Assert.Equal(0, runtime.Launched);
        Assert.Equal(1, runtime.Inspections);
    }

    /// <summary>磁盘仍为远端引用但已打开窗口有本地模型时, 以实际模型为准复用而不误拒绝.</summary>
    [Fact]
    public async Task RemoteDiskBindingDoesNotOverrideOpenLocalModel()
    {
        var path = Project("converted.pbip", "Converted.Report");
        var report = FilePath(Path.Combine("Converted.Report", "definition.pbir"), """{"version":"4.0","datasetReference":{"byConnection":{"connectionString":"semanticmodelid=example"}}}""");
        var runtime = new FakeRuntime { States = _ => [new(21, Started, report, true)] };
        var target = await Opener(runtime).OpenAsync(path, default);
        Assert.Equal(21, target.ProcessId);
        await Opener(runtime).VerifyAsync(target, default);
        Assert.Equal(0, runtime.Launched);
    }

    /// <summary>Bridge 已确认目标文件且没有本地模型时立即终止, 不等到超时或重复启动.</summary>
    [Theory]
    [InlineData("DESKTOP_NO_LOCAL_MODEL")]
    [InlineData("DESKTOP_REMOTE_REPORT")]
    public async Task StopsForVerifiedFileWithoutLocalModel(string code)
    {
        var path = FilePath("remote.pbix");
        var runtime = new FakeRuntime { States = _ => [new(21, Started, path, false, ModelErrorCode: code)] };
        Assert.Equal(code, (await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default))).Code);
        Assert.Equal(1, runtime.Inspections);
        Assert.Equal(0, runtime.Launched);
    }

    /// <summary>加载期间继续等待, 文件就绪但无模型的终态才结束打开.</summary>
    [Fact]
    public async Task WaitsForBridgeBeforeReportingNoLocalModel()
    {
        var path = FilePath("loading-remote.pbix");
        var runtime = new FakeRuntime
        {
            States = call => [new(21, Started, path, false,
                ModelErrorCode: call >= 3 ? DesktopModelAvailability.NoLocalModelCode : null)]
        };
        Assert.Equal(DesktopModelAvailability.NoLocalModelCode, (await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default))).Code);
        Assert.Equal(3, runtime.Inspections);
    }

    /// <summary>远端判断不接受混合绑定或重复属性, 避免按 JSON 解析差异选择模型.</summary>
    [Theory]
    [InlineData("{\"datasetReference\":{\"byConnection\":{\"connectionString\":\"remote\"},\"byPath\":{\"path\":\"../Model\"}}}")]
    [InlineData("{\"datasetReference\":{\"byConnection\":{},\"byConnection\":{\"connectionString\":\"remote\"}}}")]
    public void RejectsAmbiguousRemoteBindings(string definition)
    {
        var path = FilePath("definition.pbir", definition);
        Assert.Equal("LOCAL_MODEL_FILE_INVALID", Assert.Throws<AppException>(() => DesktopModelFileIdentity.HasRemoteBinding(path)).Code);
    }

    /// <summary>已有相同完整 PBIX 路径直接复用, 不依赖本机安装解析成功.</summary>
    [Fact]
    public async Task ReusesExactPbixWithoutLaunching()
    {
        var path = FilePath("same.pbix");
        var runtime = new FakeRuntime { States = _ => [new(21, Started, path.ToUpperInvariant(), true)], Installed = false };
        var target = await Opener(runtime).OpenAsync(path, default);
        Assert.Equal(new LocalModelFileTarget(path, 21, Started), target);
        Assert.Equal(0, runtime.Launched);
        Assert.Equal(0, runtime.Resolved);
    }

    /// <summary>多个 PBIP 同目录时按各自真实报表绑定复用, 不猜测唯一目录入口.</summary>
    [Fact]
    public async Task ReusesOnlyBoundPbirAmongProjects()
    {
        var first = Project("first.pbip", "First.Report");
        var second = Project("second.pbip", "Second.Report");
        var firstReport = Path.Combine(root, "First.Report", "definition.pbir");
        var secondReport = Path.Combine(root, "Second.Report", "definition.pbir");
        var runtime = new FakeRuntime { States = _ => [new(21, Started, firstReport, true), new(22, Started, secondReport, true)] };
        Assert.Equal(22, (await Opener(runtime).OpenAsync(second, default)).ProcessId);
        Assert.True(DesktopModelFileIdentity.Read(first).Matches(firstReport));
        Assert.False(DesktopModelFileIdentity.Read(first).Matches(secondReport));
        Assert.Equal(0, runtime.Launched);
    }

    /// <summary>不同目录的同名文件不能被当成已打开的所选文件.</summary>
    [Fact]
    public async Task OpensSameNameFromDifferentDirectory()
    {
        var path = FilePath("same.pbix");
        var other = FilePath(Path.Combine("other", "same.pbix"));
        var runtime = new FakeRuntime { States = call => call <= 2 ? [new(21, Started, other, true)] : [new(21, Started, other, true), new(22, Started, path, true)] };
        var target = await Opener(runtime).OpenAsync(path, default);
        Assert.Equal(22, target.ProcessId);
        Assert.Equal(1, runtime.Launched);
        Assert.Equal(path, runtime.LaunchedPath);
    }

    /// <summary>同一完整文件在多个窗口打开时拒绝歧义并保留所有窗口.</summary>
    [Fact]
    public async Task RejectsDuplicateFileInstances()
    {
        var path = FilePath("same.pbix");
        var runtime = new FakeRuntime { States = _ => [new(21, Started, path, true), new(22, Started, path, true)] };
        Assert.Equal("DESKTOP_FILE_AMBIGUOUS", (await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default))).Code);
        Assert.Equal(0, runtime.Launched);
    }

    /// <summary>已有匹配窗口尚未启动引擎时只等待, 不重复启动 Desktop.</summary>
    [Fact]
    public async Task WaitsForExistingFileEngine()
    {
        var path = FilePath("waiting.pbix");
        var runtime = new FakeRuntime { States = call => [new(21, Started, path, call >= 3)] };
        Assert.Equal(21, (await Opener(runtime).OpenAsync(path, default)).ProcessId);
        Assert.Equal(0, runtime.Launched);
        Assert.True(runtime.Inspections >= 3);
    }

    /// <summary>无本机安装时返回简短安装建议, 不启动任意文件关联程序.</summary>
    [Fact]
    public async Task ReportsMissingInstallation()
    {
        var path = FilePath("missing.pbix");
        var runtime = new FakeRuntime { Installed = false };
        var error = await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default));
        Assert.Equal("DESKTOP_NOT_INSTALLED", error.Code);
        Assert.Contains("安装", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, runtime.Launched);
    }

    /// <summary>无法核验已有窗口时不冒险重复打开同一文件.</summary>
    [Fact]
    public async Task BlocksLaunchWhenExistingFileIdentityCannotBeRead()
    {
        var path = FilePath("unknown.pbix");
        var runtime = new FakeRuntime { States = _ => [new(21, Started, null, false, "DESKTOP_BRIDGE_UNAVAILABLE")] };
        Assert.Equal("DESKTOP_FILE_IDENTITY_UNAVAILABLE", (await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default))).Code);
        Assert.Equal(0, runtime.Launched);
    }

    /// <summary>安装解析期间另一入口打开同一文件后, 启动前重新发现并直接复用.</summary>
    [Fact]
    public async Task RechecksInstancesAfterResolvingInstallation()
    {
        var path = FilePath("opened-during-resolution.pbix");
        IReadOnlyList<DesktopModelFileState> current = [];
        var runtime = new FakeRuntime { States = _ => current, OnResolve = () => current = [new(21, Started, path, true)] };
        Assert.Equal(21, (await Opener(runtime).OpenAsync(path, default)).ProcessId);
        Assert.Equal(0, runtime.Launched);
        Assert.Equal(1, runtime.Resolved);
        Assert.Equal(2, runtime.Inspections);
    }

    /// <summary>安装解析期间新增无法核验的窗口会阻止新开, 避免并发重复文件.</summary>
    [Fact]
    public async Task RejectsUnknownInstanceAppearingDuringInstallationResolution()
    {
        var path = FilePath("unknown-during-resolution.pbix");
        IReadOnlyList<DesktopModelFileState> current = [];
        var runtime = new FakeRuntime { States = _ => current, OnResolve = () => current = [new(21, Started, null, false, "DESKTOP_BRIDGE_UNAVAILABLE")] };
        Assert.Equal("DESKTOP_FILE_IDENTITY_UNAVAILABLE", (await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default))).Code);
        Assert.Equal(0, runtime.Launched);
    }

    /// <summary>启动后超时只结束连接等待, 不发出任何退出或终止操作.</summary>
    [Fact]
    public async Task BoundsLoadingWait()
    {
        var path = FilePath("loading.pbix");
        var runtime = new FakeRuntime();
        var opener = new DesktopModelFileOpener(runtime, timeout: TimeSpan.FromMilliseconds(40), pollInterval: TimeSpan.FromMilliseconds(2));
        Assert.Equal("DESKTOP_OPEN_TIMEOUT", (await Assert.ThrowsAsync<AppException>(() => opener.OpenAsync(path, default))).Code);
        Assert.Equal(1, runtime.Launched);
    }

    /// <summary>取消传播且启动次数不重放, 后端不拥有关闭用户窗口的能力.</summary>
    [Fact]
    public async Task CancelsWaitWithoutRepeatingLaunch()
    {
        var path = FilePath("cancel.pbix");
        var runtime = new FakeRuntime();
        using var cancellation = new CancellationTokenSource();
        runtime.OnLaunch = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Opener(runtime).OpenAsync(path, cancellation.Token));
        Assert.Equal(1, runtime.Launched);
    }

    /// <summary>取消已发生时不得启动 Desktop 或读取窗口.</summary>
    [Fact]
    public async Task DoesNotLaunchAfterPreexistingCancellation()
    {
        var path = FilePath("cancelled.pbix");
        var runtime = new FakeRuntime();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Opener(runtime).OpenAsync(path, new CancellationToken(true)));
        Assert.Equal(0, runtime.Launched);
        Assert.Equal(0, runtime.Inspections);
    }

    /// <summary>连接完成前重新复核启动时间和文件, PID 重用、切换及未就绪均拒绝.</summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task RejectsTargetDrift(bool restart, bool switched, bool ready)
    {
        var path = FilePath("selected.pbix");
        var other = FilePath("other.pbix");
        var runtime = new FakeRuntime { States = _ => [new(21, restart ? Started.AddSeconds(1) : Started, switched ? other : path, ready)] };
        var target = new LocalModelFileTarget(path, 21, Started);
        Assert.Equal("DESKTOP_FILE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => Opener(runtime).VerifyAsync(target, default))).Code);
    }

    /// <summary>安全参数列表保留包含空格及 Shell 特殊字符的完整单个文件路径.</summary>
    [Fact]
    public void UsesExplicitExecutableAndSingleArgument()
    {
        var path = FilePath("中文 & report name.pbix");
        var executable = @"C:\Program Files\Microsoft Power BI Desktop\bin\PBIDesktop.exe";
        var start = DesktopModelFileRuntime.CreateStartInfo(executable, path);
        Assert.False(start.UseShellExecute);
        Assert.Equal(executable, start.FileName);
        Assert.Equal(path, Assert.Single(start.ArgumentList));
        Assert.Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);
        Assert.Equal("", start.Arguments);
    }

    /// <summary>无关程序和仅伪装文件名的普通文件不能通过安装身份检查.</summary>
    [Fact]
    public void RejectsUnverifiedExecutable()
    {
        Assert.False(DesktopInstallation.IsDesktopExecutable(FilePath("PBIDesktop.exe")));
        Assert.False(DesktopInstallation.IsDesktopExecutable(Environment.ProcessPath!));
    }

    /// <summary>传统安装保留明确 EXE 参数启动, 官方 Store 目标使用 Launch 契约以避开受保护 EXE.</summary>
    [Fact]
    public void SelectsInstalledLaunchMechanism()
    {
        const string executable = @"C:\Verified\PBIDesktop.exe";
        Assert.False(DesktopModelFileRuntime.UsesStoreActivation(new(executable)));
        Assert.True(DesktopModelFileRuntime.UsesStoreActivation(new(executable, "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe!Microsoft.MicrosoftPowerBIDesktop")));
        Assert.Equal("DESKTOP_LAUNCH_FAILED", Assert.Throws<AppException>(() => DesktopModelFileRuntime.UsesStoreActivation(new(executable, "Other.Package!Application"))).Code);
        Assert.Equal("DESKTOP_LAUNCH_FAILED", Assert.Throws<AppException>(() => DesktopModelFileRuntime.UsesStoreActivation(new(executable, "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe!"))).Code);
        Assert.Equal("DESKTOP_LAUNCH_FAILED", Assert.Throws<AppException>(() => DesktopModelFileRuntime.UsesStoreActivation(new(executable, "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe!App;open"))).Code);
        var path = FilePath("中文 & store report.pbix");
        Assert.Equal("\"" + path + "\"", DesktopModelFileRuntime.QuoteStoreFileArgument(path));
    }

    /// <summary>拒绝相对、UNC、设备及替代数据流路径, 不交给 Desktop 处理.</summary>
    [Theory]
    [InlineData("report.pbix")]
    [InlineData("\\\\server\\share\\report.pbix")]
    [InlineData("\\\\?\\C:\\report.pbix")]
    [InlineData("C:\\report.pbix:stream")]
    public void RejectsUnsafeFilePaths(string path) => Assert.Equal("LOCAL_MODEL_FILE_UNSAFE", Assert.Throws<AppException>(() => DesktopModelFileIdentity.Read(path)).Code);

    /// <summary>PBIP 越界、绝对、空、重复及多报表入口不能被用作路径匹配依据.</summary>
    [Theory]
    [InlineData("{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"../escape\"}}]}")]
    [InlineData("{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"C:/escape\"}}]}")]
    [InlineData("{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"\"}}]}")]
    [InlineData("{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"First.Report\",\"path\":\"Second.Report\"}}]}")]
    [InlineData("{\"version\":\"1.0\",\"artifacts\":[{},{}]}")]
    public void RejectsUnsafeProjectBindings(string json)
    {
        var path = FilePath("invalid.pbip", json);
        Assert.Equal("LOCAL_MODEL_FILE_INVALID", Assert.Throws<AppException>(() => DesktopModelFileIdentity.Read(path)).Code);
    }

    /// <summary>在第二次实例发现边界改变入口绑定, 确定性验证复核拒绝, 不与文件读取抢占句柄.</summary>
    [Fact]
    public async Task RechecksProjectBindingBeforeReturning()
    {
        var path = Project("changing.pbip", "First.Report");
        var oldReport = Path.Combine(root, "First.Report", "definition.pbir");
        var runtime = new FakeRuntime
        {
            States = call =>
            {
                if (call == 2) Project("changing.pbip", "Second.Report");
                return [new(21, Started, oldReport, call >= 2)];
            },
        };
        Assert.Equal("LOCAL_MODEL_FILE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => Opener(runtime).OpenAsync(path, default))).Code);
        Assert.Equal(2, runtime.Inspections);
    }

    /// <summary>Open 返回后 PBIP 绑定改变时, Bridge 即使仍返回 PBIP 入口也不能通过复核.</summary>
    [Fact]
    public async Task RejectsProjectBindingChangeAfterOpen()
    {
        var path = Project("changed-after-open.pbip", "First.Report");
        var runtime = new FakeRuntime { States = _ => [new(21, Started, path, true)] };
        var opener = Opener(runtime);
        var target = await opener.OpenAsync(path, default);
        Project("changed-after-open.pbip", "Second.Report");
        Assert.Equal("LOCAL_MODEL_FILE_CHANGED", (await Assert.ThrowsAsync<AppException>(() => opener.VerifyAsync(target, default))).Code);
    }

    /// <summary>创建精确入口测试用例, 默认使用短等待避免原生进程.</summary>
    /// <param name="runtime">可控 Desktop 边界.</param>
    /// <returns>测试使用的文件打开服务.</returns>
    private static DesktopModelFileOpener Opener(FakeRuntime runtime) => new(runtime, timeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(2));

    /// <summary>创建独立普通文件, 不覆盖用户目录中的内容.</summary>
    /// <param name="relative">临时根下相对路径.</param>
    /// <param name="content">测试文件内容.</param>
    /// <returns>真实完整文件路径.</returns>
    private string FilePath(string relative, string content = "test")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>建立仅用于身份解析的 PBIP 和对应 PBIR 文件.</summary>
    /// <param name="name">临时入口名称.</param>
    /// <param name="report">对应报表相对目录.</param>
    /// <returns>PBIP 完整路径.</returns>
    private string Project(string name, string report)
    {
        FilePath(Path.Combine(report, "definition.pbir"), "{}");
        return FilePath(name, JsonSerializer.Serialize(new { version = "1.0", artifacts = new[] { new { report = new { path = report } } } }));
    }

    /// <summary>只清理本测试创建且固定在临时根下的目录.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }

    /// <summary>可控的发现和启动边界, 不提供窗口关闭或进程终止方法.</summary>
    private sealed class FakeRuntime : IDesktopModelFileRuntime
    {
        public Func<int, IReadOnlyList<DesktopModelFileState>> States { get; init; } = _ => [];
        public bool Installed { get; init; } = true;
        public int Inspections { get; private set; }
        public int Resolved { get; private set; }
        public int Launched { get; private set; }
        public string? LaunchedPath { get; private set; }
        public Action? OnLaunch { get; set; }
        public Action? OnResolve { get; init; }

        /// <inheritdoc/>
        public Task<IReadOnlyList<DesktopModelFileState>> InspectAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(States(++Inspections));
        }

        /// <inheritdoc/>
        public Task<string> ResolveExecutableAsync(CancellationToken token)
        {
            Resolved++;
            token.ThrowIfCancellationRequested();
            if (!Installed) throw new AppException("DESKTOP_NOT_INSTALLED", "请安装 Power BI Desktop", 3);
            OnResolve?.Invoke();
            return Task.FromResult(@"C:\Verified\PBIDesktop.exe");
        }

        /// <inheritdoc/>
        public Task LaunchAsync(string executable, string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Launched++;
            LaunchedPath = path;
            OnLaunch?.Invoke();
            return Task.CompletedTask;
        }
    }
}
