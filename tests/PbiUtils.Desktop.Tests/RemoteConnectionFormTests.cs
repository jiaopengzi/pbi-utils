// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\RemoteConnectionFormTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 用真实隔离 DPAPI 存储验证平铺表单的一次性身份、密码绑定、撤销及存储操作, 不访问网络.

using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Connections;
using PbiUtils.Application.Connections;
using System.Net;
using System.Security;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>仅使用合成目标与假密码, 不读取用户最近记录或创建 Windows 网络身份.</summary>
public sealed class RemoteConnectionFormTests
{
    /// <summary>保存密码只在精确匹配且点击连接时恢复, 读取表单不改存储、不暴露密码和密文.</summary>
    [Fact]
    public void ReusesExactStoredCredentialWithoutEchoingItToTheFrontend()
    {
        using var probe = new FormProbe();
        var id = probe.SaveCredential();
        var before = File.ReadAllBytes(probe.StoreFile);
        var view = probe.Form.Read(default);
        Assert.True(Assert.Single(view.Recent).HasSavedPassword);
        var output = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(FormProbe.Password, output);
        Assert.DoesNotContain("protectedPassword", output, StringComparison.OrdinalIgnoreCase);
        using var selected = probe.Form.Submit(view.FormId, FormProbe.Input(id), default);
        Assert.NotNull(selected.Credential);
        Assert.Equal(FormProbe.Password, selected.Credential.Use((name, secret) => new NetworkCredential(name, secret).Password));
        Assert.Equal(before, File.ReadAllBytes(probe.StoreFile));
        Assert.Equal("REMOTE_FORM_EXPIRED", Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, FormProbe.Input(id), default)).Code);
    }

    /// <summary>目标、模型和账号任一变化时, 旧 ID 不能把保存密码授予新的身份.</summary>
    [Theory]
    [InlineData("server", "different-server")]
    [InlineData("database", "different-model")]
    [InlineData("userName", "private-server\\different-user")]
    public void ChangedIdentityCannotReuseStoredPassword(string field, string value)
    {
        using var probe = new FormProbe();
        var id = probe.SaveCredential();
        var before = File.ReadAllBytes(probe.StoreFile);
        var view = probe.Form.Read(default);
        var input = FormProbe.Fields(id); input[field] = value;
        Assert.Equal("REMOTE_PASSWORD_MISSING", Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, JsonSerializer.SerializeToElement(input), default)).Code);
        Assert.Equal("REMOTE_FORM_EXPIRED", Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, FormProbe.Input(id), default)).Code);
        Assert.Equal(before, File.ReadAllBytes(probe.StoreFile));
    }

    /// <summary>XMLA 拒绝指定 Windows 身份, 返回错误不包含新密码, 不写记录.</summary>
    [Fact]
    public void XmlaRejectsWindowsCredentialsWithoutEchoingOrSavingPassword()
    {
        using var probe = new FormProbe();
        var view = probe.Form.Read(default);
        var input = FormProbe.Fields("");
        input["server"] = "powerbi://api.powerbi.com/v1.0/myorg/test";
        input["password"] = FormProbe.Password; input["remember"] = true; input["rememberPassword"] = true;
        var error = Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, JsonSerializer.SerializeToElement(input), default));
        Assert.Equal("INVALID_AUTHENTICATION_MODE", error.Code);
        Assert.DoesNotContain(FormProbe.Password, error.ToString());
        Assert.False(File.Exists(probe.StoreFile));
    }

    /// <summary>另一实例清除密码后不静默降级为当前身份.</summary>
    [Fact]
    public void ExternallyClearedPasswordDoesNotFallBackToCurrentIdentity()
    {
        using var probe = new FormProbe(); var id = probe.SaveCredential();
        var view = probe.Form.Read(default);
        new LocalRemoteConnectionStore(probe.Root).ClearPassword(id);
        Assert.Equal("REMOTE_PASSWORD_MISSING", Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, FormProbe.Input(id), default)).Code);
        var current = Assert.Single(probe.Store.Load());
        Assert.Equal("windows", current.AuthenticationMode); Assert.False(current.HasSavedPassword);
    }

    /// <summary>导航和关闭撤销上下文, 新表单替换旧身份, 迟到连接和存储操作都无效.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidatedOrReplacedContextRejectsLateSubmissionAndMutation(bool replaced)
    {
        using var probe = new FormProbe(); var id = probe.SaveCredential();
        var before = File.ReadAllBytes(probe.StoreFile); var view = probe.Form.Read(default);
        if (replaced) Assert.NotEqual(view.FormId, probe.Form.Read(default).FormId); else probe.Form.Invalidate();
        Assert.Equal("REMOTE_FORM_EXPIRED", Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, FormProbe.Input(id), default)).Code);
        Assert.Equal("REMOTE_FORM_EXPIRED", Assert.Throws<AppException>(() => probe.Form.Mutate(view.FormId, "delete", id, () => throw new InvalidOperationException(), default)).Code);
        Assert.Equal(before, File.ReadAllBytes(probe.StoreFile));
    }

    /// <summary>取消先于提交时不保存连接, 且已消费上下文不能重放.</summary>
    [Fact]
    public void CancellationBeforeSubmitDoesNotSaveAndConsumesContext()
    {
        using var probe = new FormProbe(); var view = probe.Form.Read(default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var input = FormProbe.Fields(""); input["password"] = FormProbe.Password; input["remember"] = true; input["rememberPassword"] = true;
        Assert.ThrowsAny<OperationCanceledException>(() => probe.Form.Submit(view.FormId, JsonSerializer.SerializeToElement(input), cancellation.Token));
        Assert.False(File.Exists(probe.StoreFile));
        Assert.Equal("REMOTE_FORM_EXPIRED", Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, JsonSerializer.SerializeToElement(input), default)).Code);
    }

    /// <summary>字段白名单、重复字段和明确布尔偏好均由宿主校验, 不依赖页面控件.</summary>
    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("stringBoolean")]
    [InlineData("duplicate")]
    public void RejectsInvalidOrAmbiguousInputBeforeStorage(string invalid)
    {
        using var probe = new FormProbe(); var view = probe.Form.Read(default);
        var input = FormProbe.Fields(""); input["password"] = FormProbe.Password;
        if (invalid == "extra") input["accessToken"] = "unexpected";
        if (invalid == "missing") input.Remove("remember");
        if (invalid == "stringBoolean") input["remember"] = "true";
        var raw = JsonSerializer.Serialize(input);
        if (invalid == "duplicate") raw = raw.Insert(1, "\"remember\":false,");
        using var json = JsonDocument.Parse(raw);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, json.RootElement, default)).Code);
        Assert.False(File.Exists(probe.StoreFile));
    }

    /// <summary>清密码和删除只能操作本次元数据中的 ID, 每次提交产生新上下文并保留其他记录.</summary>
    [Fact]
    public void MutationsAreBoundToDisplayedRecordsAndRotateContext()
    {
        using var probe = new FormProbe(); var id = probe.SaveCredential(); var view = probe.Form.Read(default);
        using var another = new RemoteModelAddress("second-server", "second-model"); var otherId = probe.Store.Save(another, false, default);
        Assert.Equal("REMOTE_CONNECTION_NOT_FOUND", Assert.Throws<AppException>(() => probe.Form.Mutate(view.FormId, "delete", otherId, () => throw new InvalidOperationException(), default)).Code);
        var committed = false;
        var updated = probe.Form.Mutate(view.FormId, "clearPassword", id, () => committed = true, default);
        Assert.True(committed); Assert.NotEqual(view.FormId, updated.FormId); Assert.False(updated.Recent.Single(item => item.Id == id).HasSavedPassword);
        Assert.Equal("REMOTE_FORM_EXPIRED", Assert.Throws<AppException>(() => probe.Form.Mutate(view.FormId, "delete", id, () => { }, default)).Code);
        updated = probe.Form.Mutate(updated.FormId, "delete", id, () => { }, default);
        Assert.Equal(otherId, Assert.Single(updated.Recent).Id);
    }

    /// <summary>取消存储操作不会触发提交回调或更改密文.</summary>
    [Fact]
    public void CancelledMutationPreservesCredential()
    {
        using var probe = new FormProbe(); var id = probe.SaveCredential(); var view = probe.Form.Read(default);
        var before = File.ReadAllBytes(probe.StoreFile);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => probe.Form.Mutate(view.FormId, "clearPassword", id, () => throw new InvalidOperationException(), cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(probe.StoreFile));
    }

    /// <summary>损坏存储保持原状, 用户不保存时仍可手工连接, 明确记住时拒绝覆盖.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptHistoryAllowsManualConnectionOnlyWithoutSaving(bool remember)
    {
        using var probe = new FormProbe(); Directory.CreateDirectory(probe.Root); File.WriteAllText(probe.StoreFile, "invalid");
        var view = probe.Form.Read(default); Assert.NotNull(view.Error); Assert.Empty(view.Recent);
        var input = FormProbe.Fields(""); input["password"] = FormProbe.Password; input["remember"] = remember;
        if (remember) Assert.Throws<AppException>(() => probe.Form.Submit(view.FormId, JsonSerializer.SerializeToElement(input), default));
        else { using var target = probe.Form.Submit(view.FormId, JsonSerializer.SerializeToElement(input), default); Assert.NotNull(target.Credential); }
        Assert.Equal("invalid", File.ReadAllText(probe.StoreFile));
    }

    /// <summary>独立测试目录和合成凭据, 不触碰用户配置.</summary>
    private sealed class FormProbe : IDisposable
    {
        public const string Password = "fake-private-password-🔒";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pbi-remote-form-tests-" + Guid.NewGuid().ToString("N"));
        public string StoreFile => Path.Combine(Root, "remote-connections.json");
        public LocalRemoteConnectionStore Store { get; }
        public RemoteConnectionForm Form { get; }
        /// <summary>提供真实隔离存储.</summary>
        public FormProbe() { Store = new(Root); Form = new(Store); }
        /// <summary>保存本测试的合成密码.</summary>
        public string SaveCredential()
        {
            using var secret = new SecureString(); foreach (var character in Password) secret.AppendChar(character);
            using var credential = new RemoteWindowsCredential("private-server\\private-user", secret);
            using var address = new RemoteModelAddress("private-server", "private-model", credential);
            return Store.Save(address, true, default);
        }
        /// <summary>构造明确偏好的完整前端输入.</summary>
        public static Dictionary<string, object> Fields(string id) => new()
        {
            ["server"] = "private-server",
            ["database"] = "private-model",
            ["authenticationMode"] = "windows",
            ["userName"] = "private-server\\private-user",
            ["password"] = "",
            ["remember"] = false,
            ["rememberPassword"] = false,
            ["recentId"] = id
        };
        /// <summary>序列化本测试输入, 不写入日志.</summary>
        public static JsonElement Input(string id) => JsonSerializer.SerializeToElement(Fields(id));
        /// <summary>仅删除本测试唯一目录.</summary>
        public void Dispose()
        {
            Form.Invalidate(); Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Root));
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
