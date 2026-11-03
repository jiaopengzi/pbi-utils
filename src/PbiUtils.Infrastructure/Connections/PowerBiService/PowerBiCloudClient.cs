// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Connections\PowerBiService\PowerBiCloudClient.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 参考 Bravo 服务发现和目录协议, 使用 MSAL 登录并保护后端模型句柄.

using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Connections.PowerBiService;

/// <summary>服务发现得到的公开认证配置, 不硬编码第三方应用身份.</summary>
public sealed record PowerBiLoginConfiguration(string ClientId, string Authority, string Resource, string EnvironmentName, string EnvironmentLabel, Uri Backend);
/// <summary>可替换的认证边界, 测试使用内存会话而不打开真实登录窗口.</summary>
public interface IPowerBiSignIn
{
    /// <summary>创建一个独立认证会话; 取消不替换原账号.</summary>
    Task<IPowerBiTokenSession> SignInAsync(PowerBiLoginConfiguration configuration, string email, CancellationToken token);
}
/// <summary>令牌会话只存在后端; 网页与 MCP 不接触此接口.</summary>
public interface IPowerBiTokenSession
{
    PowerBiAccount Account { get; }
    string AccountId { get; }
    /// <summary>使用内存缓存静默刷新; 需要交互时要求用户重新登录.</summary>
    Task<(string Value, DateTimeOffset ExpiresOn)> TokenAsync(CancellationToken token);
    /// <summary>退出清除本应用认证缓存.</summary>
    Task ClearAsync();
}
/// <summary>系统浏览器执行微软认证, 不在产品网页收集账号密码.</summary>
public sealed class MsalPowerBiSignIn : IPowerBiSignIn
{
    /// <summary>使用 Power BI 服务发现返回的公有客户端; 登录支持取消且最多等待两分钟.</summary>
    public async Task<IPowerBiTokenSession> SignInAsync(PowerBiLoginConfiguration configuration, string email, CancellationToken token)
    {
        var client = PublicClientApplicationBuilder.Create(configuration.ClientId)
            .WithAuthority(configuration.Authority).WithRedirectUri("http://localhost").Build();
        var scopes = new[] { configuration.Resource.TrimEnd('/') + "/.default" };
        try
        {
            using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(token);
            ceiling.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await client.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false)
                .WithLoginHint(email).WithPrompt(Prompt.SelectAccount).WithExtraQueryParameters(new Dictionary<string, (string, bool)> { ["msafed"] = ("0", false) }).ExecuteAsync(ceiling.Token);
            token.ThrowIfCancellationRequested();
            return new Session(client, scopes, result.Account);
        }
        catch (MsalClientException e) when (e.ErrorCode == "authentication_canceled")
        { throw new OperationCanceledException("已取消 Microsoft 登录", e, token); }
        catch (MsalException) { throw new AppException("POWER_BI_SIGN_IN_FAILED", "Microsoft 登录失败, 请检查组织账号、租户策略和网络后重试", 3); }
    }
    /// <summary>仅持有 MSAL 内存缓存, 不保存明文令牌或额外登录文件.</summary>
    private sealed class Session(IPublicClientApplication client, string[] scopes, IAccount account) : IPowerBiTokenSession
    {
        public PowerBiAccount Account { get; } = new(account.Username);
        public string AccountId => account.HomeAccountId.Identifier;
        /// <summary>静默续期不自动弹出登录; 权限变化需要显式退出并重新登录.</summary>
        public async Task<(string Value, DateTimeOffset ExpiresOn)> TokenAsync(CancellationToken token)
        {
            try
            {
                var result = await client.AcquireTokenSilent(scopes, account).ExecuteAsync(token);
                return (result.AccessToken, result.ExpiresOn);
            }
            catch (MsalUiRequiredException) { throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录已过期或组织要求重新验证, 请退出后重新登录", 3); }
            catch (MsalException) { throw new AppException("POWER_BI_TOKEN_FAILED", "无法刷新 Microsoft 登录, 请检查网络后重试", 3); }
        }
        /// <summary>只清除本客户端缓存, 不退出系统浏览器的其他应用.</summary>
        public Task ClearAsync() => client.RemoveAsync(account);
    }
}
/// <summary>按邮箱发现全球版和世纪互联, 认证及模型请求始终绑定同一受信任云环境.</summary>
public sealed class PowerBiCloudClient : IPowerBiServiceClient, IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly IPowerBiSignIn signIn;
    private IPowerBiTokenSession? session;
    private PowerBiLoginConfiguration? configuration;
    private string? discoveredEmail;
    private Dictionary<string, PowerBiLoginConfiguration> discovered = [];
    private Dictionary<string, Entry> models = [];
    private sealed record Entry(PowerBiServiceModel View, string Database, string Server, string DatabaseId);
    private static readonly Uri Api = new("https://api.powerbi.com/");
    public PowerBiAccount? Account => session is not null && configuration is not null
        ? session.Account with { EnvironmentName = configuration.EnvironmentName, EnvironmentLabel = configuration.EnvironmentLabel } : null;
    /// <summary>只在后台提供真实认证稳定身份, 网页邮箱不能决定最近模型所属账号.</summary>
    public string? AccountId => session?.AccountId;

    /// <summary>生产禁用重定向; 注入 HTTP 与认证适配器可验证故障和取消.</summary>
    public PowerBiCloudClient(HttpClient? http = null, IPowerBiSignIn? signIn = null, Microsoft.Extensions.Logging.ILogger<PowerBiCloudClient>? logger = null)
    {
        log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PowerBiCloudClient>.Instance;
        ownsHttp = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        this.signIn = signIn ?? new MsalPowerBiSignIn();
    }
    private readonly Microsoft.Extensions.Logging.ILogger log;
    /// <summary>按 Bravo 协议以编码后的邮箱查询环境; 新接口仅在 404 时回退, 不按邮箱后缀猜测云.</summary>
    public async Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token) => await BusinessLog.RunAsync<IReadOnlyList<PowerBiEnvironment>>(log, "PowerBiCloud.Discover", async () =>
    {
        email = PowerBiServiceConnection.ValidateEmail(email);
        var query = "?user=" + Uri.EscapeDataString(email);
        JsonDocument data;
        try { data = await RequestAsync(HttpMethod.Post, new(Api, "powerbi/globalservice/v202408/environments/discover" + query), null, token); }
        catch (AppException e) when (e.Code == "POWER_BI_API_NOT_FOUND")
        { data = await RequestAsync(HttpMethod.Post, new(Api, "powerbi/globalservice/v202003/environments/discover" + query), null, token); }
        using (data)
        {
            try
            {
                Dictionary<string, PowerBiLoginConfiguration> next = [];
                foreach (var environment in data.RootElement.GetProperty("environments").EnumerateArray())
                {
                    var name = Text(environment, "cloudName");
                    // 仅开放本轮支持的公开云; 内部及其他云不会进入登录列表.
                    if (!PowerBiEnvironment.IsSupported(name)) continue;
                    var config = ReadConfiguration(environment, name);
                    if (!next.TryAdd(name, config)) throw new InvalidOperationException();
                }
                if (next.Count == 0) throw new AppException("POWER_BI_ENVIRONMENT_UNAVAILABLE", "未发现支持的全球版或世纪互联环境, 请核对组织邮箱后重试", 3);
                token.ThrowIfCancellationRequested();
                discovered = next; discoveredEmail = email;
                return next.Values.Select(c => new PowerBiEnvironment(c.EnvironmentName, c.EnvironmentLabel)).ToArray();
            }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException)
            { throw new AppException("POWER_BI_DISCOVERY_INVALID", "Power BI 服务发现响应已变化, 请稍后重试", 3); }
        }
    });
    /// <summary>认证、资源和接口按云校验; 应用 ID 只取服务发现, 不信任网页地址或服务提供的任意域名.</summary>
    private static PowerBiLoginConfiguration ReadConfiguration(JsonElement environment, string name)
    {
        var desktop = environment.GetProperty("clients").EnumerateArray().Single(e => Text(e, "name") == "powerbi-desktop");
        var services = environment.GetProperty("services").EnumerateArray().ToArray();
        var authority = Text(services.Single(e => Text(e, "name") == "aad"), "endpoint").TrimEnd('/');
        var backend = services.Single(e => Text(e, "name") == "powerbi-backend");
        var resource = Text(backend, "resourceId").TrimEnd('/');
        var endpoint = Text(backend, "endpoint").TrimEnd('/');
        var clientId = Text(desktop, "appId");
        var policy = PowerBiCloudPolicy.Get(name);
        if (!Guid.TryParse(clientId, out _) || !policy.Authorities.Contains(authority, StringComparer.OrdinalIgnoreCase)
            || endpoint != policy.Backend || resource != policy.Resource)
            throw new AppException("POWER_BI_DISCOVERY_INVALID", "Power BI 返回的登录配置不受信任, 无法登录", 3);
        return new(clientId, authority, resource, name, policy.DisplayName, new(endpoint + "/"));
    }
    /// <summary>完整认证后替换当前会话; 失败和取消保留原登录与目录.</summary>
    public async Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null)
    {
        email = PowerBiServiceConnection.ValidateEmail(email);
        if (!string.Equals(email, discoveredEmail, StringComparison.OrdinalIgnoreCase) || !discovered.TryGetValue(environmentName, out var config))
            throw new AppException("INVALID_POWER_BI_ENVIRONMENT", "邮箱或登录环境已改变, 请重新登录", 3);
        var candidate = await signIn.SignInAsync(config, email, token);
        try { token.ThrowIfCancellationRequested(); committing?.Invoke(); }
        catch { await candidate.ClearAsync(); throw; }
        var old = session;
        session = candidate; configuration = config; models = [];
        if (old is not null) await old.ClearAsync();
        return Account!;
    }
    /// <summary>读取服务集群目录, 排除无独立可读模型的外部连接、推送和 Excel 条目.</summary>
    public async Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token)
    {
        var auth = session ?? throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "请先登录 Microsoft 工作或学校账号", 3);
        var config = configuration!;
        var (Value, ExpiresOn) = await auth.TokenAsync(token);
        using var clusterData = await RequestAsync(HttpMethod.Put, new(config.Backend, "spglobalservice/GetOrInsertClusterUrisByTenantlocation"), Value, token);
        if (clusterData.RootElement.ValueKind != JsonValueKind.Object
            || !(clusterData.RootElement.TryGetProperty("FixedClusterUri", out var clusterProperty) || clusterData.RootElement.TryGetProperty("fixedClusterUri", out clusterProperty))
            || clusterProperty.ValueKind != JsonValueKind.String)
            throw new AppException("POWER_BI_API_INVALID", "Power BI 未返回服务集群地址", 3);
        var cluster = TrustedCluster(clusterProperty.GetString() ?? "", config.EnvironmentName);
        using var workspaceData = await RequestAsync(HttpMethod.Get, new(cluster, "powerbi/databases/v201606/workspaces"), Value, token);
        using var modelData = await RequestAsync(HttpMethod.Get, new(cluster, "metadata/v201901/gallery/sharedDatasets"), Value, token);
        if (workspaceData.RootElement.ValueKind != JsonValueKind.Array || modelData.RootElement.ValueKind != JsonValueKind.Array)
            throw new AppException("POWER_BI_API_INVALID", "Power BI 模型目录响应已变化", 3);
        var workspaces = workspaceData.RootElement.EnumerateArray().Select(w => new { Id = Text(w, "id"), Name = Text(w, "name"), Premium = Text(w, "capacitySku") == "Premium" && !Flag(w, "isSharedOnPremium") }).ToArray();
        Dictionary<string, Entry> next = [];
        foreach (var item in modelData.RootElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (!item.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
            var dbName = Text(model, "dbName"); var vsName = Text(model, "vsName"); var name = Text(model, "displayName");
            if (dbName.Length == 0 || vsName.Length == 0 || name.Length == 0) continue;
            var database = vsName + "-" + dbName;
            if (database.Length > 1024 || database.Any(char.IsControl) || name.Length > 512) throw new AppException("POWER_BI_API_INVALID", "服务模型标识不合法", 3);
            var personal = item.TryGetProperty("workspaceType", out var workspaceType) && workspaceType.TryGetInt32(out var type) && type == 3;
            var workspaceId = Text(item, "workspaceObjectId");
            // 只有服务明确标为个人工作区时使用后台标记, 持久历史仍额外绑定真实账号与云.
            if (workspaceId.Length == 0 && personal) workspaceId = "@personal";
            var workspaceInfo = workspaces.FirstOrDefault(w => w.Id == workspaceId);
            var workspace = workspaceInfo?.Name;
            if (string.IsNullOrEmpty(workspace)) workspace = Text(item, "workspaceName");
            if (string.IsNullOrEmpty(workspace)) workspace = "我的工作区";
            var unavailable = Text(model, "onPremModelConnectionString").Length > 0 ? "此模型连接到外部 SSAS/AAS, 请使用服务器入口"
                : model.TryGetProperty("pushDataVersion", out var push) && push.TryGetInt32(out var n) && n != 0 ? "推送模型不支持此只读连接"
                : Text(model, "resourceName") == "ExcelModelResource" ? "Excel 模型不支持此只读连接" : null;
            var id = models.Values.FirstOrDefault(e => e.DatabaseId == dbName && e.View.WorkspaceId == workspaceId)?.View.Id ?? Guid.NewGuid().ToString("N");
            // 与 Bravo 一致: XMLA 工作区走集群端点和 TOM, 共享/个人工作区走 pbiazure 架构连接.
            var xmla = workspaceInfo?.Premium == true && !personal && unavailable is null;
            var server = xmla ? new UriBuilder(cluster) { Scheme = "powerbi", Port = -1, Path = "/v1.0/myorg/" + Uri.EscapeDataString(workspace) }.Uri.AbsoluteUri
                : "pbiazure://" + config.Backend.Host;
            next[id] = new(new(id, name, workspace, unavailable, workspaceId, dbName), xmla ? name : database, server, dbName);
            if (next.Count > 2000) throw new AppException("POWER_BI_CATALOG_LIMIT", "服务模型超过目录上限, 请改用指定 XMLA 端点", 3);
        }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(auth, session)) throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录状态已改变, 请重新获取模型", 3);
        models = next;
        return next.Values.Select(e => e.View).OrderBy(e => e.Workspace).ThenBy(e => e.Name).ToArray();
    }
    /// <summary>只解析本账号已发布且可连接的句柄; 每次读取刷新后端令牌.</summary>
    public async Task<PowerBiServiceTarget> ResolveAsync(string modelId, CancellationToken token) => await BusinessLog.RunAsync<PowerBiServiceTarget>(log, "PowerBiCloud.Resolve", async () =>
    {
        var auth = session ?? throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "请先登录 Microsoft 工作或学校账号", 3);
        if (!models.TryGetValue(modelId, out var model)) throw new AppException("INVALID_SERVICE_MODEL", "模型已失效, 请重新获取并选择", 3);
        if (model.View.UnavailableReason is not null) throw new AppException("SERVICE_MODEL_UNSUPPORTED", model.View.UnavailableReason, 3);
        var (Value, ExpiresOn) = await auth.TokenAsync(token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(auth, session)) throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录状态已改变", 3);
        return new(model.Server, model.Database, $"{configuration!.Authority}, {configuration.Resource}, {configuration.ClientId}", Value, ExpiresOn, configuration.EnvironmentName + ":" + auth.AccountId, model.DatabaseId, model.View.Name);
    });
    /// <summary>退出后旧模型句柄全部失效; 不影响系统浏览器账号.</summary>
    public async Task SignOutAsync(CancellationToken token, Action? committing = null)
    {
        token.ThrowIfCancellationRequested();
        committing?.Invoke();
        var old = session;
        if (old is not null) await old.ClearAsync();
        session = null; configuration = null; models = []; discovered = []; discoveredEmail = null;
    }
    /// <summary>只允许当前云的 HTTPS Analysis Services 集群, 严格排除跨云、端口和凭据注入.</summary>
    public static Uri TrustedCluster(string address, string environmentName = "GlobalCloud")
    {
        var suffix = PowerBiEnvironment.IsSupported(environmentName) ? PowerBiCloudPolicy.Get(environmentName).ClusterSuffix : "";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || suffix.Length == 0 || !uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new AppException("POWER_BI_CLUSTER_INVALID", "Power BI 返回的服务地址不受信任", 3);
        return uri;
    }
    /// <summary>请求限制大小且隐藏服务端原文, 禁止重定向携带令牌到其他地址.</summary>
    private async Task<JsonDocument> RequestAsync(HttpMethod method, Uri uri, string? accessToken, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (method == HttpMethod.Put) request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new AppException("POWER_BI_API_NOT_FOUND", "Power BI 服务接口不可用", 3);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AppException("POWER_BI_ACCESS_DENIED", "Power BI 权限不足或登录过期, 请检查许可证和模型 Build 权限", 3);
            if (!response.IsSuccessStatusCode) throw new AppException("POWER_BI_API_FAILED", $"Power BI 服务请求失败 (HTTP {(int)response.StatusCode}), 请稍后重试", 3);
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, token);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AppException("POWER_BI_API_TIMEOUT", "Power BI 服务请求超时, 请检查网络后重试", 3); }
        catch (HttpRequestException) { throw new AppException("POWER_BI_NETWORK_FAILED", "无法访问 Power BI 服务, 请检查网络后重试", 3); }
        catch (JsonException) { throw new AppException("POWER_BI_API_INVALID", "Power BI 服务响应无法读取", 3); }
    }
    /// <summary>可选字符串缺失时使用空值, 不读取服务连接字符串内容.</summary>
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    /// <summary>可选服务标记缺失时为 false, 不解析任意文本为布尔值.</summary>
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
    /// <summary>关闭释放本应用 HTTP 资源与内存认证引用.</summary>
    public void Dispose() { session = null; configuration = null; models.Clear(); discovered.Clear(); discoveredEmail = null; if (ownsHttp) http.Dispose(); }
}
