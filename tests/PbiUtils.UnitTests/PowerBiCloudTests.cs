// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PowerBiCloudTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用内存 HTTP 和认证会话验证服务发现、模型目录、权限、取消与凭据边界.

using PbiUtils.Infrastructure.Connections.PowerBiService;
using PbiUtils.Application.Connections;
using System.Net;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>这些测试不登录真实账号, 不继承真实服务连接结论.</summary>
public sealed class PowerBiCloudTests
{
    /// <summary>服务明确的个人工作区缺少工作区 ID 时使用后台标记, 同账号刷新及重登可恢复最近连接.</summary>
    [Fact]
    public async Task PersonalWorkspaceWithoutObjectIdKeepsStableDirectoryIdentity()
    {
        using var handler = new Handler { CatalogPayload = "[{\"workspaceType\":3,\"model\":{\"displayName\":\"Personal\",\"dbName\":\"personal-db\",\"vsName\":\"vs\"}}]" };
        using var http = new HttpClient(handler); using var client = new PowerBiCloudClient(http, new SignIn());
        await LoginAsync(client);
        var first = Assert.Single(await client.ModelsAsync(default)); Assert.Equal("@personal", first.WorkspaceId);
        Assert.Equal(first.Id, Assert.Single(await client.ModelsAsync(default)).Id);
        var target = await client.ResolveAsync(first.Id, default); Assert.Equal("pbiazure", new Uri(target.Server).Scheme);
        Assert.Equal("test-account", client.AccountId);
        await client.SignOutAsync(default); await LoginAsync(client);
        var second = Assert.Single(await client.ModelsAsync(default));
        Assert.NotEqual(first.Id, second.Id); Assert.Equal(first.WorkspaceId, second.WorkspaceId); Assert.Equal(first.ModelId, second.ModelId);
    }
    /// <summary>复制 Bravo 的容量选择边界, Premium TOM 与共享/个人架构连接使用不同地址和 Catalog.</summary>
    [Theory]
    [InlineData("Premium", false, 1, true)]
    [InlineData("Shared", false, 1, false)]
    [InlineData("Premium", true, 1, false)]
    [InlineData("Premium", false, 3, false)]
    [InlineData("", false, 1, false)]
    public async Task SelectsXmlaOnlyForSupportedCapacity(string sku, bool shared, int workspaceType, bool xmla)
    {
        using var handler = new Handler
        {
            WorkspacePayload = JsonSerializer.Serialize(new[] { new { id = "workspace", name = "团队 / 检查", capacitySku = sku, isSharedOnPremium = shared } }),
            CatalogPayload = JsonSerializer.Serialize(new[] { new { workspaceObjectId = "workspace", workspaceType, model = new { displayName = "Sales", dbName = "db1", vsName = "vs" } } })
        };
        using var http = new HttpClient(handler); using var client = new PowerBiCloudClient(http, new SignIn());
        await LoginAsync(client); var id = (await client.ModelsAsync(default)).Single().Id;
        var target = await client.ResolveAsync(id, default);
        Assert.Equal(xmla ? "powerbi" : "pbiazure", new Uri(target.Server).Scheme);
        Assert.Equal(xmla ? "Sales" : "vs-db1", target.Database);
        Assert.Equal("db1", target.DatabaseId);
        Assert.Equal("Sales", target.ModelName);
        if (xmla) { Assert.Equal("wabi-test.analysis.windows.net", new Uri(target.Server).Host); Assert.Contains("%2F", target.Server); }
    }
    /// <summary>先发现邮箱环境再显式登录, 不绕过新的选择契约.</summary>
    private static async Task<PowerBiAccount> LoginAsync(PowerBiCloudClient client)
    {
        await client.EnvironmentsAsync("test@example.com", default);
        return await client.SignInAsync("test@example.com", "GlobalCloud", default);
    }
    private const string Discovery = """
        {"environments":[{"cloudName":"GlobalCloud","clients":[{"name":"powerbi-desktop","appId":"00000000-0000-0000-0000-000000000001"}],"services":[{"name":"aad","endpoint":"https://login.microsoftonline.com/common"},{"name":"powerbi-backend","endpoint":"https://api.powerbi.com","resourceId":"https://analysis.windows.net/powerbi/api"}]}]}
        """;
    private const string Catalog = """
        [{"workspaceObjectId":"workspace","workspaceName":"团队","model":{"displayName":"Sales","dbName":"db1","vsName":"vs"}},
        {"workspaceObjectId":"workspace","model":{"displayName":"Sales","dbName":"db2","vsName":"vs"}},
        {"workspaceObjectId":"workspace","model":{"displayName":"External","dbName":"db3","vsName":"vs","onPremModelConnectionString":"Password=do-not-return"}}]
        """;
    private static readonly string ChinaDiscovery = Discovery.Replace("GlobalCloud", "ChinaCloud")
        .Replace("login.microsoftonline.com", "login.chinacloudapi.cn").Replace("api.powerbi.com", "api.powerbi.cn")
        .Replace("analysis.windows.net", "analysis.chinacloudapi.cn");
    private static readonly string BothClouds = CombineDiscovery();
    /// <summary>合并内存中的公开云发现样例并释放解析资源.</summary>
    private static string CombineDiscovery()
    {
        using var global = JsonDocument.Parse(Discovery); using var china = JsonDocument.Parse(ChinaDiscovery);
        return "{\"environments\":[" + global.RootElement.GetProperty("environments")[0].GetRawText()
            + "," + china.RootElement.GetProperty("environments")[0].GetRawText() + "]}";
    }

