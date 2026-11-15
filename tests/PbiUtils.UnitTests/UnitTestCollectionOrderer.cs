// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\UnitTestCollectionOrderer.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 优先提交实测耗时较长的独立单元测试集合.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer("PbiUtils.UnitTests.UnitTestCollectionOrderer", "PbiUtils.UnitTests")]

namespace PbiUtils.UnitTests;

/// <summary>让长集合尽早与其他单测重叠, 使用运行器原有并发位.</summary>
public sealed class UnitTestCollectionOrderer : ITestCollectionOrderer
{
    /// <summary>优先提交样例分布与真实项目配置集合, 其他集合保留输入的相对顺序.</summary>
    /// <param name="testCollections">本轮发现的测试集合.</param>
    /// <returns>xUnit 提交次序. 实际启动时刻由运行器调度.</returns>
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderByDescending(collection => collection.DisplayName is
            SampleSalesDistributionTests.CollectionName or ModelConfigurationFilesystemTests.CollectionName);
}
