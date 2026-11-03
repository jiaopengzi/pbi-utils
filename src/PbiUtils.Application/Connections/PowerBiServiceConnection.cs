// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\PowerBiServiceConnection.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义 Power BI Service 登录、模型选择与后端令牌契约.

using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;

namespace PbiUtils.Application.Connections;

/// <summary>仅用于显示的组织账号, 不含登录令牌.</summary>
public sealed record PowerBiAccount(string Username, string EnvironmentName = "GlobalCloud", string EnvironmentLabel = "全球版");
/// <summary>发现后可选择的云环境, 不向网页开放认证或接口地址.</summary>
public sealed record PowerBiEnvironment(string Name, string DisplayName)
{
    /// <summary>服务和本机记录共同使用的受支持云名称, 不接受任意地址.</summary>
    public static bool IsSupported(string name) => name is "GlobalCloud" or "ChinaCloud";
    /// <summary>只从固定云名称生成显示文本, 未知环境明确拒绝.</summary>
    public static string Label(string name) => name switch
    {
        "GlobalCloud" => "全球版",
        "ChinaCloud" => "世纪互联 (中国)",
        _ => throw new AppException("INVALID_POWER_BI_ENVIRONMENT", "请选择受支持的登录环境")
    };
}
/// <summary>最近提交的登录选择, 只保存邮箱及环境, 不含密码或令牌.</summary>
public sealed record RecentPowerBiLogin(string Id, string Email, string EnvironmentName, DateTimeOffset LastUsedAt)
{
    public string EnvironmentLabel => PowerBiEnvironment.Label(EnvironmentName);
}
/// <summary>显式登录输入, 空环境表示先发现, Remember 控制本机记录保存.</summary>
public sealed record PowerBiLoginRequest(string Email, string EnvironmentName, bool Remember);
/// <summary>登录及云选择共用的返回值, 多云未选择时不创建认证会话.</summary>
public sealed record PowerBiLoginResult(PowerBiAccount? Account, IReadOnlyList<PowerBiEnvironment> Environments, IReadOnlyList<RecentPowerBiLogin> RecentLogins);
/// <summary>当前登录和本机历史, 记录损坏时保留文件并返回明确提示.</summary>
public sealed record PowerBiLoginStatus(PowerBiAccount? Account, IReadOnlyList<RecentPowerBiLogin> RecentLogins, string? StorageWarning);
/// <summary>最近登录选择的存储边界, 不依赖网页、文件系统或认证 SDK.</summary>
public interface IPowerBiLoginStore
{
    /// <summary>加载最多20条记录, 损坏或不可读时明确失败.</summary>
    Task<IReadOnlyList<RecentPowerBiLogin>> LoadAsync(CancellationToken token);
    /// <summary>记录邮箱与环境, 失败和原子提交前取消保留旧文件.</summary>
    Task SaveAsync(string email, string environmentName, CancellationToken token);
    /// <summary>删除已保存的受限记录标识, 不接受任意路径.</summary>
    Task DeleteAsync(string id, CancellationToken token);
}
/// <summary>后端发布的模型句柄与展示信息, 不向网页暴露服务器或凭据.</summary>
public sealed record PowerBiServiceModel(string Id, string Name, string Workspace, string? UnavailableReason, string? WorkspaceId = null, string? ModelId = null);
/// <summary>持久连接身份只包含目录标识, 名称及句柄必须从当前账号目录重新取得.</summary>
public sealed record StoredPowerBiServiceModel(string Id, string WorkspaceId, string ModelId, DateTimeOffset LastUsedAt);
/// <summary>通过本次账号目录权限验证的最近连接, ModelId 是当前后端句柄.</summary>
public sealed record RecentPowerBiServiceModel(string Id, string ModelId, string Name, string Workspace, DateTimeOffset LastUsedAt);
/// <summary>最近连接读取结果, 存储异常不会伪装成可用历史.</summary>
public sealed record RecentPowerBiServiceModels(IReadOnlyList<RecentPowerBiServiceModel> Items, string? StorageWarning);
/// <summary>连接开始前捕获的后台目录身份, 不接受网页回传或在成功后改用另一个账号.</summary>
public sealed record PowerBiRecentConnection([property: System.Text.Json.Serialization.JsonIgnore] string AccountId, string EnvironmentName, string WorkspaceId, string ModelId);
/// <summary>按真实认证身份及云隔离最近成功连接, 不保存令牌或可执行连接地址.</summary>
public interface IPowerBiRecentModelStore
{
    /// <summary>只加载后台认证身份所属的云分区, 损坏时保留并明确失败.</summary>
    Task<IReadOnlyList<StoredPowerBiServiceModel>> LoadAsync(string accountId, string environmentName, CancellationToken token);
    /// <summary>只记录成功连接的稳定目录标识, 锁内合并并原子保存.</summary>
    Task SaveAsync(string accountId, string environmentName, string workspaceId, string modelId, CancellationToken token);
    /// <summary>只删除指定真实身份及云分区的记录, 提交前裁决取消, 不删除远端模型.</summary>
    Task<bool> DeleteAsync(string accountId, string environmentName, string id, CancellationToken token, Action? committing = null);
}
/// <summary>仅在后端传递的服务连接信息; 字符串表示与 JSON 均不暴露访问令牌.</summary>
public sealed class PowerBiServiceTarget(string server, string database, string identityProvider, string accessToken, DateTimeOffset expiresOn, string accountId,
    string? databaseId = null, string? modelName = null)
{
    public string Server { get; } = server;
    public string Database { get; } = database;
    public string IdentityProvider { get; } = identityProvider;
    public string? DatabaseId { get; } = databaseId;
    public string? ModelName { get; } = modelName;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccessToken { get; } = accessToken;
    public DateTimeOffset ExpiresOn { get; } = expiresOn;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccountId { get; } = accountId;
    /// <summary>禁止日志字符串展开后端连接凭据.</summary>
    public override string ToString() => "Power BI Service target";
}
/// <summary>认证和服务 API 的实际适配器, 只接受用户选择的后端句柄.</summary>
public interface IPowerBiServiceClient
{
    PowerBiAccount? Account { get; }
    /// <summary>真实认证账号的稳定后台身份, 不从邮箱输入或网页字段推导.</summary>
    string? AccountId => null;
    /// <summary>按邮箱发现支持的环境; 不登录或替换当前账号.</summary>
    Task<IReadOnlyList<PowerBiEnvironment>> EnvironmentsAsync(string email, CancellationToken token);
    /// <summary>用户主动登录; 失败或取消不替换当前账号.</summary>
    Task<PowerBiAccount> SignInAsync(string email, string environmentName, CancellationToken token, Action? committing = null);
    /// <summary>读取当前账号可见模型; 失败不发布不完整列表.</summary>
    Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token);
    /// <summary>只解析当前账号已发布的模型, 令牌由后端刷新.</summary>
    Task<PowerBiServiceTarget> ResolveAsync(string modelId, CancellationToken token);
    /// <summary>清除本应用的账号和内存缓存.</summary>
    Task SignOutAsync(CancellationToken token, Action? committing = null);
}
/// <summary>共享登录和模型列表用例, 不依赖桌面 UI、网络 SDK 或进程.</summary>
public sealed class PowerBiServiceConnection(IPowerBiServiceClient client, ILogger<PowerBiServiceConnection>? logger = null, IPowerBiLoginStore? loginStore = null,
    IPowerBiRecentModelStore? recentModelStore = null)
{
    private readonly ILogger log = logger ?? NullLogger<PowerBiServiceConnection>.Instance;
    private IReadOnlyList<PowerBiServiceModel> catalog = [];
    private string? catalogAccountId;
    private string? catalogEnvironment;
    private string? recentStorageWarning;

    public PowerBiAccount? Account => client.Account;
    /// <summary>同一登录用例负责发现、选择和认证; 单云直接登录, 多云等待明确选择.</summary>
    /// <param name="request">邮箱、已选环境或空值, 以及是否记住选择.</param>
    /// <param name="token">发现、保存和认证的取消.</param>
    /// <param name="committing">宿主在认证缓存生效前原子裁决取消.</param>
    /// <param name="phase">只报告真实的环境检测与认证阶段, 不回传邮箱或认证配置.</param>
    /// <returns>需要选择的环境, 或已完成登录的账号与最近记录.</returns>
    public Task<PowerBiLoginResult> SignInAsync(PowerBiLoginRequest request, CancellationToken token, Action? committing = null, Action<string>? phase = null) => BusinessLog.RunAsync<PowerBiLoginResult>(log, "PowerBiServiceConnection.SignIn", async () =>
    {
        var email = ValidateEmail(request.Email);
        if (request.EnvironmentName.Length > 0 && !PowerBiEnvironment.IsSupported(request.EnvironmentName))
            throw new AppException("INVALID_POWER_BI_ENVIRONMENT", "请选择受支持的登录环境");
        var environment = request.EnvironmentName;
        if (environment.Length == 0 && loginStore is not null)
        {
            try
            {
                var remembered = (await loginStore.LoadAsync(token)).FirstOrDefault(item => string.Equals(item.Email, email, StringComparison.OrdinalIgnoreCase));
                if (remembered is not null) environment = remembered.EnvironmentName;
            }
            catch (AppException) when (!request.Remember) { /* 不记住选择时允许绕过损坏历史, 但不覆盖原文件. */ }
        }
        if (environment.Length == 0) phase?.Invoke("discoveringEnvironment");
        var environments = await client.EnvironmentsAsync(email, token);
        if (environments.Count == 0) throw new AppException("POWER_BI_ENVIRONMENT_UNAVAILABLE", "未发现可用的登录环境, 请核对组织邮箱", 3);
        if (environment.Length > 0 && !environments.Any(e => e.Name == environment))
            throw new AppException("INVALID_POWER_BI_ENVIRONMENT", "账号可用环境已改变, 请重新登录并选择", 3);
        if (environments.Count > 1 && environment.Length == 0)
            return new(null, environments, (await StatusAsync(token)).RecentLogins);
        if (environment.Length == 0) environment = environments[0].Name;
        // 与 SSAS 一致, 仅用户实际提交的登录尝试落盘, 失败或后续取消认证也可下次复用输入.
        if (request.Remember && loginStore is not null) await loginStore.SaveAsync(email, environment, token);
        phase?.Invoke("authenticating");
        var account = await client.SignInAsync(email, environment, token, committing);
        catalog = []; catalogAccountId = null; catalogEnvironment = null; recentStorageWarning = null;
        return new(account, [], (await StatusAsync(CancellationToken.None)).RecentLogins);
    });
    /// <summary>打开连接中心只加载本机记录, 不发现云或登录; 损坏文件保留并显示提示.</summary>
    public Task<PowerBiLoginStatus> StatusAsync(CancellationToken token) => BusinessLog.RunAsync<PowerBiLoginStatus>(log, "PowerBiServiceConnection.Status", async () =>
    {
        try { return new(Account, loginStore is null ? [] : await loginStore.LoadAsync(token), null); }
        catch (AppException error) { return new(Account, [], error.Message); }
    });
    /// <summary>删除最近登录不退出账号或断开模型, 当前输入可继续手工使用.</summary>
    public Task DeleteLoginAsync(string id, CancellationToken token) => BusinessLog.RunAsync(log, "PowerBiServiceConnection.DeleteLogin", () =>
        loginStore?.DeleteAsync(id, token) ?? throw new AppException("UNSUPPORTED", "当前宿主未配置最近登录存储", 3));
    /// <summary>邮箱有界且不得携带显示名、控制字符或多个收件人; 返回去除首尾空格的地址.</summary>
    public static string ValidateEmail(string email)
    {
        var value = email.Trim();
        if (value.Length is < 3 or > 254 || value.Any(char.IsControl) || !System.Net.Mail.MailAddress.TryCreate(value, out var address)
            || address.Address != value || !address.Host.Contains('.'))
            throw new AppException("INVALID_POWER_BI_EMAIL", "请输入有效的工作或学校邮箱");
        return value;
    }
    /// <summary>未登录时明确报错, 不返回虚假的空成功.</summary>
    public Task<IReadOnlyList<PowerBiServiceModel>> ModelsAsync(CancellationToken token) => BusinessLog.RunAsync<IReadOnlyList<PowerBiServiceModel>>(log, "PowerBiServiceConnection.Models", async () =>
    {
        var account = client.Account ?? throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "请先登录 Microsoft 工作或学校账号", 3);
        var accountId = client.AccountId; var environment = account.EnvironmentName;
        var current = await client.ModelsAsync(token);
        token.ThrowIfCancellationRequested();
        if (accountId != client.AccountId || environment != client.Account?.EnvironmentName)
            throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录状态已改变, 请重新获取模型", 3);
        catalog = current; catalogAccountId = accountId; catalogEnvironment = environment;
        return current;
    });
    /// <summary>登录后重新获取当前目录, 只返回同一真实账号和云且稳定目录身份唯一匹配的可连接历史.</summary>
    public Task<RecentPowerBiServiceModels> RecentModelsAsync(CancellationToken token) => BusinessLog.RunAsync<RecentPowerBiServiceModels>(log, "PowerBiServiceConnection.RecentModels", async () =>
    {
        var accountId = client.AccountId; var environment = client.Account?.EnvironmentName;
        var current = await ModelsAsync(token);
        if (accountId != client.AccountId || environment != client.Account?.EnvironmentName)
            throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录状态已改变, 请重新获取模型", 3);
        if (recentModelStore is null || string.IsNullOrEmpty(accountId)) return new([], recentStorageWarning);
        IReadOnlyList<StoredPowerBiServiceModel> saved;
        try { saved = await recentModelStore.LoadAsync(accountId, environment!, token); }
        catch (AppException error) { return new([], error.Message); }
        token.ThrowIfCancellationRequested();
        if (accountId != client.AccountId || environment != client.Account?.EnvironmentName)
            throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录状态已改变, 请重新获取模型", 3);
        List<RecentPowerBiServiceModel> visible = [];
        foreach (var item in saved)
        {
            var matches = current.Where(model => model.WorkspaceId == item.WorkspaceId && model.ModelId == item.ModelId && model.UnavailableReason is null).ToArray();
            if (matches.Length == 1) visible.Add(new(item.Id, matches[0].Id, matches[0].Name, matches[0].Workspace, item.LastUsedAt));
        }
        return new(visible, recentStorageWarning);
    });
    /// <summary>按后台当前认证身份删除最近模型记录, 不以邮箱、名称或网页模型句柄选择账号分区.</summary>
    /// <param name="id">宿主发布的最近记录身份.</param>
    /// <param name="token">提交之前可取消, 原子保存开始后保留真实结果.</param>
    /// <param name="committing">宿主在开始原子保存前裁决取消.</param>
    /// <returns>是否已删除当前账号及云的一条历史记录.</returns>
    /// <exception cref="AppException">未登录、身份无效、认证已变化或本机记录不可写.</exception>
    public Task<RecentRecordDeletionResult> DeleteRecentModelAsync(string id, CancellationToken token, Action? committing = null) => BusinessLog.RunAsync(log, "PowerBiServiceConnection.DeleteRecentModel", async () =>
    {
        RecentRecordDeletionRequest.ValidateId(id);
        var accountId = client.AccountId; var environment = client.Account?.EnvironmentName;
        if (string.IsNullOrEmpty(accountId) || environment is null)
            throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "请先登录 Microsoft 工作或学校账号", 3);
        var store = recentModelStore ?? throw new AppException("UNSUPPORTED", "当前宿主未配置最近模型存储", 3);
        var deleted = await store.DeleteAsync(accountId, environment, id, token, () =>
        {
            if (accountId != client.AccountId || environment != client.Account?.EnvironmentName)
                throw new AppException("POWER_BI_SIGN_IN_REQUIRED", "登录状态已改变, 请重新获取模型", 3);
            token.ThrowIfCancellationRequested(); committing?.Invoke();
        });
        return new RecentRecordDeletionResult(deleted);
    });
    /// <summary>宿主开始连接前捕获真实身份与目录绑定, 缺失身份或无效目录不能产生持久记录.</summary>
    public PowerBiRecentConnection? CaptureConnection(string modelId)
    {
        if (recentModelStore is null || string.IsNullOrEmpty(client.AccountId)) return null;
        var matches = catalog.Where(model => model.Id == modelId && model.UnavailableReason is null).ToArray();
        if (matches.Length != 1 || catalogAccountId != client.AccountId || catalogEnvironment != client.Account?.EnvironmentName
            || string.IsNullOrEmpty(matches[0].WorkspaceId) || string.IsNullOrEmpty(matches[0].ModelId)) return null;
        return new(catalogAccountId!, catalogEnvironment!, matches[0].WorkspaceId!, matches[0].ModelId!);
    }
    /// <summary>宿主发布成功快照后按连接前捕获的真实身份记录, 存储失败只保留提示, 不推翻真实连接结果.</summary>
    public Task RecordConnectionAsync(PowerBiRecentConnection? connection, CancellationToken token) => BusinessLog.RunAsync(log, "PowerBiServiceConnection.RecordConnection", async () =>
    {
        if (recentModelStore is null || connection is null) return;
        try
        {
            await recentModelStore.SaveAsync(connection.AccountId, connection.EnvironmentName, connection.WorkspaceId, connection.ModelId, token);
            recentStorageWarning = null;
        }
        catch (AppException error) { recentStorageWarning = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { recentStorageWarning = "最近云端模型保存失败, 原记录保留; 可继续使用模型目录连接"; }
    });
    /// <summary>退出只清理当前应用持有的认证状态.</summary>
    public Task SignOutAsync(CancellationToken token, Action? committing = null) => BusinessLog.RunAsync(log, "PowerBiServiceConnection.SignOut", async () =>
    {
        await client.SignOutAsync(token, committing);
        catalog = []; catalogAccountId = null; catalogEnvironment = null; recentStorageWarning = null;
    });
}
