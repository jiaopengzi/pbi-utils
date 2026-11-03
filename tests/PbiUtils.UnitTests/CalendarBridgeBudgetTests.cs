// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\CalendarBridgeBudgetTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证日期响应实际身份和连接代际参与完整 4 MiB 信封预算, 不预扣虚构元数据.

using PbiUtils.Host.Bridge.Calendar;
using System.Text.Json;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed class CalendarBridgeBudgetTests
{
    /// <summary>响应预算采用宿主实际序列化器的转义成本, 原文不超业务上限也不能发出超额信封.</summary>
    [Theory]
    [InlineData('"')]
    [InlineData('\\')]
    [InlineData('中')]
    public void ResponseBudgetCountsHostEscaping(char character)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string id = "short";
        const long connectionRevision = 0;
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, id, connectionRevision, result = "" }, json).Length;
        var characterCost = JsonSerializer.SerializeToUtf8Bytes(character.ToString(), json).Length - 2;
        var contentBudget = CalendarBridgeBudget.Absolute - overhead;
        var result = new string(character, contentBudget / characterCost) + new string('a', contentBudget % characterCost);
        Assert.Equal(CalendarBridgeBudget.Absolute, JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, id, connectionRevision, result }, json).Length);
        CalendarBridgeBudget.PreviewResult(id, result, connectionRevision);
        Assert.Equal("MESSAGE_TOO_LARGE", Assert.Throws<AppException>(() => CalendarBridgeBudget.PreviewResult(id, result + "a", connectionRevision)).Code);
    }

    /// <summary>实际 ID 与代际决定精确边界; null、短 ID 和长 ID 各自完整使用预算, 多一字节拒绝.</summary>
    [Theory]
    [InlineData(0, 0L)]
    [InlineData(1, 0L)]
    [InlineData(1, 9007199254740991L)]
    [InlineData(32, 9007199254740991L)]
    [InlineData(36, 9007199254740991L)]
    [InlineData(100, 9007199254740991L)]
    public void ResponseBudgetIncludesActualIdentityAndRevision(int idLength, long connectionRevision)
    {
        var id = idLength == 0 ? null : new string('i', idLength);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, id, connectionRevision, result = "" }, json).Length;
        var result = new string('a', CalendarBridgeBudget.Absolute - overhead);
        Assert.Equal(CalendarBridgeBudget.Absolute, JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, id, connectionRevision, result }, json).Length);
        CalendarBridgeBudget.PreviewResult(id, result, connectionRevision);
        Assert.Equal("MESSAGE_TOO_LARGE", Assert.Throws<AppException>(() => CalendarBridgeBudget.PreviewResult(id, result + "a", connectionRevision)).Code);
    }
}
