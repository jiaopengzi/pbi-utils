// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PowerBiLoginTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证单一登录编排、最近记录跨实例持久化、取消、损坏及日志安全.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Connections.PowerBiService;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>实际文件存储使用独立临时目录, 假认证不访问微软服务.</summary>
public sealed class PowerBiLoginTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pbi-service-login-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "service-connections.json");

    /// <summary>单个全球或中国环境只需一次提交; 多云先选择, 选择阶段不认证或保存记录.</summary>
    [Theory]
    [InlineData("GlobalCloud", false)]
    [InlineData("ChinaCloud", false)]
    [InlineData("ChinaCloud", true)]
    public async Task SingleLoginUseCaseDiscoversAndAuthenticates(string name, bool multiple)
    {
        var client = new Cloud(name, multiple); var store = new LocalPowerBiLoginStore(root); var service = new PowerBiServiceConnection(client, loginStore: store);
        List<string> phases = [];
        var result = await service.SignInAsync(new("test@example.com", "", true), default, phase: phases.Add);
        if (multiple) Assert.Equal(["discoveringEnvironment"], phases);
        else Assert.Equal(["discoveringEnvironment", "authenticating"], phases);
        if (multiple)
        {
            Assert.Null(result.Account); Assert.Equal(2, result.Environments.Count); Assert.Equal(0, client.Logins); Assert.False(File.Exists(FilePath));
            result = await service.SignInAsync(new("test@example.com", name, true), default);
        }
        Assert.Equal(name, result.Account!.EnvironmentName); Assert.Empty(result.Environments); Assert.Equal(1, client.Logins);
        var saved = Assert.Single(await new LocalPowerBiLoginStore(root).LoadAsync(default));
        Assert.Equal("test@example.com", saved.Email); Assert.Equal(name, saved.EnvironmentName);
    }

    /// <summary>已保存邮箱自动沿用最近云, 后台仍验证可信配置, 即使发现多个环境也不重复要求选择.</summary>
    [Theory]
    [InlineData("GlobalCloud")]
    [InlineData("ChinaCloud")]
    public async Task RememberedEmailUsesSavedCloudWithoutRepeatingChoice(string name)
    {
        var store = new LocalPowerBiLoginStore(root); await store.SaveAsync("test@example.com", name, default);
        var client = new Cloud(name, true); var service = new PowerBiServiceConnection(client, loginStore: store);
        List<string> phases = [];
        var result = await service.SignInAsync(new("TEST@example.com", "", true), default, phase: phases.Add);
        Assert.Equal(name, result.Account!.EnvironmentName); Assert.Empty(result.Environments);
        Assert.Equal(["authenticating"], phases); Assert.Equal(1, client.Logins);
    }

    /// <summary>保存云从可信发现消失时拒绝登录, 不静默切换到另一云或覆盖原记录.</summary>
    [Fact]
    public async Task RememberedCloudDisappearanceDoesNotSwitchCloud()
    {
        var store = new LocalPowerBiLoginStore(root); await store.SaveAsync("test@example.com", "ChinaCloud", default);
        var before = File.ReadAllBytes(FilePath); var cloud = new Cloud("GlobalCloud", false);
        var service = new PowerBiServiceConnection(cloud, loginStore: store);
        Assert.Equal("INVALID_POWER_BI_ENVIRONMENT", (await Assert.ThrowsAsync<AppException>(() => service.SignInAsync(new("test@example.com", "", true), default))).Code);
        Assert.Equal(before, File.ReadAllBytes(FilePath)); Assert.Equal(0, cloud.Logins);
    }

    /// <summary>已保存中国选择不能绕过重新发现; 环境改变时明确拒绝且不更新原记录.</summary>
    [Fact]
    public async Task SavedCloudIsNotTrustedWithoutCurrentDiscovery()
    {
        var client = new Cloud("GlobalCloud", false); var store = new LocalPowerBiLoginStore(root);
        await store.SaveAsync("test@example.com", "ChinaCloud", default); var before = File.ReadAllBytes(FilePath);
        var service = new PowerBiServiceConnection(client, loginStore: store);
        Assert.Equal("INVALID_POWER_BI_ENVIRONMENT", (await Assert.ThrowsAsync<AppException>(() => service.SignInAsync(new("test@example.com", "ChinaCloud", true), default))).Code);
        Assert.Equal(0, client.Logins); Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    /// <summary>实际提交后认证失败或取消保留尝试, 单纯取消发现不保存.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledAuthenticationRemembersOnlySubmittedAttempt(bool cancelled)
    {
        var client = new Cloud("ChinaCloud", false) { AuthenticationError = cancelled ? new OperationCanceledException() : new AppException("TEST_AUTH_FAILED", "测试认证失败") };
        var service = new PowerBiServiceConnection(client, loginStore: new LocalPowerBiLoginStore(root));
        await Assert.ThrowsAnyAsync<Exception>(() => service.SignInAsync(new("test@example.com", "", true), default));
        Assert.Single((await service.StatusAsync(default)).RecentLogins); Assert.Null(client.Account);
        var another = Path.Combine(root, "cancelled-discovery");
        var beforeDiscovery = new PowerBiServiceConnection(new Cloud("ChinaCloud", false), loginStore: new LocalPowerBiLoginStore(another));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => beforeDiscovery.SignInAsync(new("test@example.com", "", true), new(true)));
        Assert.False(Directory.Exists(another));
    }

    /// <summary>邮箱按大小写去重, 跨云独立, 多实例合并且仅保留最近20条.</summary>
    [Fact]
    public async Task MultipleInstancesMergeAndBoundHistory()
    {
        var first = new LocalPowerBiLoginStore(root); var second = new LocalPowerBiLoginStore(root);
        await first.SaveAsync("TEST@example.com", "GlobalCloud", default); await second.SaveAsync("test@example.com", "GlobalCloud", default);
        Assert.Single(await first.LoadAsync(default));
        await first.SaveAsync("test@example.com", "ChinaCloud", default); Assert.Equal(2, (await second.LoadAsync(default)).Count);
        for (var index = 0; index < 22; index++) await (index % 2 == 0 ? first : second).SaveAsync($"test{index}@example.com", "ChinaCloud", default);
        var rows = await second.LoadAsync(default); Assert.Equal(20, rows.Count); Assert.Equal("test21@example.com", rows[0].Email);
        await first.DeleteAsync(rows[0].Id, default); Assert.Equal(19, (await second.LoadAsync(default)).Count);
        Assert.Equal("SERVICE_CONNECTION_NOT_FOUND", (await Assert.ThrowsAsync<AppException>(() => second.DeleteAsync(rows[0].Id, default))).Code);
    }

    /// <summary>取消、锁冲突及损坏不覆盖原文件; 取消记住登录后仍可认证.</summary>
    [Fact]
    public async Task CancellationConflictAndCorruptionPreserveHistory()
    {
        var store = new LocalPowerBiLoginStore(root); await store.SaveAsync("test@example.com", "GlobalCloud", default);
        var before = File.ReadAllBytes(FilePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("other@example.com", "ChinaCloud", new(true)));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        using (UserDataStore.Lock(FilePath + ".lock"))
            Assert.Equal("USER_DATA_BUSY", (await Assert.ThrowsAsync<AppException>(() => store.SaveAsync("other@example.com", "ChinaCloud", default))).Code);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        File.WriteAllText(FilePath, "{broken");
        var service = new PowerBiServiceConnection(new Cloud("ChinaCloud", false), loginStore: store);
        Assert.NotNull((await service.StatusAsync(default)).StorageWarning);
        Assert.Equal("INVALID_SERVICE_CONNECTIONS", (await Assert.ThrowsAsync<AppException>(() => service.SignInAsync(new("test@example.com", "", true), default))).Code);
        Assert.NotNull((await service.SignInAsync(new("test@example.com", "", false), default)).Account);
        Assert.Equal("{broken", File.ReadAllText(FilePath));
    }

    /// <summary>版本、重复字段、邮箱、云及令牌注入均拒绝, 不修复原文件.</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("email")]
    [InlineData("cloud")]
    [InlineData("token")]
    public async Task RejectsCorruptAndSensitiveStoredFields(string mutation)
    {
        var store = new LocalPowerBiLoginStore(root); await store.SaveAsync("test@example.com", "ChinaCloud", default);
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        if (mutation == "version") json["schemaVersion"] = 2;
        else if (mutation == "email") json["items"]![0]!["email"] = "invalid";
        else if (mutation == "cloud") json["items"]![0]!["environmentName"] = "https://evil.test";
        else if (mutation == "token") json["items"]![0]!["accessToken"] = "never-accepted";
        var content = mutation == "duplicate" ? "{\"schemaVersion\":1,\"schemaVersion\":1,\"items\":[]}" : json.ToJsonString();
        File.WriteAllText(FilePath, content);
        Assert.Equal("INVALID_SERVICE_CONNECTIONS", (await Assert.ThrowsAsync<AppException>(() => store.LoadAsync(default))).Code);
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    /// <summary>真实安全日志不记录邮箱, 存储文件也没有认证令牌或密码字段.</summary>
    [Fact]
    public async Task LogsAndFilesContainOnlyAuthorizedMetadata()
    {
        using var runtime = new LoggingRuntime(root, "test"); new LoggingService(runtime).Save("Debug", default);
        var store = new LocalPowerBiLoginStore(root, runtime.Factory.CreateLogger<LocalPowerBiLoginStore>());
        await store.SaveAsync("private-email@example.com", "ChinaCloud", default); _ = await store.LoadAsync(default);
        runtime.Dispose();
        var log = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.clef").SelectMany(File.ReadAllLines));
        Assert.DoesNotContain("private-email", log);
        var persisted = File.ReadAllText(FilePath); Assert.DoesNotContain("accessToken", persisted); Assert.DoesNotContain("password", persisted);
    }

    /// <summary>仅清理本测试独立创建的临时目录, 不接触实际用户数据.</summary>
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Cloud(string name, bool multiple) : IPowerBiServiceClient
    {
        public PowerBiAccount? Account { get; private set; }
        public int Logins { get; private set; }
        public Exception? AuthenticationError { get; init; }
        /// <summary>固定环境代替外部发现, 先处理预取消.</summary>
        public Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PowerBiEnvironment>>(multiple ? [new("GlobalCloud", "全球版"), new("ChinaCloud", "世纪互联 (中国)")] : [new(name, PowerBiEnvironment.Label(name))]);
        }
        /// <summary>假认证可失败或取消, 不读取真实账号凭据.</summary>
        public Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null)
        {
            token.ThrowIfCancellationRequested(); Logins++;
            if (AuthenticationError is not null) throw AuthenticationError;
            committing?.Invoke(); Account = new(email, environmentName, PowerBiEnvironment.Label(environmentName)); return Task.FromResult(Account);
        }
        /// <summary>本测试不读取服务模型.</summary>
        public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<PowerBiServiceModel>>([]);
        /// <summary>禁止假认证测试请求真实模型目标.</summary>
        public Task<PowerBiServiceTarget> ResolveAsync(string modelId, CancellationToken token) => throw new InvalidOperationException();
        /// <summary>只清理假账号.</summary>
        public Task SignOutAsync(CancellationToken token, Action? committing = null) { token.ThrowIfCancellationRequested(); committing?.Invoke(); Account = null; return Task.CompletedTask; }
    }
}
