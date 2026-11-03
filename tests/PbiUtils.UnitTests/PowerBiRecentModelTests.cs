// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PowerBiRecentModelTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Service 最近成功连接的真实身份隔离、当前目录权限、存储损坏与取消边界.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Connections.PowerBiService;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>实际本机文件存储配合假认证目录, 不访问真实 Service 或认证账号.</summary>
public sealed class PowerBiRecentModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-service-recent-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "service-recent-models.json");

    /// <summary>按真实后台账号和云分区, 邮箱相同也不能跨账号看到历史, 同账号跨云仍隔离.</summary>
    [Fact]
    public async Task PersistsAcrossInstancesAndIsolatesActualAccountAndCloud()
    {
        var cloud = new Cloud(); var store = new LocalPowerBiRecentModelStore(root);
        var service = new PowerBiServiceConnection(cloud, recentModelStore: store);
        await service.ModelsAsync(default);
        await service.RecordConnectionAsync(service.CaptureConnection("handle-one"), default);
        var reopened = new PowerBiServiceConnection(cloud, recentModelStore: new LocalPowerBiRecentModelStore(root));
        Assert.Equal("handle-one", Assert.Single((await reopened.RecentModelsAsync(default)).Items).ModelId);
        cloud.AccountId = "different-real-account";
        Assert.Empty((await reopened.RecentModelsAsync(default)).Items);
        cloud.AccountId = "real-account"; cloud.Account = cloud.Account! with { EnvironmentName = "ChinaCloud" };
        Assert.Empty((await reopened.RecentModelsAsync(default)).Items);
        cloud.Account = null;
        Assert.Equal("POWER_BI_SIGN_IN_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => reopened.RecentModelsAsync(default))).Code);
        var persisted = File.ReadAllText(FilePath);
        Assert.DoesNotContain("real-account", persisted); Assert.DoesNotContain("test@example.com", persisted); Assert.DoesNotContain("handle-one", persisted);
    }

    /// <summary>每次重新读取目录权限, 重名不同身份不能替代旧记录, 改名后展示当前名称与新会话句柄.</summary>
    [Fact]
    public async Task RevalidatesPermissionsAndUsesStableIdsInsteadOfNames()
    {
        var cloud = new Cloud(); var service = new PowerBiServiceConnection(cloud, recentModelStore: new LocalPowerBiRecentModelStore(root));
        await service.ModelsAsync(default); await service.RecordConnectionAsync(service.CaptureConnection("handle-one"), default);
        cloud.Models = [new("new-handle", "Renamed", "Moved label", null, "workspace-one", "model-one"), new("same-name", "Sales", "Team", null, "workspace-two", "model-two")];
        var row = Assert.Single((await service.RecentModelsAsync(default)).Items);
        Assert.Equal("new-handle", row.ModelId); Assert.Equal("Renamed", row.Name); Assert.Equal("Moved label", row.Workspace);
        cloud.Models = [new("same-name", "Sales", "Team", null, "workspace-two", "model-two")];
        Assert.Empty((await service.RecentModelsAsync(default)).Items);
        cloud.Models = [new("one", "Sales", "Team", "已失去连接权限", "workspace-one", "model-one")];
        Assert.Empty((await service.RecentModelsAsync(default)).Items);
        cloud.Models = [new("one", "Sales", "Team", null, "workspace-one", "model-one"), new("ambiguous", "Sales", "Team", null, "workspace-one", "model-one")];
        Assert.Empty((await service.RecentModelsAsync(default)).Items);
        Assert.Equal(5, cloud.CatalogReads);
    }

    /// <summary>连接前捕获原认证身份, 成功后账号变化也不能把旧模型写到另一个账号或云.</summary>
    [Fact]
    public async Task SuccessfulConnectionUsesCapturedAccountAndRejectsUnboundHandles()
    {
        var cloud = new Cloud(); var store = new LocalPowerBiRecentModelStore(root);
        var service = new PowerBiServiceConnection(cloud, recentModelStore: store);
        Assert.Null(service.CaptureConnection("handle-one"));
        await service.ModelsAsync(default);
        Assert.Null(service.CaptureConnection("unknown-handle"));
        var captured = service.CaptureConnection("handle-one"); Assert.NotNull(captured);
        cloud.AccountId = "other-account"; cloud.Account = new("same@example.com", "ChinaCloud");
        await service.RecordConnectionAsync(captured, default);
        Assert.Single(await store.LoadAsync("real-account", "GlobalCloud", default));
        Assert.Empty(await store.LoadAsync("other-account", "ChinaCloud", default));
        Assert.Null(service.CaptureConnection("handle-one"));
    }

    /// <summary>最近读取等待本机存储期间切换账号并刷新目录, 原请求不能用新共享字段通过身份核验.</summary>
    [Fact]
    public async Task AccountChangeDuringRecentReadCannotReturnPreviousAccountHistory()
    {
        var cloud = new Cloud(); var store = new GatedStore();
        var service = new PowerBiServiceConnection(cloud, recentModelStore: store);
        var recent = service.RecentModelsAsync(default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cloud.AccountId = "different-real-account"; cloud.Account = new("same@example.com", "ChinaCloud");
        await service.ModelsAsync(default);
        store.Release.SetResult();
        Assert.Equal("POWER_BI_SIGN_IN_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => recent)).Code);
        Assert.Equal("real-account", store.LoadedAccountId); Assert.Equal("GlobalCloud", store.LoadedEnvironment);
    }

    /// <summary>多实例更新共享锁内合并, 同账号每云最多20条, 不覆盖另一云和账号.</summary>
    [Fact]
    public async Task MergesInstancesAndBoundsEachAccountCloudIndependently()
    {
        var first = new LocalPowerBiRecentModelStore(root); var second = new LocalPowerBiRecentModelStore(root);
        await first.SaveAsync("other", "GlobalCloud", "workspace", "other-model", default);
        await first.SaveAsync("account", "ChinaCloud", "workspace", "china-model", default);
        for (var index = 0; index < 22; index++)
            await (index % 2 == 0 ? first : second).SaveAsync("account", "GlobalCloud", "workspace", "model-" + index, default);
        var rows = await second.LoadAsync("account", "GlobalCloud", default);
        Assert.Equal(20, rows.Count); Assert.Equal("model-21", rows[0].ModelId);
        await second.SaveAsync("account", "GlobalCloud", "workspace", "model-20", default);
        Assert.Equal(20, (await first.LoadAsync("account", "GlobalCloud", default)).Count);
        Assert.Equal("model-20", (await first.LoadAsync("account", "GlobalCloud", default))[0].ModelId);
        Assert.Single(await first.LoadAsync("other", "GlobalCloud", default)); Assert.Single(await first.LoadAsync("account", "ChinaCloud", default));
    }

    /// <summary>删除只针对当前后台账号及云分区, 不能使用其他账号记录、旧模型句柄或同名模型代替身份.</summary>
    [Fact]
    public async Task DeleteIsScopedToActualAccountAndCloudAndPreservesOtherHistory()
    {
        var store = new LocalPowerBiRecentModelStore(root);
        await store.SaveAsync("real-account", "GlobalCloud", "workspace", "model", default);
        await store.SaveAsync("other-account", "GlobalCloud", "workspace", "model", default);
        await store.SaveAsync("real-account", "ChinaCloud", "workspace", "model", default);
        var id = Assert.Single(await store.LoadAsync("real-account", "GlobalCloud", default)).Id;
        var cloud = new Cloud(); var service = new PowerBiServiceConnection(cloud, recentModelStore: store);
        cloud.AccountId = "other-account";
        Assert.False((await service.DeleteRecentModelAsync(id, default)).Deleted);
        cloud.AccountId = "real-account"; cloud.Account = cloud.Account! with { EnvironmentName = "ChinaCloud" };
        Assert.False((await service.DeleteRecentModelAsync(id, default)).Deleted);
        cloud.Account = cloud.Account! with { EnvironmentName = "GlobalCloud" };
        await new LocalPowerBiRecentModelStore(root).SaveAsync("real-account", "GlobalCloud", "workspace", "newer-model", default);
        Assert.True((await service.DeleteRecentModelAsync(id, default)).Deleted);
        Assert.False((await service.DeleteRecentModelAsync(id, default)).Deleted);
        Assert.Equal("newer-model", Assert.Single(await store.LoadAsync("real-account", "GlobalCloud", default)).ModelId);
        Assert.Single(await store.LoadAsync("other-account", "GlobalCloud", default)); Assert.Single(await store.LoadAsync("real-account", "ChinaCloud", default));
        cloud.Account = null;
        Assert.Equal("POWER_BI_SIGN_IN_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => service.DeleteRecentModelAsync(id, default))).Code);
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<AppException>(() => service.DeleteRecentModelAsync("handle-one", default))).Code);
    }

    /// <summary>删除准备时账号发生变化, 提交前的真实身份复核拒绝写入原账号分区.</summary>
    [Fact]
    public async Task DeleteRechecksAccountBeforeAtomicCommit()
    {
        var cloud = new Cloud(); var store = new LocalPowerBiRecentModelStore(root);
        await store.SaveAsync("real-account", "GlobalCloud", "workspace", "model", default);
        var id = Assert.Single(await store.LoadAsync("real-account", "GlobalCloud", default)).Id;
        var gated = new GatedDeleteStore(store); var service = new PowerBiServiceConnection(cloud, recentModelStore: gated);
        var pending = service.DeleteRecentModelAsync(id, default);
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cloud.AccountId = "new-account"; gated.Release.SetResult();
        Assert.Equal("POWER_BI_SIGN_IN_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => pending)).Code);
        Assert.Single(await store.LoadAsync("real-account", "GlobalCloud", default));
    }

    /// <summary>删除取消、锁竞争及文档损坏保留原历史, 取消不覆盖已经开始的原子提交结果.</summary>
    [Fact]
    public async Task DeleteProtectsHistoryAndReportsActualCommitResult()
    {
        var store = new LocalPowerBiRecentModelStore(root); await store.SaveAsync("account", "GlobalCloud", "workspace", "model", default);
        var id = Assert.Single(await store.LoadAsync("account", "GlobalCloud", default)).Id; var before = File.ReadAllBytes(FilePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync("account", "GlobalCloud", id, new(true)));
        using (UserDataStore.Lock(FilePath + ".lock"))
            Assert.Equal("USER_DATA_BUSY", (await Assert.ThrowsAsync<AppException>(() => store.DeleteAsync("account", "GlobalCloud", id, default))).Code);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        File.WriteAllText(FilePath, "{broken");
        Assert.Equal("INVALID_SERVICE_RECENT_MODELS", (await Assert.ThrowsAsync<AppException>(() => store.DeleteAsync("account", "GlobalCloud", id, default))).Code);
        Assert.Equal("{broken", File.ReadAllText(FilePath)); File.WriteAllBytes(FilePath, before);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync("account", "GlobalCloud", id, default, () => throw new OperationCanceledException()));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        using var cancelAfterCommit = new CancellationTokenSource();
        Assert.True(await store.DeleteAsync("account", "GlobalCloud", id, cancelAfterCommit.Token, cancelAfterCommit.Cancel));
        Assert.Empty(await store.LoadAsync("account", "GlobalCloud", default)); Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>预取消、文件锁冲突与损坏均保留原文件, 禁止损坏文件被连接历史覆盖.</summary>
    [Fact]
    public async Task CancellationLockConflictAndCorruptionPreserveHistory()
    {
        var store = new LocalPowerBiRecentModelStore(root); await store.SaveAsync("account", "GlobalCloud", "workspace", "model", default);
        var before = File.ReadAllBytes(FilePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("account", "GlobalCloud", "workspace", "second", new(true)));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        using (UserDataStore.Lock(FilePath + ".lock"))
            Assert.Equal("USER_DATA_BUSY", (await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("account", "GlobalCloud", "workspace", "second", default))).Code);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        File.WriteAllText(FilePath, "{broken");
        Assert.Equal("INVALID_SERVICE_RECENT_MODELS", (await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("account", "GlobalCloud", "workspace", "second", default))).Code);
        var cloud = new Cloud(); var service = new PowerBiServiceConnection(cloud, recentModelStore: store);
        var recent = await service.RecentModelsAsync(default); Assert.Empty(recent.Items); Assert.NotNull(recent.StorageWarning);
        await service.RecordConnectionAsync(service.CaptureConnection("handle-one"), default);
        Assert.NotNull((await service.RecentModelsAsync(default)).StorageWarning); Assert.Equal("{broken", File.ReadAllText(FilePath));
    }

    /// <summary>拒绝未知版本、重复字段、身份伪造及敏感字段, 不自动迁移或修复原文件.</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("identity")]
    [InlineData("token")]
    public async Task RejectsInvalidDocumentsWithoutOverwriting(string mutation)
    {
        var store = new LocalPowerBiRecentModelStore(root); await store.SaveAsync("account", "GlobalCloud", "workspace", "model", default);
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        if (mutation == "version") json["schemaVersion"] = 99;
        else if (mutation == "identity") json["items"]![0]!["id"] = "forged";
        else if (mutation == "token") json["items"]![0]!["accessToken"] = "never-accepted";
        var content = mutation == "duplicate" ? "{\"schemaVersion\":1,\"schemaVersion\":1,\"items\":[]}" : json.ToJsonString();
        File.WriteAllText(FilePath, content);
        Assert.Equal("INVALID_SERVICE_RECENT_MODELS", (await Assert.ThrowsAsync<AppException>(() => store.LoadAsync("account", "GlobalCloud", default))).Code);
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    /// <summary>最近模型读取取消和目录权限失败明确失败, 不能返回上次缓存或泄漏后台认证身份.</summary>
    [Fact]
    public async Task ReadCancellationAndPermissionErrorsDoNotFallBackToCachedHistory()
    {
        var cloud = new Cloud(); var service = new PowerBiServiceConnection(cloud, recentModelStore: new LocalPowerBiRecentModelStore(root));
        await service.ModelsAsync(default); await service.RecordConnectionAsync(service.CaptureConnection("handle-one"), default);
        Assert.Single((await service.RecentModelsAsync(default)).Items);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecentModelsAsync(new(true)));
        cloud.CatalogError = new AppException("POWER_BI_ACCESS_DENIED", "请检查权限", 3);
        Assert.Equal("POWER_BI_ACCESS_DENIED", (await Assert.ThrowsAsync<AppException>(() => service.RecentModelsAsync(default))).Code);
        Assert.DoesNotContain("real-account", JsonSerializer.Serialize(service.CaptureConnection("handle-one")));
    }

    /// <summary>安全业务日志不得包含认证账号、目录标识或会话句柄.</summary>
    [Fact]
    public async Task LogsDoNotExposeIdentityOrModelMetadata()
    {
        using var runtime = new LoggingRuntime(root, "test"); new LoggingService(runtime).Save("Debug", default);
        var store = new LocalPowerBiRecentModelStore(root, runtime.Factory.CreateLogger<LocalPowerBiRecentModelStore>());
        await store.SaveAsync("private-account", "GlobalCloud", "private-workspace", "private-model", default);
        _ = await store.LoadAsync("private-account", "GlobalCloud", default);
        var id = Assert.Single(await store.LoadAsync("private-account", "GlobalCloud", default)).Id;
        Assert.True(await store.DeleteAsync("private-account", "GlobalCloud", id, default));
        runtime.Dispose();
        var output = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.clef").SelectMany(File.ReadAllLines));
        Assert.DoesNotContain("private-account", output); Assert.DoesNotContain("private-workspace", output); Assert.DoesNotContain("private-model", output);
        Assert.DoesNotContain(id, output);
    }

    /// <summary>仅清理本测试创建的独立临时目录.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class GatedStore : IPowerBiRecentModelStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LoadedAccountId { get; private set; }
        public string? LoadedEnvironment { get; private set; }
        /// <summary>锁定真实身份捕获后的读取, 精确控制另一次账号和目录变更.</summary>
        public async Task<IReadOnlyList<StoredPowerBiServiceModel>> LoadAsync(string accountId, string environmentName, CancellationToken token)
        {
            LoadedAccountId = accountId; LoadedEnvironment = environmentName;
            Entered.SetResult(); await Release.Task.WaitAsync(token);
            return [new("saved", "workspace-one", "model-one", DateTimeOffset.UtcNow)];
        }
        /// <summary>此竞争测试不写入真实文件.</summary>
        public Task SaveAsync(string accountId, string environmentName, string workspaceId, string modelId, CancellationToken token) => Task.CompletedTask;
        /// <summary>读取竞争测试不调用删除.</summary>
        public Task<bool> DeleteAsync(string accountId, string environmentName, string id, CancellationToken token, Action? committing = null) => throw new NotSupportedException();
    }

    /// <summary>删除提交前阻塞, 验证独立账号变化不能绕过最后的后台身份复核.</summary>
    private sealed class GatedDeleteStore(IPowerBiRecentModelStore inner) : IPowerBiRecentModelStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>读取交由实际隔离存储处理.</summary>
        public Task<IReadOnlyList<StoredPowerBiServiceModel>> LoadAsync(string accountId, string environmentName, CancellationToken token) => inner.LoadAsync(accountId, environmentName, token);
        /// <summary>记录交由实际隔离存储处理.</summary>
        public Task SaveAsync(string accountId, string environmentName, string workspaceId, string modelId, CancellationToken token) => inner.SaveAsync(accountId, environmentName, workspaceId, modelId, token);
        /// <summary>释放后才进入实际文件锁和提交复核, 保持原账号分区参数.</summary>
        public async Task<bool> DeleteAsync(string accountId, string environmentName, string id, CancellationToken token, Action? committing = null)
        {
            Entered.SetResult(); await Release.Task.WaitAsync(token);
            return await inner.DeleteAsync(accountId, environmentName, id, token, committing);
        }
    }

    private sealed class Cloud : IPowerBiServiceClient
    {
        public PowerBiAccount? Account { get; set; } = new("test@example.com");
        public string? AccountId { get; set; } = "real-account";
        public IReadOnlyList<PowerBiServiceModel> Models { get; set; } = [new("handle-one", "Sales", "Team", null, "workspace-one", "model-one")];
        public int CatalogReads { get; private set; }
        public AppException? CatalogError { get; set; }
        /// <summary>返回固定云, 不访问微软服务.</summary>
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiEnvironment>>([new("GlobalCloud", "全球版")]);
        /// <summary>本组测试只使用已登录假会话.</summary>
        public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null) => throw new NotSupportedException();
        /// <summary>每次读取计数且可控制权限错误, 不从本机历史生成目录权限.</summary>
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CatalogReads++;
            if (CatalogError is not null) throw CatalogError;
            return Task.FromResult(Models);
        }
        /// <summary>本组测试不获取真实令牌或连接目标.</summary>
        public Task<PowerBiServiceTarget> ResolveAsync(string modelId, CancellationToken token) => throw new NotSupportedException();
        /// <summary>清理假会话, 不影响系统账号.</summary>
        public Task SignOutAsync(CancellationToken token, Action? committing = null) { token.ThrowIfCancellationRequested(); committing?.Invoke(); Account = null; AccountId = null; return Task.CompletedTask; }
    }
}