    /// <summary>按服务返回单云或双云, 邮箱安全编码, 登录提示及所有令牌请求绑定选中云.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task DiscoversAndRoutesSelectedCloud(bool china, bool both, bool xmla)
    {
        using var handler = new Handler
        {
            DiscoveryPayload = both ? BothClouds : china ? ChinaDiscovery : Discovery,
            WorkspacePayload = "[{\"id\":\"workspace\",\"name\":\"团队\",\"capacitySku\":\"" + (xmla ? "Premium" : "Shared") + "\"}]"
        };
        using var http = new HttpClient(handler); var auth = new SignIn(); using var client = new PowerBiCloudClient(http, auth);
        var email = "test+cloud@example.com";
        var environments = await client.EnvironmentsAsync(email, default);
        Assert.Equal(both ? 2 : 1, environments.Count);
        Assert.All(handler.DiscoveryUris, uri => Assert.Equal("?user=" + Uri.EscapeDataString(email), uri.Query));
        var name = china ? "ChinaCloud" : "GlobalCloud";
        var account = await client.SignInAsync(email, name, default);
        Assert.Equal(name, account.EnvironmentName);
        Assert.Equal(email, auth.Email);
        Assert.Equal(china ? "https://login.chinacloudapi.cn/common" : "https://login.microsoftonline.com/common", auth.Configuration!.Authority);
        Assert.Equal(china ? "https://analysis.chinacloudapi.cn/powerbi/api" : "https://analysis.windows.net/powerbi/api", auth.Configuration.Resource);
        var target = await client.ResolveAsync((await client.ModelsAsync(default)).First(m => m.Name == "Sales").Id, default);
        var api = china ? "api.powerbi.cn" : "api.powerbi.com";
        var cluster = china ? "wabi-test.analysis.chinacloudapi.cn" : "wabi-test.analysis.windows.net";
        Assert.Equal(xmla ? cluster : api, new Uri(target.Server).Host);
        Assert.Equal(xmla ? "powerbi" : "pbiazure", new Uri(target.Server).Scheme);
        Assert.Equal(name + ":test-account", target.AccountId);
        Assert.Contains(auth.Configuration.Resource, target.IdentityProvider);
        Assert.All(handler.AuthenticatedHosts, host => Assert.True(host == api || host == cluster));
        Assert.DoesNotContain("test-access-token", JsonSerializer.Serialize(account));
    }

    /// <summary>选中云以外的集群在发送令牌前被拒绝, 同时拒绝世纪互联的伪造域名.</summary>
    [Theory]
    [InlineData("GlobalCloud", "https://wabi-test.analysis.chinacloudapi.cn/")]
    [InlineData("ChinaCloud", "https://wabi-test.analysis.windows.net/")]
    [InlineData("ChinaCloud", "https://wabi.analysis.chinacloudapi.cn.evil.test/")]
    [InlineData("ChinaCloud", "https://user@wabi.analysis.chinacloudapi.cn/")]
    [InlineData("ChinaCloud", "https://wabi.analysis.chinacloudapi.cn:8443/")]
    public async Task RejectsCrossCloudOrSpoofedClusterBeforeTokenLeavesCloud(string cloud, string cluster)
    {
        using var handler = new Handler { DiscoveryPayload = BothClouds, Cluster = cluster };
        using var http = new HttpClient(handler); using var client = new PowerBiCloudClient(http, new SignIn());
        await client.EnvironmentsAsync("test@example.com", default);
        await client.SignInAsync("test@example.com", cloud, default);
        Assert.Equal("POWER_BI_CLUSTER_INVALID", (await Assert.ThrowsAsync<AppException>(() => client.ModelsAsync(default))).Code);
        Assert.Single(handler.AuthenticatedHosts);
    }

