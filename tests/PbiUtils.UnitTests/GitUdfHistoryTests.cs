// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\GitUdfHistoryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证精确版本目录的最后修改提交、合并简化与浅边界, 不按墙钟或仓库 HEAD 猜测日期.

using PbiUtils.Infrastructure.Udf.Repositories;
using System.Text;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Udf;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>使用独立对象构造器覆盖目录级拓扑语义, 不访问真实仓库.</summary>
public sealed class GitUdfHistoryTests
{
    private const string Path = "packages/p/pql.assert/0.7.1";
    private const string PreviousPath = "packages/p/pql.assert/0.7.0";

    /// <summary>同包版本独立计时; README 修改影响整包, 无关 HEAD 与作者时间不影响版本日期.</summary>
    [Fact]
    public void VersionDirectoriesUseLastWholeTreeChangeAndCommitterTime()
    {
        var history = new GitUdfHistoryFixture();
        var files = Files();
        files[PreviousPath + "/README.md"] = "old"u8.ToArray();
        var first = history.Commit(history.Tree(files), 300, authorSeconds: 999);
        files[Path + "/README.md"] = "readme changed"u8.ToArray();
        var updated = history.Commit(history.Tree(files), 100, [first]);
        files["unrelated.txt"] = "head"u8.ToArray();
        var tree = history.Tree(files);
        var head = history.Commit(tree, 900, [updated]);
        var result = GitUdfHistoryReader.Read(history.Objects, head, tree, [Path, PreviousPath], [], default);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100), result[Path]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(300), result[PreviousPath]);
        Assert.Equal(TimeSpan.Zero, result[Path]!.Value.Offset);
    }

    /// <summary>合并采用实际同树父链, 不把合并时刻或较大墙钟时间当成更新.</summary>
    [Fact]
    public void MergeFollowsFirstSameTreeParentRatherThanMergeOrNewestTimestamp()
    {
        var history = new GitUdfHistoryFixture(); var files = Files();
        var tree = history.Tree(files); var root = history.Commit(tree, 100);
        var left = history.Commit(tree, 900, [root]);
        files[Path + "/README.md"] = "branch"u8.ToArray();
        var changedTree = history.Tree(files); var right = history.Commit(changedTree, 50, [root]);
        var merge = history.Commit(changedTree, 1000, [left, right]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(50), Read(history, merge, changedTree));
    }

    /// <summary>合并合成所有父树均不同的新目录内容时归属合并提交.</summary>
    [Fact]
    public void MergeNewDirectoryContentIsItsOwnUpdate()
    {
        var history = new GitUdfHistoryFixture(); var files = Files();
        var tree = history.Tree(files); var root = history.Commit(tree, 100);
        files[Path + "/README.md"] = "left"u8.ToArray(); var left = history.Commit(history.Tree(files), 200, [root]);
        files[Path + "/README.md"] = "right"u8.ToArray(); var right = history.Commit(history.Tree(files), 300, [root]);
        files[Path + "/README.md"] = "merged"u8.ToArray(); tree = history.Tree(files);
        var merge = history.Commit(tree, 50, [left, right]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(50), Read(history, merge, tree));
    }

    /// <summary>浅边界不能冒充根; 边界之后可证明的目录变更仍能返回真实日期.</summary>
    [Fact]
    public void ShallowBoundaryIsUnknownButLaterProvableChangeHasDate()
    {
        var history = new GitUdfHistoryFixture(); var files = Files();
        var tree = history.Tree(files); var boundary = history.Commit(tree, 100, [new string('b', 40)]);
        Assert.Null(GitUdfHistoryReader.Read(history.Objects, boundary, tree, [Path], [boundary], default)[Path]);
        files[Path + "/README.md"] = "new"u8.ToArray(); tree = history.Tree(files);
        var changed = history.Commit(tree, 200, [boundary]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), GitUdfHistoryReader.Read(history.Objects, changed, tree, [Path], [boundary], default)[Path]);
    }

    /// <summary>非浅父对象缺失和快照根树身份冲突均拒绝完整结果.</summary>
    [Fact]
    public void MissingNonShallowParentAndWrongSnapshotTreeAreInvalid()
    {
        var history = new GitUdfHistoryFixture(); var tree = history.Tree(Files());
        var head = history.Commit(tree, 100, [new string('b', 40)]);
        Assert.Throws<AppException>(() => Read(history, head, tree));
        head = history.Commit(tree, 100);
        Assert.Throws<AppException>(() => Read(history, head, new string('a', 40)));
    }

    /// <summary>损坏或无显式时区的提交者时间不被转换成未知成功.</summary>
    [Theory]
    [InlineData("not-a-date +0000")]
    [InlineData("100 +9999")]
    [InlineData("100")]
    public void MalformedCommitterTimeIsInvalid(string stamp)
    {
        var history = new GitUdfHistoryFixture(); var tree = history.Tree(Files());
        var commit = history.Add(1, Encoding.ASCII.GetBytes($"tree {tree}\ncommitter Fixture <f@example.test> {stamp}\n\nMessage\n"));
        Assert.Throws<AppException>(() => Read(history, commit, tree));
    }

    /// <summary>Git 路径大小写保持精确, 重命名按新目录创建处理, 取消不会返回部分日期.</summary>
    [Fact]
    public void ExactCasePathHasNoImplicitRenameTrackingAndCancellationPropagates()
    {
        var history = new GitUdfHistoryFixture(); var files = Files(); var oldTree = history.Tree(files);
        var root = history.Commit(oldTree, 100);
        var renamed = files.ToDictionary(pair => pair.Key.Replace("pql.assert", "PQL.Assert", StringComparison.Ordinal), pair => pair.Value);
        var tree = history.Tree(renamed); var head = history.Commit(tree, 200, [root]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), GitUdfHistoryReader.Read(history.Objects, head, tree, ["packages/p/PQL.Assert/0.7.1"], [], default).Values.Single());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => GitUdfHistoryReader.Read(history.Objects, head, tree, [Path], [], cancellation.Token));
    }

    /// <summary>Git 消息与作者可能采用非 UTF8 编码, 仅 ASCII 结构与提交时间参与日期读取.</summary>
    [Fact]
    public void NonUtf8CommitMessageAndIdentityDoNotPreventDateRead()
    {
        var history = new GitUdfHistoryFixture(); var tree = history.Tree(Files());
        var commit = history.Add(1, [.. Encoding.ASCII.GetBytes($"tree {tree}\nauthor "), 0xe9,
            .. Encoding.ASCII.GetBytes(" <f@example.test> 999 +0800\ncommitter "), 0xe9,
            .. Encoding.ASCII.GetBytes(" <f@example.test> 100 +0800\nencoding ISO-8859-1\n\n"), 0xe9]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100), Read(history, commit, tree));
    }

    /// <summary>为单一路径执行正式读取器, 所有父链必须完整.</summary>
    private static DateTimeOffset? Read(GitUdfHistoryFixture history, string head, string tree) => GitUdfHistoryReader.Read(history.Objects, head, tree, [Path], [], default)[Path];
    /// <summary>同目录多文件保证比较的是整个目录树而非仅 manifest 或函数正文.</summary>
    private static Dictionary<string, byte[]> Files() => new(StringComparer.Ordinal) { [Path + "/README.md"] = "readme"u8.ToArray(), [Path + "/lib/a.tmdl"] = "function"u8.ToArray() };
}
