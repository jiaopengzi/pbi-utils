// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GuiDocumentContextTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 GUI 文档与关闭票据隔离, 防止旧页面及关窗确认期间的新业务进入后台.

using PbiUtils.Host.Bridge.Session;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class GuiDocumentContextTests
{
    /// <summary>导航只接受严格递增代际, 旧页面的请求不能借用新文档上下文.</summary>
    [Fact]
    public void NavigationInvalidatesPreviousDocument()
    {
        var context = new GuiDocumentContext();
        context.RequireRequest(0, "project.inspect");
        context.Navigate(1);
        Assert.Equal("STALE_DOCUMENT", Assert.Throws<AppException>(() => context.RequireRequest(0, "project.inspect")).Code);
        context.RequireRequest(1, "project.inspect");
        Assert.Equal("STALE_DOCUMENT", Assert.Throws<AppException>(() => context.Navigate(0)).Code);
    }

    /// <summary>关闭票据冻结业务但保留回复和取消, 错误或迟到票据不能解除另一轮关闭.</summary>
    [Fact]
    public void CloseTicketBlocksBusinessButAllowsControl()
    {
        var context = new GuiDocumentContext();
        var first = context.PrepareClose();
        Assert.Equal("BUSY", Assert.Throws<AppException>(() => context.RequireRequest(0, "model.edit")).Code);
        context.RequireRequest(0, "ui.dialogRespond");
        context.RequireRequest(0, "request.cancel");
        Assert.False(context.AbortClose("untrusted"));
        Assert.True(context.AbortClose(first));
        context.RequireRequest(0, "model.edit");
        var second = context.PrepareClose();
        Assert.NotEqual(first, second);
        Assert.False(context.AbortClose(first));
        context.ConfirmClose(second);
        Assert.Equal("HOST_CONTEXT_CLOSED", Assert.Throws<AppException>(() => context.RequireRequest(0, "project.inspect")).Code);
    }
}
