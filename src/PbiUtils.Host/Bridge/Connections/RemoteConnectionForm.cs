// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Connections\RemoteConnectionForm.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为平铺远端表单提供文档级一次性上下文, 宿主独立校验目标、最近记录与密码绑定.

using PbiUtils.Infrastructure.Connections;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Connections;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Connections;

/// <summary>只含非秘密元数据的平铺表单快照, 不包含保存密码或密文.</summary>
/// <param name="FormId">当前文档内一次性表单身份.</param>
/// <param name="Recent">此身份可引用的最近记录快照.</param>
/// <param name="Error">读取失败时的安全提示, 手工输入仍可使用.</param>
public sealed record RemoteConnectionFormView(string FormId, IReadOnlyList<RecentRemoteConnection> Recent, string? Error = null);

/// <summary>输入在页面中保留, 宿主只保存随机身份和非秘密快照; 提交后立即消费身份.</summary>
/// <param name="store">固定用户目录内的最近连接与 DPAPI 存储.</param>
/// <param name="logger">只记录安全阶段与错误码.</param>
public sealed class RemoteConnectionForm(LocalRemoteConnectionStore store, ILogger<RemoteConnectionForm>? logger = null)
{
    private static readonly string[] Fields = ["server", "database", "authenticationMode", "userName", "password", "remember", "rememberPassword", "recentId"];
    private readonly ILogger log = logger ?? NullLogger<RemoteConnectionForm>.Instance;
    private readonly Lock sync = new();
    private RemoteConnectionFormView? active;

