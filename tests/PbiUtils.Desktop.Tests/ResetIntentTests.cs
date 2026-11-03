// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\ResetIntentTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证重置意图仅在关闭成功后消费.

using PbiUtils.Desktop.Shell;
using PbiUtils.Desktop;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class ResetIntentTests
{
    [Fact]
    public void CancelledCloseCannotResetOnLaterClose()
    {
        var intent = new ResetIntent();
        intent.Schedule();
        intent.Cancel();
        Assert.False(intent.Consume());
        intent.Schedule();
        Assert.True(intent.Consume());
        Assert.False(intent.Consume());
    }
}