    /// <summary>空值、显示名、多地址及控制字符在网络请求前拒绝.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("Name <test@example.com>")]
    [InlineData("test@example.com;other@example.com")]
    [InlineData("test@exam\nple.com")]
    public async Task InvalidEmailNeverRequestsDiscovery(string email)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); using var client = new PowerBiCloudClient(http, new SignIn());
        Assert.Equal("INVALID_POWER_BI_EMAIL", (await Assert.ThrowsAsync<AppException>(() => client.EnvironmentsAsync(email, default))).Code);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>改变邮箱或选择未返回的云不能打开认证, 失败不覆盖当前账号及目录.</summary>
    [Fact]
    public async Task DiscoverySelectionIsBoundToEmailAndNeverChangesExistingSession()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); var auth = new SignIn(); using var client = new PowerBiCloudClient(http, auth);
        await LoginAsync(client); var id = (await client.ModelsAsync(default)).First(m => m.Name == "Sales").Id;
        Assert.Equal("INVALID_POWER_BI_ENVIRONMENT", (await Assert.ThrowsAsync<AppException>(() => client.SignInAsync("other@example.com", "GlobalCloud", default))).Code);
        Assert.Equal("INVALID_POWER_BI_ENVIRONMENT", (await Assert.ThrowsAsync<AppException>(() => client.SignInAsync("test@example.com", "ChinaCloud", default))).Code);
        handler.DiscoveryPayload = ChinaDiscovery;
        await client.EnvironmentsAsync("test@example.com", default);
        Assert.Equal("GlobalCloud", client.Account!.EnvironmentName);
        Assert.Equal("pbiazure://api.powerbi.com", (await client.ResolveAsync(id, default)).Server);
        auth.Cancel = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.SignInAsync("test@example.com", "ChinaCloud", default));
        Assert.Equal("GlobalCloud", client.Account!.EnvironmentName);
        Assert.NotNull(await client.ResolveAsync(id, default));
        auth.Cancel = false;
        await client.SignInAsync("test@example.com", "ChinaCloud", default);
        Assert.Equal("INVALID_SERVICE_MODEL", (await Assert.ThrowsAsync<AppException>(() => client.ResolveAsync(id, default))).Code);
    }

    /// <summary>世纪互联配置不允许混入全球资源、接口或伪造认证地址; 内部云也不会暴露.</summary>
    [Theory]
    [InlineData("api.powerbi.cn", "api.powerbi.com")]
    [InlineData("analysis.chinacloudapi.cn", "analysis.windows.net")]
    [InlineData("login.chinacloudapi.cn", "login.evil.test")]
    public async Task RejectsMixedCloudDiscovery(string original, string replacement)
    {
        using var handler = new Handler { DiscoveryPayload = ChinaDiscovery.Replace(original, replacement) };
        using var http = new HttpClient(handler); var auth = new SignIn(); using var client = new PowerBiCloudClient(http, auth);
        Assert.Equal("POWER_BI_DISCOVERY_INVALID", (await Assert.ThrowsAsync<AppException>(() => client.EnvironmentsAsync("test@example.com", default))).Code);
        Assert.Equal(0, auth.Calls);
    }

    /// <summary>新发现接口 404 才回退; 重名模型独立选择, 刷新稳定标识且令牌不出现在展示对象中.</summary>
    [Fact]
    public async Task DiscoveryFallbackAndCatalogProtectSecretsAndIdentity()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new SignIn(); using var client = new PowerBiCloudClient(http, auth);
        Assert.Equal("POWER_BI_SIGN_IN_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => new PowerBiServiceConnection(client).ModelsAsync(default))).Code);
        await LoginAsync(client);
        Assert.Equal(2, handler.Discoveries);
        var models = await client.ModelsAsync(default);
        Assert.Equal(3, models.Count);
        Assert.Equal(2, models.Count(m => m.Name == "Sales"));
        Assert.Equal(3, models.Select(m => m.Id).Distinct().Count());
        Assert.All(models, m => Assert.Equal("团队", m.Workspace));
        var id = models.First(m => m.Name == "Sales").Id;
        var target = await client.ResolveAsync(id, default);
        Assert.Equal("pbiazure://api.powerbi.com", target.Server);
        Assert.DoesNotContain("test-access-token", JsonSerializer.Serialize(target));
        Assert.DoesNotContain("test-access-token", target.ToString());
        Assert.DoesNotContain("do-not-return", JsonSerializer.Serialize(models));
        Assert.Equal("SERVICE_MODEL_UNSUPPORTED", (await Assert.ThrowsAsync<AppException>(() => client.ResolveAsync(models.Single(m => m.Name == "External").Id, default))).Code);
        Assert.Contains((await client.ModelsAsync(default)), m => m.Id == id);
        Assert.True(auth.Session.TokenReads >= 3);
        Assert.All(handler.AuthenticatedHosts, host => Assert.True(host == "api.powerbi.com" || host == "wabi-test.analysis.windows.net"));
        await client.SignOutAsync(default);
        Assert.True(auth.Session.Cleared);
        Assert.Null(client.Account);
        Assert.Equal("POWER_BI_SIGN_IN_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => client.ResolveAsync(id, default))).Code);
    }
    /// <summary>部分请求失败不会发布部分目录; 账号切换后上一账号的句柄不能使用.</summary>
    [Fact]
    public async Task FailedRefreshPreservesCatalogAndNewAccountInvalidatesHandles()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); var auth = new SignIn();
        using var client = new PowerBiCloudClient(http, auth);
        await LoginAsync(client); var id = (await client.ModelsAsync(default)).First(m => m.Name == "Sales").Id;
        handler.Denied = true;
        var error = await Assert.ThrowsAsync<AppException>(() => client.ModelsAsync(default));
        Assert.Equal("POWER_BI_ACCESS_DENIED", error.Code); Assert.DoesNotContain("test-access-token", error.Message);
        Assert.NotNull(await client.ResolveAsync(id, default));
        handler.Denied = false;
        await LoginAsync(client);
        Assert.Equal("INVALID_SERVICE_MODEL", (await Assert.ThrowsAsync<AppException>(() => client.ResolveAsync(id, default))).Code);
    }
    /// <summary>认证取消不得替换已有账号或清空原模型目录.</summary>
    [Fact]
    public async Task CancelledSignInPreservesExistingSession()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); var auth = new SignIn();
        using var client = new PowerBiCloudClient(http, auth);
        await LoginAsync(client); var id = (await client.ModelsAsync(default)).First(m => m.Name == "Sales").Id;
        auth.Cancel = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => LoginAsync(client));
        Assert.NotNull(client.Account); Assert.NotNull(await client.ResolveAsync(id, default));
    }
    /// <summary>缓存提交被宿主拒绝时清理候选认证, 不触碰原会话.</summary>
    [Fact]
    public async Task RejectedCommitDiscardsCandidateAndKeepsOldAccount()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); var auth = new SignIn();
        using var client = new PowerBiCloudClient(http, auth);
        await LoginAsync(client); var old = auth.Session;
        var id = (await client.ModelsAsync(default)).First(m => m.Name == "Sales").Id;
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.SignInAsync("test@example.com", "GlobalCloud", default, () => throw new OperationCanceledException()));
        Assert.True(auth.Session.Cleared); Assert.False(old.Cleared);
        Assert.NotNull(await client.ResolveAsync(id, default));
    }
    /// <summary>集群验证在发送认证 HTTP 之前完成, 排除伪造域名、端口和用户信息.</summary>
    [Theory]
    [InlineData("https://analysis.windows.net.evil.test/")]
    [InlineData("https://wabi.analysis.windows.net.evil.test/")]
    [InlineData("http://wabi.analysis.windows.net/")]
    [InlineData("https://user@wabi.analysis.windows.net/")]
    [InlineData("https://wabi.analysis.windows.net:8443/")]
    [InlineData("https://wabi.analysis.windows.net/path")]
    [InlineData("https://wabi.analysis.windows.net/?next=evil")]
    public void ClusterRejectsUntrustedAddresses(string uri) => Assert.Equal("POWER_BI_CLUSTER_INVALID", Assert.Throws<AppException>(() => PowerBiCloudClient.TrustedCluster(uri)).Code);

    /// <summary>伪造的发现身份不得交给 MSAL; 未发布的模型不请求网络.</summary>
    [Fact]
    public async Task RejectsSpoofedConfigurationAndModel()
    {
        using var handler = new Handler { DiscoveryPayload = Discovery.Replace("login.microsoftonline.com", "login.evil.test") };
        using var http = new HttpClient(handler); var auth = new SignIn(); using var client = new PowerBiCloudClient(http, auth);
        Assert.Equal("POWER_BI_DISCOVERY_INVALID", (await Assert.ThrowsAsync<AppException>(() => LoginAsync(client))).Code);
        Assert.Equal(0, auth.Calls);
        handler.DiscoveryPayload = Discovery; await LoginAsync(client);
        var requests = handler.Requests;
        Assert.Equal("INVALID_SERVICE_MODEL", (await Assert.ThrowsAsync<AppException>(() => client.ResolveAsync("https://evil.test", default))).Code);
        Assert.Equal(requests, handler.Requests);
    }
    /// <summary>内存响应按实际参考协议组织, 权限失败包含敏感原文以验证脱敏.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public int Discoveries { get; private set; }
        public int Requests { get; private set; }
        public bool Denied { get; set; }
        public string DiscoveryPayload { get; set; } = Discovery;
        public string WorkspacePayload { get; set; } = "[{\"id\":\"workspace\",\"name\":\"团队\"}]";
        public string CatalogPayload { get; set; } = Catalog;
        public List<string> AuthenticatedHosts { get; } = [];
        public List<Uri> DiscoveryUris { get; } = [];
        public string? Cluster { get; set; }
        /// <summary>按路径返回固定响应, 不访问外网.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            var uri = request.RequestUri!;
            if (request.Headers.Authorization is not null) { AuthenticatedHosts.Add(uri.Host); Assert.Equal("test-access-token", request.Headers.Authorization.Parameter); }
            var status = HttpStatusCode.OK;
            string payload;
            if (uri.AbsolutePath.Contains("environments/discover", StringComparison.Ordinal))
            { Discoveries++; DiscoveryUris.Add(uri); payload = DiscoveryPayload; if (uri.AbsolutePath.Contains("v202408", StringComparison.Ordinal)) status = HttpStatusCode.NotFound; }
            else if (uri.AbsolutePath.Contains("GetOrInsertClusterUris", StringComparison.Ordinal)) payload = JsonSerializer.Serialize(new { FixedClusterUri = Cluster ?? (uri.Host == "api.powerbi.cn" ? "https://wabi-test.analysis.chinacloudapi.cn/" : "https://wabi-test.analysis.windows.net/") });
            else if (uri.AbsolutePath.EndsWith("workspaces", StringComparison.Ordinal)) payload = WorkspacePayload;
            else { payload = CatalogPayload; if (Denied) { status = HttpStatusCode.Forbidden; payload = "test-access-token"; } }
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(payload) });
        }
    }
    private sealed class SignIn : IPowerBiSignIn
    {
        public int Calls { get; private set; }
        public bool Cancel { get; set; }
        public Session Session { get; private set; } = new();
        public PowerBiLoginConfiguration? Configuration { get; private set; }
        public string? Email { get; private set; }
        /// <summary>替代真实 Microsoft 认证, 按场景抛出取消.</summary>
        public Task<IPowerBiTokenSession> SignInAsync(PowerBiLoginConfiguration config, string email, CancellationToken token)
        {
            Calls++;
            Configuration = config; Email = email;
            if (Cancel) throw new OperationCanceledException();
            Session = new(); return Task.FromResult<IPowerBiTokenSession>(Session);
        }
    }
    private sealed class Session : IPowerBiTokenSession
    {
        public PowerBiAccount Account { get; } = new("test@example.com");
        public string AccountId => "test-account";
        public int TokenReads { get; private set; }
        public bool Cleared { get; private set; }
        /// <summary>返回固定假令牌并记录续期读取次数.</summary>
        public Task<(string, DateTimeOffset)> TokenAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); TokenReads++; return Task.FromResult(("test-access-token", DateTimeOffset.UtcNow.AddHours(1))); }
        /// <summary>记录内存缓存是否清理.</summary>
        public Task ClearAsync() { Cleared = true; return Task.CompletedTask; }
    }
}