    /// <summary>立即创建新的文档上下文, 不等待用户输入、不占有模型门闩或解密保存密码.</summary>
    public RemoteConnectionFormView Read(CancellationToken token)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<RecentRemoteConnection> recent = [];
            string? error = null;
            try { recent = store.Load(); }
            catch (AppException failure) { error = failure.Message; BusinessLog.RemoteDialogRejected(log, failure.Code); }
            token.ThrowIfCancellationRequested();
            active = new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), recent, error);
            return active;
        }
    }

    /// <summary>仅修改本上下文实际展示的记录; 原子裁决提交后再改存储并返回新身份.</summary>
    public RemoteConnectionFormView Mutate(string formId, string action, string recordId, Action committing, CancellationToken token)
    {
        lock (sync)
        {
            var current = Current(formId);
            if (action is not ("delete" or "clearPassword")) throw new AppException("VALIDATION_ERROR", "最近连接操作无效");
            var entry = Selected(current.Recent, recordId);
            token.ThrowIfCancellationRequested();
            committing();
            active = null;
            if (action == "delete") store.Delete(entry.Id);
            else store.ClearPassword(entry.Id);
            return Read(CancellationToken.None);
        }
    }

    /// <summary>消费当前表单并按完整目标和身份复核存储, 仅连接提交时恢复密码.</summary>
    /// <param name="formId">可信本地界面此前取得的一次表单身份.</param>
    /// <param name="fields">只允许固定字段的新输入, 不回传秘密.</param>
    /// <param name="token">提交前取消令牌.</param>
    /// <param name="committing">实际持久化最近记录或密码之前的可靠提交边界; 不保存时不调用.</param>
    /// <returns>由连接调用者释放的地址与临时凭据.</returns>
    public RemoteModelAddress Submit(string formId, JsonElement fields, CancellationToken token, Action? committing = null)
    {
        lock (sync)
        {
            var current = Current(formId);
            active = null;
            token.ThrowIfCancellationRequested();
            if (fields.ValueKind != JsonValueKind.Object) throw Invalid();
            var names = fields.EnumerateObject().Select(item => item.Name).ToArray();
            if (names.Length != Fields.Length || names.Distinct().Count() != names.Length || names.Any(name => !Fields.Contains(name))) throw Invalid();
            return CreateAddress(fields, current.Recent, committing, token);
        }
    }

    /// <summary>导航或关闭撤销旧页面尚未提交的身份, 不保留任何输入密码.</summary>
    public void Invalidate() { lock (sync) active = null; }

    /// <summary>只接受当前文档最新宿主身份, 失效、迟到与重复提交都不能读取密码或修改记录.</summary>
    private RemoteConnectionFormView Current(string formId) => active is not null && active.FormId == formId
        ? active : throw new AppException("REMOTE_FORM_EXPIRED", "连接表单已更新, 请重新载入后再试");

    /// <summary>独立校验所有字段, 失败始终释放新建凭据, 错误和返回元数据都不包含输入密码.</summary>
    private RemoteModelAddress CreateAddress(JsonElement fields, IReadOnlyList<RecentRemoteConnection> displayed, Action? committing, CancellationToken token)
    {
        var server = Field(fields, "server", 1024).Trim();
        var database = Field(fields, "database", 200).Trim();
        var mode = Field(fields, "authenticationMode", 16);
        var name = Field(fields, "userName", 256).Trim();
        var password = Field(fields, "password", 1024);
        var recentId = Field(fields, "recentId", 64);
        var remember = Flag(fields, "remember");
        var rememberPassword = Flag(fields, "rememberPassword");
        if (mode is not ("current" or "windows") || (mode == "current" && (name.Length != 0 || password.Length != 0 || rememberPassword))
            || (rememberPassword && !remember)) throw new AppException("INVALID_AUTHENTICATION_MODE", "身份验证参数不正确");
        RemoteWindowsCredential? credential = null;
        RemoteModelAddress? candidate = null;
        try
        {
            if (mode == "windows")
            {
                if (server.Contains("://", StringComparison.Ordinal)) throw new AppException("INVALID_AUTHENTICATION_MODE", "指定 Windows 账号仅用于 SSAS 主机, Power BI XMLA 使用组织身份");
                if (password.Length == 0)
                {
                    var entry = Selected(displayed, recentId);
                    if (!entry.HasSavedPassword || entry.AuthenticationMode != mode || !string.Equals(server, entry.Server, StringComparison.OrdinalIgnoreCase)
                        || database != entry.Database || !string.Equals(name, entry.UserName, StringComparison.OrdinalIgnoreCase))
                        throw new AppException("REMOTE_PASSWORD_MISSING", "保存的密码与当前目标或身份不匹配, 请重新输入");
                    credential = store.ReadCredential(entry.Id) ?? throw new AppException("REMOTE_PASSWORD_MISSING", "保存的密码已被清除, 请重新输入目标账号密码");
                }
                else
                {
                    using var secret = new SecureString();
                    foreach (var character in password) secret.AppendChar(character);
                    credential = new RemoteWindowsCredential(name, secret);
                }
            }
            candidate = new(server, database, credential);
            RemoteModelAddressPolicy.Validate(candidate);
            token.ThrowIfCancellationRequested();
            if (remember) { committing?.Invoke(); store.Save(candidate, rememberPassword, token); }
            BusinessLog.RemoteDialogSubmitted(log, mode == "windows", remember);
            return candidate;
        }
        catch { if (candidate is not null) candidate.Dispose(); else credential?.Dispose(); throw; }
    }

    /// <summary>网页不能引用未在本次上下文展示的存储目标.</summary>
    private static RecentRemoteConnection Selected(IReadOnlyList<RecentRemoteConnection> entries, string id) =>
        entries.SingleOrDefault(entry => entry.Id == id) ?? throw new AppException("REMOTE_CONNECTION_NOT_FOUND", "最近连接记录不存在, 请重新选择或输入连接");

    /// <summary>字符串字段必须存在且不超长, 不将输入内容放进错误.</summary>
    private static string Field(JsonElement fields, string name, int maximum)
    {
        if (!fields.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximum) throw Invalid();
        return value.GetString()!;
    }

    /// <summary>授权偏好只能用明确布尔值, 不把遗漏、文本或数字视为同意.</summary>
    private static bool Flag(JsonElement fields, string name) => fields.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Invalid()
    };

    /// <summary>不包含输入内容的稳定校验错误.</summary>
    private static AppException Invalid() => new("VALIDATION_ERROR", "远端连接输入字段不正确");
}
