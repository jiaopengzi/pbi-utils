// FilePath    : pbi-utils\tests\PbiUtils.Cli.Tests\CliTestCollectionOrderer.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 优先提交共享用户数据、发布别名及安装事务的长串行测试集合.

using PbiUtils.ScriptTests;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer("PbiUtils.Cli.Tests.CliTestCollectionOrderer", "PbiUtils.Cli.Tests")]

namespace PbiUtils.Cli.Tests;

/// <summary>减少长串行集合在短用例之后排队的时间.</summary>
public sealed class CliTestCollectionOrderer : ITestCollectionOrderer
{
    /// <summary>优先提交长串行集合, 保留其余集合的相对次序及原有并发限制.</summary>
    /// <param name="testCollections">本轮由 xUnit 发现的测试集合.</param>
    /// <returns>xUnit 使用的提交顺序. 集合内测试仍串行执行.</returns>
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderByDescending(collection => collection.DisplayName == CliUserDataCollection.Name)
            .ThenByDescending(collection => collection.DisplayName is nameof(ScriptReleaseAliasPublicationTests) or nameof(InstallerApplicationTests));
}
