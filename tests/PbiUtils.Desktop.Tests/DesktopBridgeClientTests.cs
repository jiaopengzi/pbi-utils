// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopBridgeClientTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证 Desktop 管道分帧边界与文件内容校验, 不操作用户窗口.
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using System.Text;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopBridgeClientTests
{
    /// <summary>批量新增必须存在、删除必须消失, 所有修改属性均须真实落盘.</summary>
    [Fact]
    public void RequiresBatchCreatesUpdatesAndDeletesToPersist()
    {
        var expected = new ModelInfo("model", 1702, "en-US", [], [new("T", "New", "1", "", "0%", "Folder", true)], [], 0, 0);
        var value = JsonSerializer.Serialize(new MeasureBatch("baseline", [new("create", "T", "New", "1", "", "0%", "Folder", true), new("delete", "T", "Old")]), JsonSerializerOptions.Web);
        ModelEditRequest[] edits = [new("measure.batch", "batch", "items", value)];
        Assert.True(DesktopFileSync.EditsPersisted(expected, expected, edits));
        Assert.False(DesktopFileSync.EditsPersisted(expected, expected with { Measures = [] }, edits));
        Assert.False(DesktopFileSync.EditsPersisted(expected, expected with { Measures = [.. expected.Measures, new("T", "Old", "2", null, null, null, false)] }, edits));
        Assert.False(DesktopFileSync.EditsPersisted(expected, expected with { Measures = [expected.Measures[0] with { IsHidden = false }] }, edits));
        Assert.False(DesktopFileSync.EditsPersisted(expected, expected with { Measures = [expected.Measures[0] with { FormatString = "0.0" }] }, edits));
        Assert.False(DesktopFileSync.EditsPersisted(expected, expected with { Measures = [expected.Measures[0] with { Description = "stale" }] }, edits));
    }
    /// <summary>本地化快捷键可含换行空格, 但另存为和其他命令不得被选中.</summary>
    [Theory]
    [InlineData("保存 (Ctrl + S)", true)]
    [InlineData("Save\n(Ctrl+S)", true)]
    [InlineData("另存为", false)]
    [InlineData("Save as", false)]
    public void MatchesOnlySaveCommand(string name, bool expected) => Assert.Equal(expected, DesktopFileSync.IsSaveCommand(name));
    /// <summary>头部和 UTF-8 正文被任意拆分时仍按字节长度解析.</summary>
    [Fact]
    public async Task ReadsFragmentedUtf8Frame()
    {
        var body = Encoding.UTF8.GetBytes("{\"result\":\"中文\"}");
        using var stream = new FragmentedStream([.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body]);
        Assert.Equal(body, await DesktopBridgeClient.ReadFrameAsync(stream, default));
    }
    /// <summary>协议畸形和过大响应在分配正文前拒绝.</summary>
    [Theory]
    [InlineData("Content-Length: 5\r\nContent-Length: 5\r\n\r\n")]
    [InlineData("Content-Length: 1048577\r\n\r\n")]
    [InlineData("Content-Length: -1\r\n\r\n")]
    public async Task RejectsInvalidLengths(string header)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(header));
        Assert.Equal("DESKTOP_BRIDGE_PROTOCOL", (await Assert.ThrowsAsync<AppException>(() => DesktopBridgeClient.ReadFrameAsync(stream, default))).Code);
    }
    /// <summary>新文件时间不能掩盖旧的度量值内容, 换行差异可忽略.</summary>
    [Fact]
    public void RequiresExpectedPersistedValue()
    {
        var expected = new PbiUtils.Application.Models.ModelInfo("model", 1702, "en-US", [], [new("T", "M", "1", "new\r\n", null, null, false)], [], 0, 0);
        var stale = expected with { Measures = [expected.Measures[0] with { Description = "old" }] };
        PbiUtils.Application.Models.Editing.ModelEditRequest[] edits = [new("measure.set", "M", "description", "new\n", "T")];
        Assert.False(DesktopFileSync.EditsPersisted(expected, stale, edits));
        Assert.True(DesktopFileSync.EditsPersisted(expected, expected, edits));
        Assert.False(DesktopFileSync.EditsPersisted(expected, expected, [new("table.set", "missing", "description", "")]));
        Assert.False(DesktopFileSync.EditsPersisted(expected, expected, [new("udf.set", "missing", "expression", "")]));
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        /// <summary>每次最多交付两个字节, 复现命名管道分片.</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], token);
    }
}
